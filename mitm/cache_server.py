#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
gakumas gRPC 缓存/回放服务器
=============================
双模式：
  online  : 转发到官方 + 缓存「请求指纹 -> 响应」
  offline : 只从缓存回放（不联网）

请求指纹 = path + sha1(请求体)
  - 同一 API 不同参数会得到不同指纹，因此可缓存多组响应
  - 未命中时：online 模式转发官方并记录；offline 模式返回 gRPC 错误

缓存布局：
  cache/<sanitized_path>/<fingerprint>.json
"""
import os, sys, ssl, json, time, socket, hashlib, threading, traceback, datetime
import h2.connection, h2.events, h2.config, h2.errors
import http.client
from collections import deque

# === 运行时探针（probe.py，缺失则降级为 None，不影响主流程）===
PROBE = None
try:
    import probe as PROBE
except Exception:
    PROBE = None


def _pnote(seq, branch, body=None, status=200, extra=None):
    """把一次请求的收尾记进探针；探针不可用时静默跳过。"""
    if PROBE is None or not seq:
        return
    try:
        PROBE.note(seq, branch, body, status, extra)
    except Exception:
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
LEAF_CERT = os.path.join(HERE, 'leaf_c2c8_chain.pem')
LEAF_KEY  = os.path.join(HERE, 'leaf_c2c8.key')
OUTDIR    = os.path.join(HERE, 'records')
CACHEDIR  = os.path.join(HERE, 'cache')
os.makedirs(OUTDIR, exist_ok=True)
os.makedirs(CACHEDIR, exist_ok=True)

# ==== 全解锁模块（动态合并，保留账号自身数据）====
# 这些 API 必须每次实时转发：包含账号私有数据，缓存会串号
NO_CACHE_PATHS = ('/client.api.User/Get',)

UNLOCKER = None
_CONTENT_RELOADER = None
_PRODUCE_LOCK = threading.Lock()
try:
    import unlock as _unlock_mod
    # 分发包模式：不带 masterTrans，只靠 static_userget.bin
    _mt_candidates = [
        os.path.normpath(os.path.join(HERE, 'masterTrans')),
        os.path.normpath(os.path.join(HERE, '..', 'ref', 'gkms-local', 'extracted',
                                      'gakumas-local', 'local-files', 'masterTrans')),
    ]
    _mt = next((p for p in _mt_candidates if os.path.isdir(p)), None)
    UNLOCKER = _unlock_mod.Unlocker(_mt)
except Exception as _e:
    UNLOCKER = None

UPSTREAM_HOST = 'api.game-gakuen-idolmaster.jp'
UPSTREAM_PORT = 443
LISTEN_PORT   = int(os.environ.get('GKM_MITM_PORT', '18443'))
# mode: online (forward+cache) | offline (cache-only) | hybrid (cache-first, miss->forward)
MODE = os.environ.get('GKM_MODE', 'online').lower()
DUMP_PRODUCE = os.environ.get('GKM_DUMP_PRODUCE') == '1'

# ---- fake server time (for birthday / event replay) ----
# GKM_FAKE_TIME 可以是:
#   YYYY-MM-DD        某一天
#   MM-DD             今年的某月某日
#   13位毫秒时间戳     原样使用
# 留空 = 不伪造
FAKE_TIME_RAW = os.environ.get('GKM_FAKE_TIME', '').strip()


def _fake_server_time_ms():
    if not FAKE_TIME_RAW:
        return None
    try:
        s = FAKE_TIME_RAW
        if s.isdigit() and len(s) >= 12:
            return int(s)
        if len(s) == 10 and s[4] == '-':
            d = datetime.datetime.strptime(s, '%Y-%m-%d')
            return int(d.timestamp() * 1000)
        if len(s) == 5 and s[2] == '-':
            y = datetime.datetime.now().year
            d = datetime.datetime.strptime('%d-%s' % (y, s), '%Y-%m-%d')
            return int(d.timestamp() * 1000)
    except Exception as e:
        log('FAKE_TIME parse error: %s' % e)
    return None


def _time_headers():
    ms = _fake_server_time_ms()
    if ms is None:
        # Cached response timestamps must not move the offline client back
        # before newly released skins/music. Explicit fake time still wins.
        if MODE in ('offline', 'hybrid'):
            ms = int(time.time() * 1000)
        else:
            return []
    return [('x-server-time', str(ms))]


LOG = os.path.join(HERE, 'mitm.log')
_lock = threading.Lock()
_seq = [0]

def log(*a):
    line = ' '.join(str(x) for x in a)
    ts = datetime.datetime.now().strftime('%H:%M:%S.%f')[:-3]
    with _lock:
        print(f'[{ts}] {line}', flush=True)
        try:
            with open(LOG, 'a', encoding='utf-8') as f:
                f.write(f'[{ts}] {line}\n')
        except Exception:
            pass


def send_grpc_data(conn, tls, stream_id, data):
    """All response paths share the same frame and flow-control handling."""
    send_body_with_flow(conn, tls, stream_id, data)


def next_id():
    with _lock:
        _seq[0] += 1
        return _seq[0]

def sanitize(path):
    s = path.strip('/').replace('/', '_')
    bad = '<>:"\\|?*'
    for c in bad:
        s = s.replace(c, '_')
    return s[:150] or 'root'

def fingerprint(path, body):
    h = hashlib.sha1()
    h.update(path.encode('utf-8', 'replace'))
    h.update(b'\x00')
    h.update(body)
    return h.hexdigest()[:16]

def cache_path(path, fp):
    d = os.path.join(CACHEDIR, sanitize(path))
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, fp + '.json')


# ==== FORGE: 按路径返回预置响应（优先级最高，热重载）====
FORGE_DIR = os.path.join(HERE, 'forge')
os.makedirs(FORGE_DIR, exist_ok=True)
_forge_cache = {'mtime': 0.0, 'map': {}}

_MASTER_MANAGER = None
def master_manager():
    global _MASTER_MANAGER
    if _MASTER_MANAGER is None:
        from master_lifecycle import ActiveMaster
        _MASTER_MANAGER=ActiveMaster(os.path.dirname(HERE),_unlock_mod,log)
    return _MASTER_MANAGER


def offline_master_response():
    from local_costume_master import response
    return response(HERE, master_manager().response())

def forge_lookup(path):
    """forge/index.json: {"<api path>": "<filename>"}，文件放在 forge/ 下"""
    try:
        idx = os.path.join(FORGE_DIR, 'index.json')
        if not os.path.isfile(idx):
            return None
        mt = os.path.getmtime(idx)
        if mt != _forge_cache['mtime']:
            with open(idx, encoding='utf-8') as f:
                _forge_cache['map'] = json.load(f)
            _forge_cache['mtime'] = mt
            log('FORGE index reloaded:', list(_forge_cache['map'].keys()))
        fn = _forge_cache['map'].get(path)
        if not fn:
            return None
        fp = os.path.join(FORGE_DIR, fn)
        if not os.path.isfile(fp):
            return None
        with open(fp, 'rb') as f:
            return f.read()
    except Exception as e:
        log('FORGE err', type(e).__name__, e)
        return None

def cache_lookup(path, body):
    """返回 (hit, entry)。
    策略由 GKM_MATCH 控制:
      exact : path + sha1(请求体)  —— 严格匹配（在线缓存用）
      path  : 只按 API 路径匹配     —— 离线回放用（请求体含 session，必然变化）
    """
    if path == '/client.api.StoryEvent/Top':
        from activity_cache import replay_detail
        from pathlib import Path
        frame = replay_detail(Path(HERE) / 'offline-fallbacks',
                              Path(CACHEDIR) / sanitize(path), body)
        if frame is None:
            return False, None
        return True, {'status': 200, 'resp_body_hex': frame.hex(), 'fingerprint': 'activity-id'}

    mode = os.environ.get('GKM_MATCH', 'exact').lower()
    if mode == 'path':
        d = os.path.join(CACHEDIR, sanitize(path))
        if os.path.isdir(d):
            files = [f for f in os.listdir(d) if f.endswith('.json')]
            if files:
                # 优先选最近写入的（通常是最后一个成功变体）
                files.sort(key=lambda f: os.path.getmtime(os.path.join(d, f)), reverse=True)
                for f in files:
                    try:
                        return True, json.load(open(os.path.join(d, f), encoding='utf-8'))
                    except Exception:
                        continue
        return False, None
    # exact
    fp = fingerprint(path, body)
    p = cache_path(path, fp)
    if os.path.isfile(p):
        try:
            return True, json.load(open(p, encoding='utf-8'))
        except Exception:
            return False, None
    return False, None

def cache_store(path, body, resp_headers, resp_body, status):
    fp = fingerprint(path, body)
    p = cache_path(path, fp)
    entry = {
        'path': path, 'fingerprint': fp, 'status': status,
        'resp_headers': resp_headers, 'resp_body_hex': resp_body.hex(),
        'resp_body_len': len(resp_body),
        'req_body_len': len(body), 'req_body_hex': body.hex(),
        'cached_at': datetime.datetime.now().isoformat(),
    }
    tmp = p + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(entry, f, ensure_ascii=False, indent=1)
    os.replace(tmp, p)
    return fp

# 上游代理：游戏锁日服 IP，MITM 转发给官方时必须走 Clash，否则连不上。
# 格式 "host:port"，留空则直连。默认用 Clash 的 mixed 端口。
# 端口不写死：不同机器 Clash 可能用 7897 / 7890 / 7900，优先读启动前
# 保存的真实系统代理（proxy.saved.json），再退回 7897。
_ENV_UPSTREAM = os.environ.get('GKM_UPSTREAM_PROXY')


def _detect_upstream_proxy():
    try:
        with open(os.path.join(HERE, 'proxy.saved.json'), encoding='utf-8') as f:
            s = (json.load(f) or {}).get('Server') or ''
        if ':' in s:
            return s
    except Exception:
        pass
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r'Software\Microsoft\Windows\CurrentVersion\Internet Settings') as k:
            s = str(winreg.QueryValueEx(k, 'ProxyServer')[0] or '')
        if ':' in s:
            return s
    except Exception:
        pass
    return ''


# 显式设置（含离线哨兵 127.0.0.1:1）一律尊重；仅当未设置时才自动探测，
# 这样 online 抓取模式不会再因为 Clash 换端口而整条链路失败。
UPSTREAM_PROXY = _ENV_UPSTREAM or (_detect_upstream_proxy() or '127.0.0.1:7897')


def _make_upstream_conn(timeout):
    """建立到官方的连接；若配置了代理则走 HTTP CONNECT 隧道。"""
    ctx = ssl.create_default_context()
    if UPSTREAM_PROXY:
        ph, _, pp = UPSTREAM_PROXY.partition(':')
        conn = http.client.HTTPSConnection(ph, int(pp or 7897), timeout=timeout, context=ctx)
        conn.set_tunnel(UPSTREAM_HOST, UPSTREAM_PORT)
        return conn
    return http.client.HTTPSConnection(UPSTREAM_HOST, UPSTREAM_PORT, timeout=timeout, context=ctx)


def forward_grpc(path, headers, body, timeout=25):
    conn = _make_upstream_conn(timeout)
    drop = ('host','connection','content-length','transfer-encoding','keep-alive',
            'proxy-connection','upgrade','te')
    fwd = {}
    for k, v in headers.items():
        lk = k.lower()
        if lk.startswith(':') or lk in drop:
            continue
        fwd[lk] = v
    fwd['host'] = UPSTREAM_HOST
    fwd['te'] = 'trailers'
    try:
        conn.request('POST', path, body=body, headers=fwd)
        r = conn.getresponse()
        rb = r.read()
        return r.status, {k: v for k, v in r.getheaders()}, rb
    finally:
        try: conn.close()
        except Exception: pass


def send_body_with_flow(conn, tls, stream_id, data):
    """分片发送响应体，遵守 HTTP/2 帧大小上限与流控窗口"""
    if not data:
        return
    MAXF = conn.max_outbound_frame_size
    pos = 0
    total = len(data)
    guard = 0
    while pos < total:
        guard += 1
        if guard > 200000:
            raise Exception('flow control stuck at %d/%d' % (pos, total))
        win = conn.local_flow_control_window(stream_id)
        if win <= 0:
            # 需要等客户端的 WINDOW_UPDATE
            tls.sendall(conn.data_to_send())
            d = tls.recv(65536)
            if not d:
                raise Exception('client closed while awaiting window update')
            # WINDOW_UPDATE can arrive with another request on this connection.
            # Retain those events for the main dispatcher rather than losing it.
            events = conn.receive_data(d)
            pending = conn._gkm_pending_events
            for event in events:
                if isinstance(event, h2.events.DataReceived):
                    conn.acknowledge_received_data(event.flow_controlled_length, event.stream_id)
                    event._gkm_flow_acknowledged = True
                pending.append(event)
            continue
        n = min(MAXF, win, total - pos)
        if n <= 0:
            n = 1
        conn.send_data(stream_id, data[pos:pos + n], end_stream=False)
        pos += n
        tls.sendall(conn.data_to_send())



def get_tamper_cfg():
    """每次请求时读 tamper.cfg，无需重启服务器"""
    try:
        with open(os.path.join(HERE, 'tamper.cfg'), encoding='utf-8') as f:
            cfg = {}
            for line in f:
                line = line.strip()
                if not line or line.startswith('#') or '=' not in line:
                    continue
                k, v = line.split('=', 1)
                cfg[k.strip()] = v.strip()
            return (cfg.get('mode', ''), cfg.get('path', ''),
                    int(cfg.get('step', '2') or 2), int(cfg.get('size', '2') or 2),
                    int(cfg.get('start', '0') or 0),
                    int(cfg.get('fixed', '-1') or -1),
                    int(cfg.get('xor', '255') or 255))
    except Exception:
        return '', '', 2, 2, 0, -1, 255




# ==================== 字节翻转探测引擎 ====================
import time as _time

_PROBE = {
    "offset": 0,        # 当前探测的 payload 内偏移
    "step": 2,          # 每次前进的字节数
    "size": 2,          # 每次翻转的字节数
    "log": [],          # (path, offset, time)
    "max_offset": 0,    # 自动探测到的 payload 上限
    "last": {},         # path -> last req time (用于检测重试)
    "retries": [],      # 判定为「失败重试」的记录
}


def probe_apply(payload: bytes, path: str):
    """每次请求翻转下一个位置，并记录；返回 (新payload, offset)"""
    st = _PROBE
    start = st.get("start", 0)
    step = st.get("step", 2)
    size = st.get("size", 2)
    xv = st.get("xor", 0xFF)

    if step == 0:                      # 固定 offset 模式
        off = st.get("fixed", start)
    else:
        off = st["offset"]
        if off < start:
            off = start
        if off + size > len(payload):
            st["offset"] = start       # 回绕到 start
            off = start
        st["offset"] = off + step

    if len(payload) > st["max_offset"]:
        st["max_offset"] = len(payload)

    p = bytearray(payload)
    for i in range(off, min(off + size, len(payload))):
        p[i] ^= xv
    now = _time.time()
    # 重试检测：同一 path 在 4 秒内再次请求 = 上一次可能失败
    prev = st["last"].get(path)
    if prev is not None and (now - prev) < 4.0:
        st["retries"].append((path, off, round(now - prev, 2)))
    st["last"][path] = now
    st["log"].append((path, off, round(now, 3)))
    st["offset"] = off + st["step"]
    return bytes(p), off


def probe_dump():
    st = _PROBE
    lines = []
    lines.append(f"# probe log  entries={len(st['log'])}  maxPayload={st['max_offset']}")
    lines.append("# every applied flip:")
    for path, off, t in st["log"]:
        lines.append(f"  flip offset={off:<6} path={path}")
    lines.append("")
    lines.append(f"# retry-suspected ({len(st['retries'])}):  (same API called again within 4s)")
    for path, off, dt in st["retries"]:
        lines.append(f"  RETRY after flip offset={off:<6} dt={dt}s  path={path}")
    return "\n".join(lines)


def handle_client(sock, addr, sslctx, use_tls=True):
    cid = next_id()
    if use_tls:
        try:
            tls = sslctx.wrap_socket(sock, server_side=True)
        except Exception as e:
            log(f'#{cid} TLS fail: {type(e).__name__}: {e}')
            try: sock.close()
            except Exception: pass
            return
    else:
        tls = sock   # 明文 h2c，直接收发
        # 诊断：先偷看一下客户端发来的是什么协议
        try:
            tls.settimeout(3.0)
            _peek = tls.recv(64, socket.MSG_PEEK)
            log(f'#{cid} H2C peek[{len(_peek)}]: {_peek[:64].hex()}')
            try:
                _txt = _peek.decode('ascii', 'replace').replace(chr(13), '\\r').replace(chr(10), '\\n')
                log(f'#{cid} H2C peek text: {_txt!r}')
            except Exception:
                pass
            tls.settimeout(None)
        except Exception as _e:
            log(f'#{cid} H2C peek failed: {type(_e).__name__}: {_e}')
    try:
        log(f'#{cid} {'TLS' if use_tls else 'H2C'} OK from {addr}')
        cfg = h2.config.H2Configuration(client_side=False, header_encoding='utf-8')
        conn = h2.connection.H2Connection(config=cfg)
        conn._gkm_pending_events = deque()
        conn.initiate_connection()
        tls.sendall(conn.data_to_send())

        streams = {}
        while True:
            if conn._gkm_pending_events:
                events = list(conn._gkm_pending_events)
                conn._gkm_pending_events.clear()
            else:
                data = tls.recv(65536)
                if not data:
                    log(f'#{cid} closed')
                    break
                try:
                    events = conn.receive_data(data)
                except Exception as e:
                    log(f'#{cid} recv err {e}'); break

            for ev in events:
                if isinstance(ev, h2.events.RequestReceived):
                    hdrs = {}; path = ''
                    for k, v in ev.headers:
                        hdrs[k] = v
                        if k == ':path': path = v
                    streams[ev.stream_id] = {'headers': hdrs, 'path': path, 'body': b''}
                    log(f'#{cid} REQ {path}')

                elif isinstance(ev, h2.events.DataReceived):
                    st = streams.get(ev.stream_id)
                    if st is not None: st['body'] += ev.data
                    if not getattr(ev, '_gkm_flow_acknowledged', False):
                        conn.acknowledge_received_data(ev.flow_controlled_length, ev.stream_id)

                elif isinstance(ev, h2.events.StreamReset):
                    streams.pop(ev.stream_id, None)

                elif isinstance(ev, h2.events.StreamEnded):
                    st = streams.pop(ev.stream_id, None)
                    if st is None: continue
                    path, body = st['path'], st['body']
                    log(f'#{cid} BODY {path} len={len(body)}')
                    _pseq = None
                    if PROBE is not None:
                        try:
                            _pseq = PROBE.begin(cid, path, body_len=len(body), headers=st['headers'])
                            if _pseq:
                                PROBE.note_req(_pseq, body)
                        except Exception:
                            _pseq = None
                    if 'PictureBookLive' in path:
                        try:
                            log(f'#{cid} PBLHEX {path} = {body.hex()}')
                        except Exception:
                            pass
                    # 培育诊断：记录每个 Produce 请求（截断 hex，避免日志爆掉）
                    if MODE != 'online' and DUMP_PRODUCE and path.startswith('/client.api.Produce/'):
                        try:
                            with open(os.path.join(HERE, 'produce-trace.log'), 'a', encoding='utf-8') as _pt:
                                _pt.write('%s %s len=%d hex=%s\n' % (
                                    datetime.datetime.now().isoformat(timespec='seconds'),
                                    path, len(body), body[:800].hex()))
                        except Exception:
                            pass
                    if any(k in path for k in ('/Costume/Set', '/Home/SetCharacters', '/Story/Read', '/Story/Unlock', '/PictureBookLive', '/Preference/Update', '/Tutorial/Progress')):
                        try:
                            log(f'#{cid} BODYHEX {path} = {body.hex()}')
                        except Exception:
                            pass


                    if MODE != 'online' and _CONTENT_RELOADER is not None:
                        _CONTENT_RELOADER.refresh()

                    if MODE != 'online' and UNLOCKER is not None and path.startswith('/client.api.Produce/'):
                        import produce_runtime
                        _produce_method = path.rstrip('/').removeprefix(produce_runtime.PREFIX)
                        if _produce_method in produce_runtime.METHODS:
                            response_headers = [(':status', '200'), ('content-type', 'application/grpc')] + _time_headers()
                            try:
                                with _PRODUCE_LOCK:
                                    if not UNLOCKER.handle_write(path, body):
                                        raise produce_runtime.ProgressError('Invalid local Produce request')
                                    local_body = UNLOCKER.build_write_response(path)
                            except produce_runtime.ProgressError as error:
                                log(f'#{cid} PRODUCE REJECTED {path}: {error}')
                                conn.send_headers(ev.stream_id, response_headers + [
                                    ('grpc-status', '9'), ('grpc-message', str(error))], end_stream=True)
                                _pnote(_pseq, 'PRODUCE-REJECTED', None, 200)
                            except Exception as error:
                                log(f'#{cid} PRODUCE ERR {path}: {type(error).__name__}: {error}')
                                conn.send_headers(ev.stream_id, response_headers + [
                                    ('grpc-status', '13'), ('grpc-message', 'Local Produce response failed')],
                                    end_stream=True)
                                _pnote(_pseq, 'PRODUCE-ERROR', None, 200)
                            else:
                                conn.send_headers(ev.stream_id, response_headers)
                                send_body_with_flow(conn, tls, ev.stream_id, local_body)
                                conn.send_headers(ev.stream_id, [('grpc-status', '0')], end_stream=True)
                                _pnote(_pseq, 'PRODUCE', local_body, 200)
                                log(f'#{cid} LOCAL {path} -> {len(local_body)} B')
                            tls.sendall(conn.data_to_send())
                            continue

                    if MODE != 'online' and UNLOCKER is not None:
                        _preparation = UNLOCKER.build_preparation_response(path)
                        if _preparation is not None:
                            conn.send_headers(ev.stream_id,
                                [(':status', '200'), ('content-type', 'application/grpc')] + _time_headers())
                            send_body_with_flow(conn, tls, ev.stream_id, _preparation)
                            conn.send_headers(ev.stream_id, [('grpc-status', '0')], end_stream=True)
                            tls.sendall(conn.data_to_send())
                            _pnote(_pseq, 'PREPARATION', _preparation, 200)
                            log(f'#{cid} LOCAL-PREPARATION {path} -> {len(_preparation)} B')
                            continue

                    # === 写操作本地处理（不转发、不缓存）===
                    WRITE_HANDLED = False
                    if UNLOCKER is not None and getattr(UNLOCKER, 'state', None) is not None:
                        # 写操作抓包：把这几个路径的原始请求体落盘，便于核对字段号
                        if any(k in path for k in ('/Costume/Set', '/Home/SetCharacters', '/Story/Read', '/Story/Unlock', '/PictureBookLive')):
                            try:
                                with open(os.path.join(HERE, 'write-debug.log'), 'a', encoding='utf-8') as _wf:
                                    _wf.write('%s %s %s\n' % (datetime.datetime.now().isoformat(), path, body.hex()))
                            except Exception:
                                pass
                        try:
                            if MODE != 'online' or not path.startswith('/client.api.Produce/'):
                                WRITE_HANDLED = UNLOCKER.handle_write(path, body)
                        except Exception as _we:
                            log(f'#{cid} WRITE ERR {path}: {type(_we).__name__}: {_we}')
                    # === Profile/Get：本地返回个人资料（官方返回空，会进不去页面）===
                    # Gasha/Draw（抽卡）：本地处理，从已解锁卡面里随机抽
                    # ★ 精确匹配：不能用子串，否则 Gasha/DrawSelectPickup 也会命中，
                    #   而它的 f3 不是张数，会导致 100 条结果 + 演出卡死
                    _is_gasha = (path.rstrip('/').endswith('/client.api.Gasha/Draw')
                                 or path.rstrip('/').endswith('/client.api.Gasha/DrawSelectPickup'))
                    if _is_gasha and UNLOCKER is not None:
                        try:
                            _grb = UNLOCKER.build_gasha_response(body, path)
                            if _grb:
                                _pnote(_pseq, 'GASHA', _grb, 200)
                                _g = getattr(UNLOCKER, '_last_gasha', None) or {}
                                log(f'#{cid} GASHA-DRAW -> {len(_grb)} B  '
                                    f'gasha={_g.get("gasha","")} button={_g.get("button","")}')
                                conn.send_headers(ev.stream_id,
                                    [(':status','200'),('content-type','application/grpc')] + _time_headers())
                                send_grpc_data(conn, tls, ev.stream_id, _grb)
                                conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                                tls.sendall(conn.data_to_send())
                            else:
                                log(f'#{cid} GASHA-DRAW failed (no skins?)')
                        except Exception as _ge:
                            log(f'#{cid} GASHA-DRAW ERR: {_ge}')
                        continue

                    # IdolCard/Release（アイドル解放）：本地处理，不转发
                    if '/client.api.IdolCard/Release' in path:
                        log(f'#{cid} IDOL-RELEASE body_hex={body[:80].hex()}')
                        rb0 = b'\x00\x00\x00\x00\x04\xfa\xf0\x04\x00'
                        _pnote(_pseq, 'IDOL-RELEASE', rb0, 200)
                        try:
                            conn.send_headers(ev.stream_id,
                                [(':status','200'),('content-type','application/grpc')] + _time_headers())
                            send_grpc_data(conn, tls, ev.stream_id, rb0)
                            conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                            tls.sendall(conn.data_to_send())
                        except Exception as _ie:
                            log(f'#{cid} IDOL-RELEASE ERR: {_ie}')
                        continue

                    if False and '/client.api.Profile/Get' in path and UNLOCKER is not None:
                        try:
                            log(f'#{cid} PROFILE-REQ body_hex={body[:80].hex()}')
                            _prb = UNLOCKER.build_profile_response(body)
                            log(f'#{cid} PROFILE {path} -> {len(_prb)} B')
                            conn.send_headers(ev.stream_id,
                                [(':status','200'),('content-type','application/grpc')] + _time_headers())
                            send_grpc_data(conn, tls, ev.stream_id, _prb)
                            conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                            tls.sendall(conn.data_to_send())
                        except Exception as _pe:
                            log(f'#{cid} PROFILE ERR: {_pe}')
                        continue

                    if WRITE_HANDLED:
                        try:
                            rb0 = UNLOCKER.build_write_response(path, request_body=body)
                        except Exception as _be:
                            log(f'#{cid} WRITE RESP ERR: {_be}')
                            rb0 = b'\x00\x00\x00\x00\x04\xfa\xf0\x04\x00'
                        _pnote(_pseq, 'WRITE', rb0, 200)
                        _extra = getattr(UNLOCKER, 'last_story_unlock', None) if UNLOCKER is not None else None
                        log(f'#{cid} LOCAL {path} -> {len(rb0)} B' + (f' story={_extra}' if _extra else ''))
                        try:
                            conn.send_headers(ev.stream_id,
                                [(':status','200'),('content-type','application/grpc')] + _time_headers())
                            send_grpc_data(conn, tls, ev.stream_id, rb0)
                            conn.send_headers(ev.stream_id,
                                [('grpc-status','0')], end_stream=True)
                            tls.sendall(conn.data_to_send())
                        except Exception as _se:
                            log(f'#{cid} LOCAL SEND ERR: {_se}')
                        continue

                    _nocache = any(x in path for x in NO_CACHE_PATHS)
                    hit, entry = (False, None) if _nocache else cache_lookup(path, body)
                    src = None; status = 200; rh = {}; rb = b''

                    # ★ online 模式必须让请求真正打到官服。
                    #   forge 里配的是 System/Check 的伪造响应（1064 B），离线时游戏认；
                    #   但 online 时游戏看到这份假响应就不会再发 Auth/Login，
                    #   一键更新会卡在「等待新密钥表」然后超时（日志里只有 FORGE，零 UPSTREAM）。
                    _frb = None if MODE == 'online' else forge_lookup(path)
                    _active_master = offline_master_response() if MODE == 'offline' and path == '/client.api.Master/Get' else None
                    # ★ User/Get 优先直接发静态存档。
                    #   原来只有「转发成功后」才会用 static_archive，离线模式永远落到
                    #   fallback 的旧 User_Get.bin（818 KB），导致新解锁内容全部不生效。
                    _static_ub = getattr(UNLOCKER, 'static_archive', None) if UNLOCKER is not None else None
                    # online 模式要让 User/Get 也走官服，否则抓到的是本地静态存档而不是真响应
                    if MODE != 'online' and _static_ub is not None and path.startswith('/client.api.User/Get'):
                        # ★ 叠加游戏内写操作（换装 / 首页角色 / 剧情已读）。
                        #   DynamicState 已落盘到 dynamic-state.json，这里必须用
                        #   render() 的结果，否则重启后换装/换人全部复原。
                        try:
                            _rendered = UNLOCKER.state.render()
                            if _rendered:
                                if _rendered != _static_ub:
                                    log(f'#{cid} STATIC+STATE {len(_static_ub)} -> {len(_rendered)} B')
                                _static_ub = _rendered
                        except Exception as _re:
                            log(f'#{cid} STATE RENDER ERR: {type(_re).__name__}: {_re}')
                        rh = {'content-type': 'application/grpc'}
                        rb = _static_ub
                        src = 'STATIC'
                        status = 200
                        log(f'#{cid} STATIC {path} len={len(rb)}')
                    elif _active_master is not None:
                        rh={'content-type':'application/grpc'};rb=_active_master;src='ACTIVE-MASTER';status=200
                        log(f'#{cid} ACTIVE-MASTER {path} len={len(rb)}')
                    elif _frb is not None:
                        rh = {'content-type': 'application/grpc'}
                        rb = _frb
                        src = 'FORGE'
                        status = 200
                        log(f'#{cid} FORGE {path} len={len(rb)}')
                    elif hit and MODE in ('offline', 'hybrid'):
                        status = entry.get('status', 200)
                        rh = entry.get('resp_headers', {}) or {}
                        rb = bytes.fromhex(entry.get('resp_body_hex') or '')
                        src = 'CACHE'
                        log(f'#{cid} CACHE HIT {path} fp={entry.get("fingerprint")} len={len(rb)}')
                        # Home/Login：注入当前主页角色配置（HomeLoginResponse 有 f9999.commonResponse）
                        # Home/Enter 也有 f9999；这里只在 Home/Login 注入主页配置，避免重复更新。
                        if '/client.api.Home/Login' in path and UNLOCKER is not None:
                            try:
                                _inj = UNLOCKER.inject_home_chars(rb)
                                if _inj is not rb:
                                    log(f'#{cid} HOME-INJECT {len(rb)} -> {len(_inj)} B')
                                    rb = _inj
                            except Exception as _hie:
                                log(f'#{cid} HOME-INJECT ERR: {_hie}')
                    else:
                        try:
                            if MODE == 'offline':
                                # 离线模式：跳过转发，直接落到下面的 except 兜底路径。
                                # 关键：raise 必须在 try 内，否则会冒到 handler 层让连接直接断掉，
                                # 客户端表现就是「通信错误」。
                                log(f'#{cid} CACHE MISS (offline) {path} -> fallback')
                                raise RuntimeError('offline mode: skip forward, use fallback')
                            status, rh, rb = forward_grpc(path, st['headers'], body)
                            src = 'UPSTREAM'
                            log(f'#{cid} UPSTREAM {path} -> {status} len={len(rb)}')
                            # 在线抓取：把官服真实的 Produce 响应落盘（用于还原协议）
                            if DUMP_PRODUCE and path.startswith('/client.api.Produce/') and rb:
                                try:
                                    _dfn = 'produce-real-' + path.rstrip('/').rsplit('/', 1)[-1] + '.hex'
                                    with open(os.path.join(HERE, _dfn), 'w', encoding='ascii') as _dfh:
                                        _dfh.write(rb.hex())
                                    log(f'#{cid} PRODUCE-DUMP {path} -> {_dfn} ({len(rb)} B)')
                                except Exception as _dpe:
                                    log(f'#{cid} PRODUCE-DUMP ERR: {_dpe}')
                            # === 全解锁：转发后动态合并（保留账号数据，只拉满解锁列表）===
                            if (UNLOCKER is not None and UNLOCKER.enabled and rb
                                    and not (DUMP_PRODUCE and MODE == 'online')):
                                # 在线抓取模式纯透传：绝不篡改官服响应，避免污染真实账号
                                try:
                                    _nb = UNLOCKER.tamper(path, rb)
                                    if _nb is not rb:
                                        log(f'#{cid} UNLOCK {path} {len(rb)} -> {len(_nb)}')
                                        rb = _nb
                                except Exception as _ue:
                                    log(f'#{cid} UNLOCK ERR {type(_ue).__name__}: {_ue}')
                            # DUMP-PROFILE：保存官服真实响应供分析
                            if '/client.api.Profile/Get' in path and rb:
                                try:
                                    with open(os.path.join(HERE, 'profile-real.hex'), 'w') as _pf:
                                        _pf.write(rb.hex())
                                    log(f'#{cid} DUMP-PROFILE {len(rb)} B')
                                except Exception:
                                    pass
                            # Profile/Get：篡改名字/评论（保留 P-ID 让官服数据完整）
                            if '/client.api.Profile/Get' in path and rb and UNLOCKER is not None:
                                try:
                                    _tb = UNLOCKER.tamper_profile(rb)
                                    if _tb is not rb:
                                        log(f'#{cid} PROFILE-TAMPER {len(rb)} -> {len(_tb)}')
                                        rb = _tb
                                except Exception as _te:
                                    log(f'#{cid} PROFILE-TAMPER ERR: {_te}')
                            if not any(x in path for x in NO_CACHE_PATHS):
                                cache_store(path, body, rh, rb, status)
                        except Exception as e:
                            log(f'#{cid} FWD ERR {path}: {type(e).__name__}: {e}')
                            # 调试：True LIVE 图鉴点开时到底发了什么
                            if 'PictureBookLive' in path:
                                try:
                                    log(f'#{cid} PBL-REQ {path} len={len(body)} hex={body.hex()}')
                                except Exception:
                                    pass
                            # 离线兜底：按 path 找 offline-fallbacks/<name>.bin
                            if path.startswith('/client.api.Produce/'):
                                try:
                                    with open(os.path.join(HERE, 'produce-last-request.json'), 'w', encoding='utf-8') as _pf:
                                        json.dump({'path': path, 'body_hex': body.hex()}, _pf)
                                except OSError:
                                    pass
                            try:
                                _api = path.split('/client.api.', 1)[-1] if '/client.api.' in path else path
                                _name = _api.replace('/', '_').replace('.', '_')
                                _fbf = os.path.join(HERE, 'offline-fallbacks', _name + '.bin')
                                # User/Get 离线时直接用当前 static archive（避免 fallback 陈旧）
                                _static_ub = None
                                if '/client.api.User/Get' in path and UNLOCKER is not None:
                                    _static_ub = getattr(UNLOCKER, 'static_archive', None)
                                if _static_ub:
                                    # 注入当前主页角色 + 换装状态（这条路径直接发 static_archive，不经过 tamper）
                                    try:
                                        _sb2 = UNLOCKER.inject_home_into_userget(_static_ub)
                                        if _sb2 is not _static_ub:
                                            log(f'#{cid} USERGET-INJECT {len(_static_ub)} -> {len(_sb2)} B')
                                            _static_ub = _sb2
                                        _sb3 = UNLOCKER.inject_costume_into_userget(_static_ub)
                                        if _sb3 is not _static_ub:
                                            log(f'#{cid} COSTUME-INJECT {len(_static_ub)} -> {len(_sb3)} B')
                                            _static_ub = _sb3
                                    except Exception as _uie:
                                        log(f'#{cid} USERGET-INJECT ERR: {_uie}')
                                    _pnote(_pseq, 'STATIC', _static_ub, 200)
                                    log(f'#{cid} STATIC-USERGET {len(_static_ub)} B')
                                    conn.send_headers(ev.stream_id,
                                        [(':status','200'),('content-type','application/grpc')] + _time_headers())
                                    send_grpc_data(conn, tls, ev.stream_id, _static_ub)
                                    conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                                    tls.sendall(conn.data_to_send())
                                    continue
                                if path != '/client.api.StoryEvent/Top' and os.path.isfile(_fbf):
                                    _fb = open(_fbf, 'rb').read()
                                    _pnote(_pseq, 'FALLBACK', _fb, 200)
                                    log(f'#{cid} OFFLINE-FALLBACK {_api} -> {len(_fb)} B')
                                    conn.send_headers(ev.stream_id,
                                        [(':status','200'),('content-type','application/grpc')] + _time_headers())
                                    send_grpc_data(conn, tls, ev.stream_id, _fb)
                                    conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                                    tls.sendall(conn.data_to_send())
                                    if MODE == 'offline' and path == '/client.api.Home/Enter': master_manager().observe_success(path,st['headers'],_fb)
                                    continue
                            except Exception as _fbe:
                                log(f'#{cid} OFFLINE-FALLBACK ERR: {_fbe}')
                            if path == '/client.api.StoryEvent/Top':
                                log(f'#{cid} ACTIVITY-MISS {path}: no snapshot for requested activity')
                                conn.send_headers(ev.stream_id,
                                    [(':status', '200'), ('content-type', 'application/grpc'),
                                     ('grpc-status', '5'), ('grpc-message', 'Local activity details missing')],
                                    end_stream=True)
                                tls.sendall(conn.data_to_send())
                                continue
                            # 育成类接口：必须返回带 f9999 的合法空 commonResponse。
                            # 5 字节纯空帧或回显请求体都会让客户端报「通信错误」。
                            # 注意：真实路径是 /client.api.Produce/...，不能用 '/Produce/' 匹配
                            # （Produce 前面是点号，'/Produce/' 永远不成立）。
                            if '/client.api.Produce/' in path and UNLOCKER is not None:
                                try:
                                    _eb = UNLOCKER.build_empty_response()
                                    conn.send_headers(ev.stream_id,
                                        [(':status','200'),('content-type','application/grpc')] + _time_headers())
                                    send_grpc_data(conn, tls, ev.stream_id, _eb)
                                    conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                                    tls.sendall(conn.data_to_send())
                                    _pnote(_pseq, 'EMPTY-PRODUCE', _eb, 200)
                                    log(f'#{cid} OFFLINE-PRODUCE {path} -> {len(_eb)} B (empty common)')
                                    continue
                                except Exception as _ee:
                                    log(f'#{cid} PRODUCE-EMPTY ERR {path}: {_ee}')
                            # 其余 API：空成功帧（优雅降级）
                            try:
                                conn.send_headers(ev.stream_id,
                                    [(':status','200'),('content-type','application/grpc')] + _time_headers())
                                send_grpc_data(conn, tls, ev.stream_id, b'\x00\x00\x00\x00\x04\xfa\xf0\x04\x00')
                                conn.send_headers(ev.stream_id, [('grpc-status','0')], end_stream=True)
                                tls.sendall(conn.data_to_send())
                                _pnote(_pseq, 'EMPTY', b'\x00\x00\x00\x00\x04\xfa\xf0\x04\x00', 200)
                                log(f'#{cid} OFFLINE-EMPTY {path} -> 5 B')
                            except Exception as _ee:
                                log(f'#{cid} OFFLINE-EMPTY ERR: {_ee}')
                            continue

                    # 回放
                    try:
                        _pnote(_pseq, src or 'NONE', rb, status)
                        out = [(':status', str(status))]
                        _clock_headers = dict(_time_headers())
                        for k, v in (rh or {}).items():
                            lk = k.lower()
                            if lk in ('content-length','transfer-encoding','connection','keep-alive') or lk in _clock_headers:
                                continue
                            out.append((lk, v))
                        out.extend(_clock_headers.items())
                        if not any(k == 'content-type' for k, _ in out):
                            out.append(('content-type', 'application/grpc'))
                        # === 篡改实验（GKM_TAMPER）===
                        _cfg = get_tamper_cfg()
                        tamper, tfilter = _cfg[0], _cfg[1]
                        _PROBE["step"], _PROBE["size"] = _cfg[2], _cfg[3]
                        _PROBE["start"] = _cfg[4]
                        if _cfg[5] >= 0:
                            _PROBE["fixed"] = _cfg[5]
                        _PROBE["xor"] = _cfg[6]
                        tamper = tamper.lower()
                        if tfilter and tfilter not in path:
                            tamper = ''
                        if tamper and rb:
                            rb2 = bytearray(rb)
                            if tamper == 'zero':
                                # 把 gRPC 消息体（跳过 5 字节帧头）全部清零
                                for i in range(5, len(rb2)):
                                    rb2[i] = 0
                            elif tamper == 'flip':
                                # 翻转最后一个字节
                                rb2[-1] ^= 0xFF
                            elif tamper == 'fliplast16':
                                # 翻转最后 16 字节（可能是 GCM tag）
                                for i in range(max(5, len(rb2)-16), len(rb2)):
                                    rb2[i] ^= 0xFF
                            elif tamper == 'flipfirst':
                                # 翻转密文第一个字节（最可能破坏 protobuf 结构）
                                if len(rb2) > 12:
                                    rb2[9 + rb2[8]] ^= 0xFF if len(rb2) > 9 + rb2[8] else 0
                            elif tamper == 'zeromid':
                                # 把密文中段 32 字节清零
                                if len(rb2) > 80:
                                    mid = len(rb2) // 2
                                    for i in range(mid, min(mid + 32, len(rb2))):
                                        rb2[i] = 0
                            elif tamper == 'ascii':
                                # 只改 ASCII 部分（[5]=len+2, [8]=asciilen, [9:]=ascii）
                                if len(rb2) > 12 and rb2[6] == 0 and rb2[7] == 0:
                                    an = rb2[8]
                                    for i in range(9, min(9+an, len(rb2))):
                                        rb2[i] = ord('Z')
                            rb = bytes(rb2)
                            log(f'#{cid} TAMPER[{tamper}] applied to {path}')

                        # === 字节翻转探测（mode=probe）===
                        if tamper == 'probe' and rb:
                            _p = rb
                            _hdr = 5
                            if _p[0] == 0 and len(_p) >= 5:
                                _ml = int.from_bytes(_p[1:5], 'big')
                                if _ml + 5 <= len(_p):
                                    _hdr = 5
                            _an = _p[_hdr + 3] if len(_p) > _hdr + 3 else 0
                            _poff = _hdr + 4 + _an          # payload 起点
                            if 0 < _an < 64 and _poff < len(_p):
                                _payload = _p[_poff:]
                                _new, _off = probe_apply(_payload, path)
                                rb = _p[:_poff] + _new
                                log(f'#{cid} PROBE flip payload+{_off} (len={len(_payload)}) path={path}')
                                try:
                                    with open(os.path.join(HERE, 'probe-report.txt'), 'w', encoding='utf-8') as _f:
                                        _f.write(probe_dump())
                                except Exception:
                                    pass

                        conn.send_headers(ev.stream_id, out)
                        # gRPC: DATA 分片发送（大响应必须切分，遵守 16KB 帧上限与流控）
                        if rb:
                            send_body_with_flow(conn, tls, ev.stream_id, rb)
                        # gRPC trailers：客户端靠 grpc-status 判断流结束
                        trailers = [('grpc-status', '0')]
                        conn.send_headers(ev.stream_id, trailers, end_stream=True)
                        tls.sendall(conn.data_to_send())
                        log(f'#{cid} REPLIED {path} src={src} len={len(rb)} +trailers(grpc-status:0)')
                        if MODE == 'offline' and path in ('/client.api.Master/Get','/client.api.Home/Enter'):master_manager().observe_success(path,st['headers'],rb)
                    except Exception as e:
                        log(f'#{cid} reply err {e}')

                    # 落盘审计记录
                    # ★ 只记上游真实响应。离线回放的 CACHE / FORGE 如果也写，会把联网
                    #   抓到的 Master/Get 密钥表覆盖回旧版本 —— 抓了等于没抓。
                    if src == 'UPSTREAM':
                        rec = {'seq': cid, 'path': path, 'source': src,
                               'req_body_len': len(body), 'req_body_hex': body.hex(),
                               'resp_body_len': len(rb), 'resp_body_hex': rb.hex(),
                               'status': status, 'ts': datetime.datetime.now().isoformat()}
                        try:
                            json.dump(rec, open(os.path.join(OUTDIR, f'{cid:05d}_{sanitize(path)}.json'),'w',encoding='utf-8'),
                                      ensure_ascii=False, indent=1)
                        except Exception: pass
    except Exception as e:
        log(f'#{cid} handler err {type(e).__name__}: {e}')
        traceback.print_exc()
    finally:
        try: tls.close()
        except Exception: pass

def stats():
    total = 0; paths = {}
    for root, _, files in os.walk(CACHEDIR):
        for f in files:
            if f.endswith('.json'):
                total += 1
                rel = os.path.relpath(root, CACHEDIR).replace('\\', '/')
                paths[rel] = paths.get(rel, 0) + 1
    return total, paths

H2C_PORT = int(os.environ.get('GKM_H2C_PORT', '18446'))

def h2c_accept_loop(srv, sslctx):
    """明文 HTTP/2 (h2c) 监听，供进程内 hook 重写后的 URI 使用"""
    while True:
        try:
            sock, addr = srv.accept()
        except Exception:
            break
        t = threading.Thread(target=handle_client, args=(sock, addr, sslctx, False), daemon=True)
        t.start()


def main():
    global _CONTENT_RELOADER
    # A crash during publication must be recovered before static data is loaded.
    _root = os.path.dirname(HERE)
    if os.path.isdir(os.path.join(_root,'out','local-sync-transaction')):
        import importlib.util
        from pathlib import Path
        _spec=importlib.util.spec_from_file_location('gkm_startup_sync',os.path.join(_root,'tools','local_content_sync.py'))
        _recovery=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(_recovery)
        with _recovery.lock(Path(_root)):
            _recovery.recover_transaction(Path(_root))
        log('MASTER startup: interrupted publication recovered')
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(LEAF_CERT, LEAF_KEY)
    try: ctx.set_alpn_protocols(['h2'])
    except Exception as e: log('alpn err', e)

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind(('127.0.0.1', LISTEN_PORT))
    except OSError as e:
        log(f'FATAL 无法监听 127.0.0.1:{LISTEN_PORT} - {e}')
        log('      端口被占用：可能另一份离线包还在跑，或上次的服务没有退出。')
        log('      处理办法：任务管理器结束 python.exe，或在启动器里点「卸载解锁组件」')
        sys.exit(3)
    srv.listen(128)

    # 明文 h2c 监听（供进程内 hook 重写后的 URI 使用）
    h2c = None
    try:
        h2c = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        h2c.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        h2c.bind(('127.0.0.1', H2C_PORT))
        h2c.listen(128)
        threading.Thread(target=h2c_accept_loop, args=(h2c, ctx), daemon=True).start()
        log(f'H2C listener: 127.0.0.1:{H2C_PORT}')
    except Exception as e:
        log(f'H2C listen failed: {type(e).__name__}: {e}')

    # 静态模式：优先读预生成的 static_userget.bin（分发包只带这个，不需要 masterTrans）
    _static_loaded = False
    if UNLOCKER is not None and os.environ.get('GKM_STATIC_USER', '1') == '1':
        _bin = os.path.join(HERE, 'static_userget.bin')
        if os.path.isfile(_bin):
            try:
                with open(_bin, 'rb') as _f:
                    _blob = _f.read()
                # 应用 config.json（个人资料等）
                try:
                    _cfg = _unlock_mod.load_config()
                    _nb = _unlock_mod.apply_config_to_blob(_blob, _cfg)
                    if _nb is not _blob:
                        log(f'CONFIG applied: {_cfg.get("profile")}')
                        _blob = _nb
                except Exception as _ce:
                    log(f'CONFIG apply failed: {_ce}')
                UNLOCKER.set_static(_blob)
                log(f'STATIC ARCHIVE loaded from file: {len(_blob):,} B')
                _static_loaded = True
            except Exception as e:
                log('static bin load failed:', e)

    # 静态模式：从一条真实 User/Get 模板构建「全解锁存档」
    # ★ 只有没读到预生成文件时才构建。否则会用缓存里的旧模板重建一份，
    #   把刚加载的新存档覆盖掉 —— 表现就是「更新后照样没解锁」。
    if (not _static_loaded) and UNLOCKER is not None and UNLOCKER.enabled and os.environ.get('GKM_STATIC_USER','0') == '1':
        tpl = None
        # 优先用 records 里的完整响应
        cand = os.path.join(OUTDIR, '00030_client.api.User_Get.json')
        if os.path.isfile(cand):
            try:
                tpl = bytes.fromhex(json.load(open(cand, encoding='utf-8'))['resp_body_hex'])
            except Exception as e:
                log('static template from records failed:', e)
        if tpl is None:
            d = os.path.join(CACHEDIR, 'client.api.User_Get')
            if os.path.isdir(d):
                fs = [f for f in os.listdir(d) if f.endswith('.json')]
                if fs:
                    try:
                        tpl = bytes.fromhex(json.load(open(os.path.join(d, fs[0]), encoding='utf-8'))['resp_body_hex'])
                    except Exception as e:
                        log('static template from cache failed:', e)
        if tpl:
            arch = UNLOCKER.build_static(tpl)
            if arch:
                log(f'STATIC ARCHIVE ready: {len(arch):,} B')
            else:
                log('STATIC ARCHIVE build failed')
        else:
            log('STATIC ARCHIVE: no template found')

    if _static_loaded and UNLOCKER is not None:
        from content_reload import ContentReloader
        _CONTENT_RELOADER = ContentReloader(HERE, UNLOCKER, _unlock_mod, log)

    total, paths = stats()
    log(f'=== gakumas cache/replay server ===')
    log(f'MODE={MODE}  listen=127.0.0.1:{LISTEN_PORT}  upstream={UPSTREAM_HOST}  proxy={UPSTREAM_PROXY or "(direct)"}')
    log(f'cache entries: {total}  across {len(paths)} APIs  match={os.environ.get("GKM_MATCH","exact")}')
    if UNLOCKER is not None and UNLOCKER.enabled:
        log('UNLOCK enabled:', UNLOCKER.stats())
    else:
        log('UNLOCK disabled')
    log(f'tamper config: {os.path.join(HERE, "tamper.cfg")} (hot-reloaded each request)')

    # === 运行时探针：HTTP 观测口（probe.json 控制）===
    if PROBE is not None:
        try:
            _u = globals().get('_unlock_mod')
            _pdec = getattr(_u, 'decrypt_body', None) if _u else None
            _pfld = getattr(_u, 'fields', None) if _u else None
            _proot = HERE
            for _cand in (os.path.dirname(HERE), HERE):
                if os.path.isfile(os.path.join(_cand, 'probe.json')):
                    _proot = _cand
                    break
            if PROBE.attach('grpc', root=_proot, decrypt=_pdec, fields=_pfld):
                log('PROBE enabled: http://127.0.0.1:%d/probe/state' % PROBE.port())
            else:
                log('PROBE attach failed (continue without probe)')
        except Exception as _pe:
            log('PROBE init error: %s: %s' % (type(_pe).__name__, _pe))

    while True:
        try:
            sock, addr = srv.accept()
            threading.Thread(target=handle_client, args=(sock, addr, ctx), daemon=True).start()
        except KeyboardInterrupt:
            log('bye'); break
        except Exception as e:
            log('accept err', e)

if __name__ == '__main__':
    main()
