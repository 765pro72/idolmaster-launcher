"""Bridge for the yaha hook's GKM_YAHA_PLAIN mode.

Accepts whatever the hook speaks on 127.0.0.1:18444 and forwards it to
cache_server's h2c listener on 127.0.0.1:18446.

Two dialects are supported transparently:
  * HTTP/2 prior-knowledge (starts with b'PRI * HTTP/2.0') -> raw TCP splice
  * HTTP/1.1 gRPC (POST ...)                                -> translate to h2c

The 3.5.3 client's stock native HTTP stack (rustls + built-in roots) refuses
our self-signed CA, so HTTPS to 18443 dies with "invalid peer certificate".
GKM_YAHA_PLAIN=1 makes the hook rewrite the gRPC URI to http://127.0.0.1:18444
instead; the wire format there depends on the hook build, hence both paths.
"""
import os
import socket
import sys
import threading

HERE = os.path.dirname(os.path.abspath(__file__))

import h2.config
import h2.connection
import h2.events

LISTEN = ('127.0.0.1', 18444)
UPSTREAM = ('127.0.0.1', 18446)
LOG = None


def log(msg):
    global LOG
    if LOG is None:
        LOG = open(os.path.join(HERE, 'http1_bridge.log'), 'a', encoding='utf-8')
    LOG.write(msg + '\n')
    LOG.flush()


def pipe(src, dst):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                break
            dst.sendall(data)
    except Exception:
        pass
    finally:
        try:
            dst.shutdown(socket.SHUT_WR)
        except Exception:
            pass


def read_head(sock, buf, limit=1 << 20):
    while b'\r\n\r\n' not in buf:
        chunk = sock.recv(65536)
        if not chunk:
            return None, buf
        buf += chunk
        if len(buf) > limit:
            return None, buf
    head, _, rest = buf.partition(b'\r\n\r\n')
    return head, rest


def read_exact(sock, buf, n):
    while len(buf) < n:
        chunk = sock.recv(65536)
        if not chunk:
            break
        buf += chunk
    return buf


def read_line(sock, buf):
    while b'\r\n' not in buf:
        chunk = sock.recv(65536)
        if not chunk:
            return None, buf
        buf += chunk
    line, _, rest = buf.partition(b'\r\n')
    return line, rest


def read_chunked(sock, buf):
    out = b''
    while True:
        line, buf = read_line(sock, buf)
        if line is None:
            return None, buf
        try:
            size = int(line.split(b';')[0].strip(), 16)
        except Exception:
            return None, buf
        if size == 0:
            while True:
                line, buf = read_line(sock, buf)
                if line is None or line == b'':
                    break
            return out, buf
        buf = read_exact(sock, buf, size + 2)
        out += buf[:size]
        buf = buf[size + 2:]
        if len(out) > (32 << 20):
            return None, buf


def parse_head(head):
    lines = head.split(b'\r\n')
    if not lines:
        return None
    parts = lines[0].decode('latin-1').split(' ')
    if len(parts) < 3:
        return None
    method, path = parts[0], parts[1]
    headers = {}
    for line in lines[1:]:
        if b':' not in line:
            continue
        k, _, v = line.partition(b':')
        headers[k.decode('latin-1').strip().lower()] = v.decode('latin-1').strip()
    return method, path, headers


def request_headers(path, headers):
    # HTTP/2 forbids connection-specific fields. Preserve application metadata,
    # including the client's version/platform/session fields, without inventing it.
    nominated = {part.strip().lower() for part in headers.get('connection', '').split(',')}
    drop = {'host', 'connection', 'content-length', 'transfer-encoding',
            'keep-alive', 'proxy-connection', 'upgrade', 'te'} | nominated
    result = [(':method', 'POST'), (':path', path), (':scheme', 'http'),
              (':authority', '127.0.0.1:18446'), ('te', 'trailers')]
    for key, value in headers.items():
        name = key.lower()
        if not name.startswith(':') and name not in drop:
            result.append((name, value))
    if 'content-type' not in headers:
        result.append(('content-type', 'application/grpc'))
    return result


def response_metadata(rows):
    drop = {'connection', 'content-length', 'transfer-encoding', 'keep-alive',
            'proxy-connection', 'upgrade', 'te', 'trailer', 'content-type',
            'grpc-status', 'grpc-message'}
    result = []
    for key, value in rows:
        name = key.lower()
        if name.startswith(':') or name in drop:
            continue
        if '\r' in name or '\n' in name or '\r' in value or '\n' in value:
            raise ValueError('Invalid upstream metadata')
        result.append((name, value))
    return result


