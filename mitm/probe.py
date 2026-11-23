#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""probe — Gakumas L1 服务端观测通道（LLM 可见）。

定位
====
把「收到了什么 / 回了什么 / 走了哪条路 / 花了多久」变成可读的回环 HTTP 接口。
纯旁听：不改回放语义、不注入延迟、不做故障注入、只监听回环。

用法（服务端进程内）
====================
    import probe
    probe.attach('grpc', root=PACKAGE_ROOT, decrypt=unlock.decrypt_body, fields=unlock.fields)
    seq = probe.begin(cid, path, len(body))
    probe.note(seq, 'STATIC', rb)
    probe.event('startup', 'listen 18443')

两个角色（各自一个回环端口，互不依赖）
======================================
    role='grpc'   -> cache_server.py   (probe.json server_port，缺省 18502)
    role='proxy'  -> offline_proxy.py  (probe.json proxy_port，缺省 18503)
启动器探针（18500）由 Launcher.cs 提供，不在此列。

端点
====
    GET  /probe/ping                  存活
    GET  /probe/state                 全量快照（计数 / 分支分布 / 缓存体量 / 最近事件）
    GET  /probe/events?since=<seq>    增量事件 NDJSON
    GET  /probe/request/<seq>         单次详情（分支 / 字段集 / 字节 / sha8 / 耗时）
    GET  /probe/level                 当前档位
    POST /probe/level?value=off|meta|full

档位
====
    off   不产事件（端口仍活，state 仍可读）
    meta  默认：只记 path / 分支 / 字节 / sha8 / 字段号集合——不落任何 body
    full  额外记请求体 hex（≤64 KB）与响应明文 hex（≤256 KB，需 decrypt 可用）

配置（包根 probe.json，与启动器共用一份）
=========================================
    {"enabled": true, "port": 18500, "server_port": 18502, "proxy_port": 18503, "level": "meta"}

硬约束
======
  - 只绑 127.0.0.1；不做鉴权（回环即边界）
  - meta 档不落 body；full 档必须显式开
  - 任何内部异常转成 kind=error 事件，不静默吞
