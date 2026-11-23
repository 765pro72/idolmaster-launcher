r"""本地 MITM 代理（学マス离线攻关）
游戏走系统代理 → 把 CONNECT 指到本代理 → 本代理用自签证书终结 TLS →
  record 模式：转发到真实服务器（经 Clash 上行），请求/响应原文落盘
  replay 模式：不联网，按 rules 目录里的采样直接回放（离线）

用法:
  python mitm_proxy.py record F:\...\out\mitm_record
  python mitm_proxy.py replay F:\...\out\mitm_record
"""
import os
import socket
import ssl
import sys
import threading
import datetime
import glob

HERE = os.path.dirname(os.path.abspath(__file__))
CERTS = os.path.join(HERE, 'certs')
LISTEN = ('127.0.0.1', 7899)
# 上游代理：支持 "host:port" 字符串（环境变量串味时也不会炸），空 = 直连
_up = os.environ.get('GKM_UPSTREAM_PROXY') or ''
UPSTREAM = None
if ':' in _up:
    _h, _prt = _up.rsplit(':', 1)
    try:
        UPSTREAM = (_h, int(_prt))
    except Exception:
        UPSTREAM = None
INTERCEPT = (
    'api.asset.game-gakuen-idolmaster.jp',
    'securetoken.googleapis.com',
    'identitytoolkit.googleapis.com',
    'api.store.games.dmm.com',
    'www.googleapis.com',
    'firebasestorage.googleapis.com',
    'oauth2.googleapis.com',
)
# 通配后缀：DMM SDK 会连多级域名（ae.recibo.games.dmm.com 这种五段式，
# 单层通配符证书/精确匹配都盖不住），断网时若不接住会让 IAP 初始化失败。
INTERCEPT_SUFFIX = (
    '.game-gakuen-idolmaster.jp',
    '.googleapis.com',
    '.gstatic.com',
    '.googleusercontent.com',
    '.dmm.com',
    '.dmm.co.jp',
)


# 这些域名必须走真实网络：游戏的 master 文件从 stat 下载，
# 拦成 200 {} 会让客户端报「file size is different」-> 获取数据失败。
DIRECT_HOSTS = (
    'stat.game-gakuen-idolmaster.jp',
    'api.game-gakuen-idolmaster.jp',
    # 资源服务器（Live / MV 资产）。UnityWebRequest 走 Unity 自带的 TLS 栈，
    # 不读 Windows 根证书，拦下来只会 TLS ACCESS_DENIED -> E203 获取资产失败。
    'object.asset.game-gakuen-idolmaster.jp',
    'asset.game-gakuen-idolmaster.jp',
)


def is_intercepted(h):
    h = (h or '').lower()
    if h in DIRECT_HOSTS:
        return False
    if h in INTERCEPT:
        return True
    return any(h.endswith(s) for s in INTERCEPT_SUFFIX)

MAX_BODY = 16 * 1024 * 1024

MODE = sys.argv[1] if len(sys.argv) > 1 else 'record'
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, 'mitm_out')
os.makedirs(OUT, exist_ok=True)
LOG = os.path.join(OUT, 'mitm_capture.log')
_lock = threading.Lock()
_seq = [0]


def log(msg):
    line = '[%s] %s' % (datetime.datetime.now().strftime('%H:%M:%S.%f')[:-3], msg)
    with _lock:
        with open(LOG, 'a', encoding='utf-8') as f:
            f.write(line + '\n')


def read_until(sock, marker, cap=1 << 20):
    buf = b''
    while marker not in buf:
        chunk = sock.recv(4096)
        if not chunk:
            break
        buf += chunk
        if len(buf) > cap:
            break
    return buf


def read_head(sock, cap=1 << 20):
    """读 HTTP 头，返回 (head, 已多读的剩余字节)。
    recv 会把 body 一起吞进来，必须把剩余字节交给调用方，否则 body 会丢。"""
    buf = b''
    while b'\r\n\r\n' not in buf:
        chunk = sock.recv(4096)
        if not chunk:
            break
        buf += chunk
        if len(buf) > cap:
            break
    i = buf.find(b'\r\n\r\n')
    if i < 0:
        return buf, b''
    return buf[:i + 4], buf[i + 4:]


def read_chunked_pre(sock, pre, cap=MAX_BODY):
    """带「已多读字节」的 chunked 读取，返回原始 chunk 帧。"""
    buf = pre
    body = b''
    while True:
        while b'\r\n' not in buf:
            c = sock.recv(4096)
            if not c:
                return body
            buf += c
        line, buf = buf.split(b'\r\n', 1)
        body += line + b'\r\n'
        try:
            size = int(line.split(b';')[0].strip(), 16)
        except Exception:
            return body
        if size == 0:
            while b'\r\n' not in buf:
                c = sock.recv(4096)
                if not c:
                    return body
                buf += c
            t, buf = buf.split(b'\r\n', 1)
            body += t + b'\r\n'
            return body
        need = size + 2
        while len(buf) < need:
            c = sock.recv(65536)
            if not c:
                return body
            buf += c
        body += buf[:need]
        buf = buf[need:]
        if len(body) > cap:
            return body


