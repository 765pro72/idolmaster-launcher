#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""probe-smoke — 真链路冒烟：起一份 cache_server（18443），回放一条真实请求，
验证探针挂钩（begin → 分支 → note）在真实 gRPC 流量下成立。

不依赖游戏、不碰系统代理。结束后进程内退出。
前置：18443 / 18444 / 18502 空闲（游戏或服务在跑时本脚本会明确报错退出）。

用法（包根 = GakumasUnlock）：
    python\\python.exe -X utf8=1 -u mitm\\probe-smoke.py
"""
import json
import os
import socket
import ssl
import sys
import threading
import time

try:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

os.environ.setdefault('GKM_MODE', 'offline')
os.environ.setdefault('GKM_STATIC_USER', '1')
os.environ.setdefault('GKM_MATCH', 'exact')
os.environ.setdefault('GKM_MITM_PORT', '18443')
os.environ.setdefault('GKM_H2C_PORT', '18444')

out = {'ok': False, 'steps': {}}


def free(port):
    s = socket.socket()
    s.settimeout(0.5)
    try:
        return s.connect_ex(('127.0.0.1', port)) != 0
    finally:
        s.close()


def pick_record():
    rec = os.path.join(HERE, 'records')
    best = None
    for n in sorted(os.listdir(rec)) if os.path.isdir(rec) else []:
        if not n.endswith('.json'):
            continue
        try:
            d = json.load(open(os.path.join(rec, n), encoding='utf-8'))
            body = bytes.fromhex(d.get('req_body_hex') or '')
            if d.get('path') and body and (best is None or len(body) < len(best[1])):
                best = (d['path'], body, n)
        except Exception:
            continue
    return best


def main():
    for p in (18443, 18444):
        if not free(p):
            out['err'] = 'port %d already in use — stop the running service first' % p
            print(json.dumps(out, ensure_ascii=False, indent=1))
            return 1
    rec = pick_record()
    if not rec:
        out['err'] = 'no record with req_body_hex under mitm/records'
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 1
    path, req_body, recname = rec
    out['steps']['record'] = {'file': recname, 'path': path, 'req_bytes': len(req_body)}

    import cache_server            # 环境变量已就位，导入即定型
    threading.Thread(target=cache_server.main, daemon=True).start()
    for _ in range(40):
        time.sleep(0.25)
        if not free(18443):
            break
    out['steps']['service_up'] = not free(18443)
    if not out['steps']['service_up']:
        out['err'] = 'cache_server did not bind 18443'
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 1

    import h2.config
    import h2.connection
    import h2.events

    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    ctx.set_alpn_protocols(['h2'])
    sock = socket.create_connection(('127.0.0.1', 18443), timeout=10)
    tls = ctx.wrap_socket(sock, server_hostname='api.game-gakuen-idolmaster.jp')
    cfg = h2.config.H2Configuration(client_side=True, header_encoding='utf-8')
    conn = h2.connection.H2Connection(config=cfg)
    conn.initiate_connection()
    tls.sendall(conn.data_to_send())
    conn.send_headers(1, [(':method', 'POST'), (':scheme', 'https'),
                          (':authority', 'api.game-gakuen-idolmaster.jp'),
                          (':path', path), ('content-type', 'application/grpc'),
                          ('te', 'trailers')])
    conn.send_data(1, req_body, end_stream=True)
    tls.sendall(conn.data_to_send())

    body = b''
    status = None
    tls.settimeout(10)
    done = False
    while not done:
        data = tls.recv(65536)
        if not data:
            break
        for ev in conn.receive_data(data):
            if isinstance(ev, h2.events.ResponseReceived):
                status = dict(ev.headers).get(':status')
            elif isinstance(ev, h2.events.DataReceived):
                body += ev.data
                conn.acknowledge_received_data(ev.flow_controlled_length, ev.stream_id)
            elif isinstance(ev, h2.events.StreamEnded):
                done = True
        tls.sendall(conn.data_to_send())
    out['steps']['response'] = {'status': status, 'bytes': len(body)}
    try:
        tls.close()
    except Exception:
        pass

    time.sleep(0.4)
    import urllib.request
    st = json.loads(urllib.request.urlopen('http://127.0.0.1:%d/probe/state' % _probe_port(),
                                          timeout=5).read().decode('utf-8'))
    out['steps']['probe'] = {'enabled': st.get('enabled'), 'requests': st.get('requests'),
                             'branches': st.get('branches'),
                             'counter_for_path': st.get('counters', {}).get(path),
                             'recent_kinds': [e['kind'] for e in st.get('recent', [])][-6:]}
    out['ok'] = bool(status == '200' and len(body) > 0
                     and out['steps']['probe']['counter_for_path'] == 1
                     and out['steps']['probe']['branches'])
    print(json.dumps(out, ensure_ascii=False, indent=1))
    return 0 if out['ok'] else 1


def _probe_port():
    try:
        cfg = json.load(open(os.path.join(ROOT, 'probe.json'), encoding='utf-8-sig'))
        return int(cfg.get('server_port') or 18502)
    except Exception:
        return 18502


if __name__ == '__main__':
    code = main()
    sys.stdout.flush()
    os._exit(code)