"""
import hashlib
import json
import os
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

RING = 800           # 内存事件环上限
DETAIL_KEEP = 400    # 详情表保留条数
REQ_HEX_CAP = 64 * 1024
RESP_HEX_CAP = 256 * 1024
CACHE_STAT_TTL = 5.0

_lock = threading.Lock()
_local = threading.local()

_role = 'grpc'
_root = ''
_seq = 0
_level = 'meta'
_enabled = False
_port = 0
_started = 0.0
_log_path = None
_server = None
_decrypt = None
_fields = None

_events = []        # [(seq, ms, kind, detail_str)]
_details = {}       # seq -> dict
_order = []         # seq 顺序，用于淘汰
_counters = {}      # path -> count
_branches = {}      # branch -> count
_errors = 0
_requests = 0
_cache_stat = {'files': 0, 'bytes': 0, 'at': 0.0}


# ---------------------------------------------------------------- 基础工具

def _now_ms():
    return int(time.time() * 1000)


def _sha8(b):
    try:
        return hashlib.sha256(b).hexdigest()[:8]
    except Exception:
        return ''


def _field_ids(b):
    """用调用方注入的 fields() 取 protobuf 字段号：顶层 → 一层嵌套。

    只给顶层字段号没有观测价值（gRPC 响应普遍是 f1 里面套全部数据），
    因此对 wiretype=2 的字段再展开一层，输出 {"1": [子字段号...], "2": null}。
    """
    if _fields is None or not b:
        return None
    try:
        out = {}
        for t in _fields(b):
            fn = int(t[0])
            wt = int(t[1])
            out[str(fn)] = _nested_ids(t[4]) if (wt == 2 and len(t) >= 5) else None
        return out
    except Exception:
        return None


def _nested_ids(sub):
    """把 wiretype=2 的字节体当子消息解析——只在自洽时返回字段号。

    packed repeated 标量（例如一排 int64 ID）也会「解析成功」并吐出大整数，
    因此加两道闸：字段号 1..1000、按 raw_slice 拼回必须逐字节等于原串。
    认不出就返回 None——宁可不报，不报假的。
    """
    if not sub:
        return None
    try:
        fs = _fields(sub)
    except Exception:
        return None
    if not fs or len(fs) > 64:
        return None
    ids = []
    for t in fs:
        fn = int(t[0])
        if fn <= 0 or fn > 1000:
            return None
        ids.append(fn)
    try:
        if b''.join(t[2] for t in fs) != sub:
            return None
    except Exception:
        return None
    return sorted(set(ids))


def _plain(b):
    """尝试解密成明文 protobuf；失败原样返回（调用方不再区分）。"""
    if _decrypt is None or not b:
        return None
    try:
        return _decrypt(b)
    except Exception:
        return None


def _add_event(kind, detail):
    global _seq
    with _lock:
        _seq += 1
        seq = _seq
        line = detail if isinstance(detail, str) else json.dumps(detail, ensure_ascii=False)
        _events.append((seq, _now_ms(), kind, line))
        if len(_events) > RING:
            del _events[:len(_events) - RING]
        path = _log_path
    if path:
        try:
            with open(path, 'a', encoding='utf-8') as f:
                f.write(json.dumps({'seq': seq, 'pid': os.getpid(), 'ms': _now_ms(),
                                    'kind': kind, 'detail': line}, ensure_ascii=False) + '\n')
        except Exception:
            pass
    return seq


def _add_detail(d):
    with _lock:
        _details[d['seq']] = d
        _order.append(d['seq'])
        while len(_order) > DETAIL_KEEP:
            old = _order.pop(0)
            _details.pop(old, None)


# ---------------------------------------------------------------- 配置

def _read_config(root):
    """读包根 probe.json；缺文件 = 关闭。键缺失走缺省。"""
    cfg = {'enabled': False, 'port': 18500, 'server_port': 18502,
           'proxy_port': 18503, 'level': 'meta'}
    path = os.path.join(root, 'probe.json')
    if not os.path.isfile(path):
        return cfg
    try:
        with open(path, encoding='utf-8-sig') as f:
            data = json.load(f)
        if isinstance(data, dict):
            for k in cfg:
                if k in data:
                    cfg[k] = data[k]
    except Exception:
        pass
    return cfg


def _resolve_port(cfg, role):
    env = os.environ.get('GKM_PROBE_PORT', '').strip()
    if env.isdigit():
        return int(env)
    key = 'server_port' if role == 'grpc' else 'proxy_port'
    try:
        v = int(cfg.get(key) or 0)
    except Exception:
        v = 0
    return v or (18502 if role == 'grpc' else 18503)


# ---------------------------------------------------------------- 记录面

def begin(cid, path, body_len=0, headers=None):
    """一次请求的开始。返回 seq（未启用 / off 档返回 None）。"""
    global _requests
    if not _enabled or _level == 'off':
        return None
    with _lock:
        _requests += 1
        n = _requests
        _counters[path] = _counters.get(path, 0) + 1
    d = {'seq': _add_event('request', {'cid': cid, 'path': path, 'req_len': body_len,
                                       'n': n, 't': _now_ms()}),
         'cid': str(cid), 'path': path, 'req_len': body_len, 'status': 200,
         'branch': '', 'resp_len': 0, 'resp_sha8': '', 'req_fields': None,
         'resp_fields': None, 'ms': 0, 't0': time.time()}
    if _level == 'full' and body_len:
        d['req_hex'] = None   # 由调用方经 note_req 补
    _local.seq = d['seq']
    _add_detail(d)
    return d['seq']


def note_req(seq, body):
    """full 档记录请求体（截断到 REQ_HEX_CAP）。"""
    if not seq or _level != 'full' or not body:
        return
    try:
        d = _details.get(seq)
        if d is None:
            return
        d['req_hex'] = body[:REQ_HEX_CAP].hex()
        d['req_fields'] = _field_ids(body)
    except Exception:
        _fail(seq, 'note_req')


def note(seq, branch, body=None, status=200, extra=None):
    """一次请求的收尾：分支 + 响应体指纹（meta 不落 body）。"""
    if not seq:
        return
    try:
        d = _details.get(seq)
        if d is None:
            return
        d['branch'] = branch or d.get('branch') or ''
        d['status'] = status
        d['ms'] = int((time.time() - d.get('t0', time.time())) * 1000)
        if body:
            d['resp_len'] = len(body)
            d['resp_sha8'] = _sha8(body)
            pt = _plain(body)
            if pt is not None:
                d['resp_fields'] = _field_ids(pt)
                if _level == 'full':
                    d['resp_hex'] = pt[:RESP_HEX_CAP].hex()
            else:
                d['resp_fields'] = _field_ids(body)
        if extra:
            d['extra'] = extra
        with _lock:
            _branches[d['branch'] or 'none'] = _branches.get(d['branch'] or 'none', 0) + 1
        _add_event('response', {'seq': seq, 'path': d['path'], 'branch': d['branch'],
                                'len': d['resp_len'], 'sha8': d['resp_sha8'],
                                'fields': d['resp_fields'], 'ms': d['ms'],
                                'status': status})
    except Exception:
        _fail(seq, 'note')


def tag(name):
    """给当前线程正在处理的那笔请求打分支标签（不改计数，note 时统一计）。"""
    seq = getattr(_local, 'seq', None)
    if not seq or not _enabled:
        return
    try:
        with _lock:
            d = _details.get(seq)
        if d is not None and not d.get('resp_len'):
            d['branch'] = name
    except Exception:
        _fail(seq, 'tag')


def event(kind, detail):
    """任意旁听事件（startup / error / write / resource ...）。"""
    if not _enabled or _level == 'off':
        return
    if isinstance(detail, dict):
        detail = dict(detail)
        detail.setdefault('t', _now_ms())
    _add_event(kind, detail)


def tick():
    """保底：清掉 begin 之后从未 note 的悬空详情耗时统计。"""
    if not _enabled:
        return
    try:
        now = time.time()
        for seq in list(_order):
            d = _details.get(seq)
            if d is None:
                continue
            if not d.get('branch') and (now - d.get('t0', now)) > 30:
                d['branch'] = 'pending'
                d['ms'] = int((now - d.get('t0', now)) * 1000)
    except Exception:
        pass


def _fail(seq, where):
    global _errors
    with _lock:
        _errors += 1
    try:
        _add_event('error', {'where': where, 'seq': seq,
                             'err': traceback.format_exc(limit=2)[-400:]})
    except Exception:
        pass


# ---------------------------------------------------------------- 快照

def _scan_cache(cachedir):
    now = time.time()
    if now - _cache_stat['at'] < CACHE_STAT_TTL:
        return _cache_stat
    files = 0
    size = 0
    try:
        for base, _dirs, names in os.walk(cachedir):
            for n in names:
                try:
                    size += os.path.getsize(os.path.join(base, n))
                    files += 1
                except Exception:
                    pass
    except Exception:
        pass
    _cache_stat.update({'files': files, 'bytes': size, 'at': now})
    return _cache_stat


def state():
    with _lock:
        ev = list(_events[-40:])
        counters = dict(_counters)
        branches = dict(_branches)
        seq = _seq
        details = dict(_details)
    cs = _scan_cache(os.path.join(_root or '', 'mitm', 'cache'))
    out = {
        'ok': True,
        'role': _role,
        'enabled': _enabled,
        'level': _level,
        'port': _port,
        'pid': os.getpid(),
        'uptime_s': round(time.time() - _started, 1) if _started else 0,
        'seq': seq,
        'requests': _requests,
        'errors': _errors,
        'counters': dict(sorted(counters.items(), key=lambda kv: -kv[1])),
        'branches': dict(sorted(branches.items(), key=lambda kv: -kv[1])),
        'cache': {'files': cs['files'], 'bytes': cs['bytes']},
        'details_kept': len(details),
        'recent': [{'seq': s, 'ms': m, 'kind': k, 'detail': d} for s, m, k, d in ev],
    }
    return out


# ---------------------------------------------------------------- HTTP 面

class _Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    server_version = 'gkm-probe/1'

    def log_message(self, fmt, *args):
        return

    def _send(self, code, ctype, body):
        if isinstance(body, str):
            body = body.encode('utf-8')
        try:
            self.send_response(code)
            self.send_header('Content-Type', ctype)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Connection', 'close')
            self.end_headers()
            self.wfile.write(body)
        except Exception:
            pass

    def _json(self, obj, code=200):
        self._send(code, 'application/json; charset=utf-8',
                   json.dumps(obj, ensure_ascii=False, indent=1))

    def do_GET(self):
        path, _, query = self.path.partition('?')
        if path == '/probe/ping':
            self._send(200, 'text/plain; charset=utf-8',
                       'pong %s role=%s' % (time.strftime('%H:%M:%S'), _role))
            return
        if path == '/probe/state':
            self._json(state())
            return
        if path == '/probe/events':
            since = 0
            for part in query.split('&'):
                if part.startswith('since='):
                    try:
                        since = int(part[6:])
                    except Exception:
                        since = 0
            with _lock:
                rows = [e for e in _events if e[0] > since]
            body = ''.join(json.dumps({'seq': s, 'ms': m, 'kind': k, 'detail': d},
                                      ensure_ascii=False) + '\n' for s, m, k, d in rows)
            self._send(200, 'application/x-ndjson; charset=utf-8', body)
            return
        if path.startswith('/probe/request/'):
            try:
                seq = int(path.rsplit('/', 1)[-1])
            except Exception:
                self._json({'ok': False, 'err': 'bad seq'}, 400)
                return
            d = _details.get(seq)
            if d is None:
                self._json({'ok': False, 'err': 'seq not kept', 'seq': seq,
                            'kept': list(_details)[:5]}, 404)
                return
            out = {k: v for k, v in d.items()}
            out['ok'] = True
            self._json(out)
            return
        if path == '/probe/level':
            self._json({'ok': True, 'level': _level})
            return
        self._send(404, 'text/plain; charset=utf-8',
                   'routes: /probe/ping /probe/state /probe/events?since= '
                   '/probe/request/<seq> /probe/level')

    def do_POST(self):
        global _level
        path, _, query = self.path.partition('?')
        if path == '/probe/level':
            value = ''
            for part in query.split('&'):
                if part.startswith('value='):
                    value = part[6:]
            ln = 0
            try:
                ln = int(self.headers.get('Content-Length') or 0)
            except Exception:
                ln = 0
            if ln:
                try:
                    body = self.rfile.read(min(ln, 4096)).decode('utf-8', 'replace')
                    for part in body.replace('&', ' ').split():
                        if part.startswith('value='):
                            value = part[6:]
                except Exception:
                    pass
            value = value.strip().lower()
            if value not in ('off', 'meta', 'full'):
                self._json({'ok': False, 'err': 'value must be off|meta|full'}, 400)
                return
            _level = value
            event('level', {'level': value})
            self._json({'ok': True, 'level': value})
            return
        self._send(404, 'text/plain; charset=utf-8', 'routes: POST /probe/level?value=')


# ---------------------------------------------------------------- 装配

def attach(role, root=None, decrypt=None, fields=None, port=None):
    """装配探针。返回是否启用（False = probe.json 缺 enabled / 关闭）。"""
    global _role, _root, _enabled, _port, _level, _started, _log_path, _decrypt, _fields, _server
    _role = role
    _root = root or os.getcwd()
    _decrypt = decrypt
    _fields = fields
    cfg = _read_config(_root)
    try:
        lvl = str(cfg.get('level') or 'meta').lower()
    except Exception:
        lvl = 'meta'
    _level = lvl if lvl in ('off', 'meta', 'full') else 'meta'
    if not cfg.get('enabled'):
        return False
    _port = int(port or _resolve_port(cfg, role))
    _enabled = True
    _started = time.time()
    d = os.path.join(_root, 'logs', 'probe')
    try:
        os.makedirs(d, exist_ok=True)
        _log_path = os.path.join(d, 'events-%s.ndjson' % role)
    except Exception:
        _log_path = None
    last = None
    for attempt in range(10):
        try:
            srv = ThreadingHTTPServer(('127.0.0.1', _port + attempt), _Handler)
            srv.daemon_threads = True
            _server = srv
            _port = _port + attempt
            t = threading.Thread(target=srv.serve_forever, name='probe-%s' % role, daemon=True)
            t.start()
            if attempt:
                event('port fallback', {'role': role, 'port': _port})
            event('startup', {'role': role, 'port': _port, 'level': _level,
                              'pid': os.getpid()})
            return True
        except Exception as e:
            last = e
            continue
    _enabled = False
    try:
        sys.stderr.write('[probe] listen failed on %s: %s\n' % (role, last))
    except Exception:
        pass
    return False


def enabled():
    return _enabled


def port():
    return _port