def read_exact(sock, n):
    buf = b''
    while len(buf) < n:
        chunk = sock.recv(min(65536, n - len(buf)))
        if not chunk:
            break
        buf += chunk
    return buf


def read_chunked(sock, cap=MAX_BODY):
    body = b''
    while True:
        line = read_until(sock, b'\r\n', 4096)
        if not line:
            break
        body += line
        try:
            size = int(line.split(b';')[0].strip(), 16)
        except Exception:
            break
        if size == 0:
            body += read_until(sock, b'\r\n', 65536)
            break
        body += read_exact(sock, size + 2)
        if len(body) > cap:
            break
    return body


def parse_headers(block):
    txt = block.decode('latin1')
    lines = txt.split('\r\n')
    first = lines[0] if lines else ''
    hdrs = []
    for ln in lines[1:]:
        if ln and ':' in ln:
            k, v = ln.split(':', 1)
            hdrs.append((k.strip(), v.strip()))
    return first, hdrs


def hget(hdrs, name, default=None):
    for k, v in hdrs:
        if k.lower() == name.lower():
            return v
    return default


def save(dirpath, name, data):
    with open(os.path.join(dirpath, name), 'wb') as f:
        f.write(data)


def tls_server_ctx():
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(os.path.join(CERTS, 'chain.pem'), os.path.join(CERTS, 'leaf.key'))
    ctx.set_alpn_protocols(['http/1.1'])
    return ctx


def _open_upstream_route(host, proxy, verify):
    up = None
    try:
        up = socket.create_connection(proxy or (host, 443), timeout=15)
        if proxy:
            up.sendall(('CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n' % (host, host)).encode())
            head = read_until(up, b'\r\n\r\n', 65536)
            if b' 200' not in head.split(b'\r\n')[0]:
                raise RuntimeError('upstream CONNECT failed')
        ctx = ssl.create_default_context()
        if not verify:
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
        ctx.set_alpn_protocols(['http/1.1'])
        return ctx.wrap_socket(up, server_hostname=host)
    except Exception:
        if up is not None:
            up.close()
        raise


def open_upstream(host, verify=True):
    try:
        return _open_upstream_route(host, UPSTREAM, verify)
    except (OSError, RuntimeError) as error:
        if MODE != 'forward' or not UPSTREAM:
            raise
        # Only retry the TLS connection. No HTTP request or login payload has
        # been sent at this point, so POST requests cannot be submitted twice.
        log('FORWARD route fallback %s proxy-to-direct reason=%s' %
            (host, type(error).__name__))
        return _open_upstream_route(host, None, verify)


def record_exchange(tag, host, method, target, head, body, tls, chunked=False, persist=True):
    if persist:
        d = os.path.join(OUT, tag)
        os.makedirs(d, exist_ok=True)
        save(d, 'req.txt', head)
        if body:
            save(d, 'req.bin', body)
    tup = open_upstream(host, verify=not persist)
    out = ['%s %s HTTP/1.1' % (method, target)]
    for k, v in parse_headers(head)[1]:
        if k.lower() in ('connection', 'proxy-connection', 'keep-alive',
                         'transfer-encoding', 'content-length', 'expect'):
            continue
        out.append('%s: %s' % (k, v))
    if chunked:
        out.append('Transfer-Encoding: chunked')     # body 已是原始 chunk 帧
    elif body:
        out.append('Content-Length: %d' % len(body))
    out.append('Connection: close')
    tup.sendall(('\r\n'.join(out) + '\r\n\r\n').encode('latin1') + body)

    rhead, rrest = read_head(tup)
    rf, rh = parse_headers(rhead)
    te = (hget(rh, 'Transfer-Encoding') or '').lower()
    cl = hget(rh, 'Content-Length')
    if 'chunked' in te:
        rbody = read_chunked_pre(tup, rrest)
    elif cl:
        need = int(cl)
        rbody = rrest[:need]
        if len(rbody) < need:
            rbody += read_exact(tup, need - len(rbody))
    else:
        rbody = rrest
        while True:
            c = tup.recv(65536)
            if not c:
                break
            rbody += c
            if len(rbody) > MAX_BODY:
                break
    tup.close()
    if persist:
        save(d, 'res.txt', rhead)
        if rbody:
            save(d, 'res.bin', rbody)
        log('%s %s %s -> %s (req=%d res=%d)' % (tag, method, target, rf, len(body), len(rbody)))
    else:
        log('FORWARD %s %s -> %s (req=%d res=%d)' % (host, method, rf, len(body), len(rbody)))
    return rhead, rbody