def upstream_call(path, headers, body):
    sock = socket.create_connection(UPSTREAM, timeout=10)
    try:
        cfg = h2.config.H2Configuration(client_side=True, header_encoding='utf-8')
        conn = h2.connection.H2Connection(config=cfg)
        conn.initiate_connection()
        sock.sendall(conn.data_to_send())

        sid = conn.get_next_available_stream_id()
        hdrs = request_headers(path, headers)
        conn.send_headers(sid, hdrs)
        if body:
            conn.send_data(sid, body)
        conn.end_stream(sid)
        sock.sendall(conn.data_to_send())

        status = 500
        resp = b''
        grpc_status = None
        grpc_message = None
        metadata = []
        done = False
        sock.settimeout(20)
        while not done:
            data = sock.recv(65536)
            if not data:
                break
            for ev in conn.receive_data(data):
                if isinstance(ev, h2.events.ResponseReceived):
                    for k, v in ev.headers:
                        if k == ':status':
                            status = int(v)
                        elif k == 'grpc-status':
                            grpc_status = v
                        elif k == 'grpc-message':
                            grpc_message = v
                    metadata.extend(response_metadata(ev.headers))
                elif isinstance(ev, h2.events.DataReceived):
                    resp += ev.data
                    conn.acknowledge_received_data(ev.flow_controlled_length, ev.stream_id)
                elif isinstance(ev, h2.events.TrailersReceived):
                    metadata.extend(response_metadata(ev.headers))
                    for k, v in ev.headers:
                        if k == 'grpc-status':
                            grpc_status = v
                        elif k == 'grpc-message':
                            grpc_message = v
                    done = True
                elif isinstance(ev, h2.events.StreamEnded):
                    done = True
            pending = conn.data_to_send()
            if pending and not done:
                sock.sendall(pending)
        return status, resp, grpc_status, grpc_message, metadata
    finally:
        try:
            sock.close()
        except Exception:
            pass


def handle(client, addr):
    try:
        client.settimeout(15)
        log('CONN %s' % (addr,))
        first = b''
        while len(first) < 5:
            chunk = client.recv(65536)
            if not chunk:
                log('CONN %s: closed before 5 bytes' % (addr,))
                return
            first += chunk
        log('FIRST bytes=%d' % len(first))

        if first.startswith(b'PRI * HTTP/2.0'):
            log('H2C passthrough -> %s' % (UPSTREAM,))
            up = socket.create_connection(UPSTREAM, timeout=10)
            up.sendall(first)
            th = threading.Thread(target=pipe, args=(up, client), daemon=True)
            th.start()
            pipe(client, up)
            th.join(5)
            return

        buf = first
        log('HEAD first=%d' % len(first))
        head, rest = read_head(client, buf)
        log('HEAD done headlen=%s restlen=%d' % ('-' if head is None else len(head), len(rest)))
        if head is None:
            log('CONN %s: no header terminator, bytes=%d' % (addr, len(rest)))
            return
        buf = rest
        parsed = parse_head(head)
        if not parsed:
            log('CONN %s: malformed request head' % (addr,))
            return
        method, path, headers = parsed
        te = headers.get('transfer-encoding', '').lower()
        if 'chunked' in te:
            body, buf = read_chunked(client, buf)
            if body is None:
                return
        else:
            n = int(headers.get('content-length', '0') or 0)
            buf = read_exact(client, buf, n)
            body = buf[:n]
        log('%s %s body=%d' % (method, path, len(body)))

        status, resp, grpc_status, grpc_message, metadata = upstream_call(path, headers, body)
        trailer = ['grpc-status: %s' % (grpc_status if grpc_status is not None else '13')]
        if grpc_message:
            trailer.append('grpc-message: %s' % grpc_message)
        # 必须回 HTTP/1.1：客户端是 curl（HTTP/1.1 模式），看到 "HTTP/2" 会
        # 报 "invalid HTTP version parsed"。body 用 chunked + trailer 才是
        # gRPC over HTTP/1.1 的合法形态（grpc-status 走 trailer）。
        chunk = b''
        if resp:
            chunk = ('%x\r\n' % len(resp)).encode('latin-1') + resp + b'\r\n'
        head = (
            'HTTP/1.1 %d OK\r\n'
            'Content-Type: application/grpc\r\n'
            'Transfer-Encoding: chunked\r\n'
            'Trailer: grpc-status, grpc-message\r\n'
            '\r\n'
        ) % status
        head = head[:-2] + ''.join('%s: %s\r\n' % pair for pair in metadata) + '\r\n'
        tail = b'0\r\n' + ('\r\n'.join(trailer) + '\r\n').encode('latin-1') + b'\r\n'
        client.sendall(head.encode('latin-1') + chunk + tail)
        log('%s -> %d body=%d grpc=%s' % (path, status, len(resp), grpc_status))
    except Exception as exc:
        log('ERR %s: %s: %s' % (addr, type(exc).__name__, exc))
    finally:
        try:
            client.close()
        except Exception:
            pass


def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(LISTEN)
    srv.listen(128)
    log('LISTEN %s -> %s' % (LISTEN, UPSTREAM))
    while True:
        try:
            client, addr = srv.accept()
        except Exception:
            break
        threading.Thread(target=handle, args=(client, addr), daemon=True).start()


if __name__ == '__main__':
    main()