def synthetic(host, method, target):
    """离线夹具：不联网，直接按 host/path 返回预置响应。"""
    fix = os.environ.get('GKM_FIXTURE_DIR') or os.path.join(HERE, 'offline')
    if not os.path.isdir(fix):
        fix = os.path.join(HERE, '..', 'out', 'offline')
    if not os.path.isdir(fix):
        return None

    def read(name):
        p = os.path.join(fix, name)
        return open(p, 'rb').read() if os.path.exists(p) else b''

    def ok(ctype, body):
        return (('HTTP/1.1 200 OK\r\nContent-Type: %s\r\nContent-Length: %d\r\n'
                 'Cache-Control: no-cache\r\nConnection: close\r\n\r\n'
                 % (ctype, len(body))).encode('latin1'), body)

    h = host.lower()
    if h == 'api.asset.game-gakuen-idolmaster.jp' and '/v2/pub/' in target:
        from pathlib import Path
        from octo_catalog import reply
        route = target.split('?', 1)[0]
        try:
            path = Path(HERE).parent / 'game_path.txt'
            root = Path(path.read_text('utf-8-sig').strip().strip('"'))
            catalog = reply(route, read('octo_list.bin'), root)
        except (OSError, ValueError) as error:
            log('OCTO catalog unavailable %s: %s' % (route, error))
            catalog = None
        if catalog:
            log('SYNTH Octo no-change %s -> %d B' % (route, len(catalog)))
            return ok('application/x-protobuf', catalog)
        log('OCTO catalog unavailable for %s; local installation needs an asset catalog' % route)
        return (b'HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n', b'')
    if h == 'securetoken.googleapis.com' and target.startswith('/v1/token'):
        b = read('firebase_token.json')
        if b:
            log('SYNTH firebase token -> %d B' % len(b))
            return ok('application/json; charset=UTF-8', b)
    if h in ('identitytoolkit.googleapis.com', 'www.googleapis.com'):
        # Fixed public package fixtures, not a profile manager or account import.
        # Match only known API routes; never invent a fresh device UUID on launch.
        path = target.split('?', 1)[0]
        name = {
            '/v1/accounts:signInWithCustomToken': 'firebase_signin.json',
            '/v1/accounts:signUp': 'firebase_signup.json',
            '/v1/accounts:lookup': 'firebase_lookup.json',
            '/identitytoolkit/v3/relyingparty/verifyCustomToken': 'firebase_signin.json',
            '/identitytoolkit/v3/relyingparty/signupNewUser': 'firebase_signup.json',
            '/identitytoolkit/v3/relyingparty/getAccountInfo': 'firebase_lookup.json',
        }.get(path)
        if name:
            b = read(name)
            if b:
                log('SYNTH firebase local fixture -> %d B' % len(b))
                return ok('application/json; charset=UTF-8', b)
    if h == 'identitytoolkit.googleapis.com':
        log('SYNTH identitytoolkit %s -> {}' % target)
        return ok('application/json; charset=UTF-8', b'{}')
    if h.startswith('api.store.games.dmm.com') or h.endswith('.dmm.com') or h.endswith('.dmm.co.jp') or h == 'dmm.com':
        log('SYNTH dmm %s %s -> 200 {}' % (h, target))
        return ok('application/json; charset=UTF-8', b'{}')
    if h.endswith('googleapis.com'):
        log('SYNTH misc googleapis %s -> 200 empty' % target)
        return ok('application/json; charset=UTF-8', b'{}')
    # 兜底：其余被拦截的域名一律回 200 {}，避免 SDK 因为 404/无响应卡住启动
    if is_intercepted(h):
        log('SYNTH default %s %s -> 200 {}' % (h, target))
        return ok('application/json; charset=UTF-8', b'{}')
    return None


def replay_exchange(host, method, target, head):
    """按 host+path 找已采样目录，回放 res.txt/res.bin；找不到就 404。"""
    syn = synthetic(host, method, target)
    if syn:
        return syn
    want = host.lower()
    best = None
    for d in sorted(glob.glob(os.path.join(OUT, '*'))):
        if not os.path.isdir(d):
            continue
        rt = os.path.join(d, 'req.txt')
        if not os.path.exists(rt):
            continue
        try:
            rfirst, rh = parse_headers(open(rt, 'rb').read())
        except Exception:
            continue
        h = (hget(rh, 'Host') or '').split(':')[0].lower()
        if h != want:
            continue
        if target in rfirst:
            best = d
            break
    if not best:
        log('MISS %s %s' % (host, target))
        return (b'HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n', b'')
    rhead = open(os.path.join(best, 'res.txt'), 'rb').read()
    rbody = b''
    rb = os.path.join(best, 'res.bin')
    if os.path.exists(rb):
        rbody = open(rb, 'rb').read()
    log('HIT %s %s -> %s (%d B body)' % (host, target, best, len(rbody)))
    return rhead, rbody


def serve_tls(conn, host):
    ctx = tls_server_ctx()
    try:
        tls = ctx.wrap_socket(conn, server_side=True)
    except Exception as e:
        log('TLS terminate failed for %s: %r' % (host, e))
        conn.close()
        return
    try:
        while True:
            head, rest = read_head(tls)
            if not head:
                break
            first, hdrs = parse_headers(head)
            try:
                method, target, _v = first.split(' ', 2)
            except ValueError:
                break
            cl = hget(hdrs, 'Content-Length')
            te = (hget(hdrs, 'Transfer-Encoding') or '').lower()
            expect = (hget(hdrs, 'Expect') or '').lower()
            chunked = 'chunked' in te
            body = b''
            if expect and '100-continue' in expect:
                tls.sendall(b'HTTP/1.1 100 Continue\r\n\r\n')
            if cl:
                need = int(cl)
                body = rest[:need]
                if len(body) < need:
                    body += read_exact(tls, need - len(body))
            elif chunked:
                body = read_chunked_pre(tls, rest)   # 原始 chunk 帧
            elif rest:
                body = rest
            with _lock:
                _seq[0] += 1
                n = _seq[0]
            tag = '%03d_%s_%s' % (n, host.split('.')[0], method)
            if MODE in ('record', 'forward'):
                try:
                    rhead, rbody = record_exchange(tag, host, method, target, head, body, tls, chunked, persist=(MODE == 'record'))
                except Exception as e:
                    log('%s upstream error %r' % (tag, e))
                    tls.sendall(b'HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n')
                    break
            else:
                rhead, rbody = replay_exchange(host, method, target, head)
            keep = [ln for ln in rhead.decode('latin1').split('\r\n')
                    if ln and not ln.lower().startswith('connection:')]
            tls.sendall(('\r\n'.join(keep) + '\r\nConnection: close\r\n\r\n').encode('latin1') + rbody)
            break   # 一个连接只处理一个请求（已用 Connection: close）
    except Exception as e:
        log('serve error %r' % (e,))
    finally:
        try:
            tls.close()
        except Exception:
            pass


def tunnel(conn, host):
    """非拦截域名：透明转发到上游（不解密）。"""
    log('TUNNEL %s' % host)
    try:
        if UPSTREAM:
            up = socket.create_connection(UPSTREAM, timeout=15)
            up.sendall(('CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n' % (host, host)).encode())
            head = read_until(up, b'\r\n\r\n', 65536)
            if b' 200' not in head.split(b'\r\n')[0]:
                conn.close(); up.close(); return
        else:
            up = socket.create_connection((host, 443), timeout=15)
        conn.sendall(b'HTTP/1.1 200 Connection established\r\n\r\n')

        def pump(a, b):
            try:
                while True:
                    d = a.recv(65536)
                    if not d:
                        break
                    b.sendall(d)
            except Exception:
                pass
            finally:
                try: b.shutdown(socket.SHUT_WR)
                except Exception: pass

        t = threading.Thread(target=pump, args=(conn, up), daemon=True)
        t.start()
        pump(up, conn)
        t.join(timeout=1)
    except Exception as e:
        log('TUNNEL-FAIL %s %r' % (host, e))
    finally:
        try: conn.close()
        except Exception: pass
        try: up.close()
        except Exception: pass


def handle(conn):
    head = read_until(conn, b'\r\n\r\n', 65536)
    if not head:
        conn.close(); return
    first, hdrs = parse_headers(head)
    if not first.upper().startswith('CONNECT'):
        # 普通 HTTP（极少见）：直接 200 空响应，避免卡住
        conn.sendall(b'HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n')
        conn.close(); return
    try:
        target = first.split(' ')[1]
        host = target.split(':')[0]
    except Exception:
        conn.close(); return
    if is_intercepted(host):
        conn.sendall(b'HTTP/1.1 200 Connection established\r\n\r\n')
        serve_tls(conn, host.lower())
    else:
        tunnel(conn, host)


def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind(LISTEN)
    except OSError as e:
        log('FATAL 无法监听 %s:%d - %s' % (LISTEN[0], LISTEN[1], e))
        log('      端口被占用。请关闭其他代理软件，或杀掉残留的 python.exe')
        sys.exit(3)
    srv.listen(128)
    log('LISTEN %s:%d mode=%s out=%s intercept=%d' % (LISTEN[0], LISTEN[1], MODE, OUT, len(INTERCEPT)))
    while True:
        conn, _ = srv.accept()
        threading.Thread(target=handle, args=(conn,), daemon=True).start()


if __name__ == '__main__':
    main()
