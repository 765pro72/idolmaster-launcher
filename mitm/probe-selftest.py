#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""probe-selftest — LLM 探针自测（不碰 18443，不占正式端口）。

验证四件事：
  1. attach 读 probe.json 起回环 HTTP（这里用 SELFTEST_PORT 覆盖端口）
  2. begin / note 记一笔真实样本响应 → meta 档出字节 + sha8 + 字段号集合（不落 body）
  3. POST /probe/level?value=full → 同一样本出明文 hex（decrypt 生效）
  4. 端点齐全：/probe/ping /state /events /request/<seq> /level

用法（包根 = GakumasUnlock）：
    python\\python.exe mitm\\probe-selftest.py
"""
import json
import os
import sys
import urllib.request

try:                                  # 控制台默认 GBK，中文 JSON 会炸
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import probe          # noqa: E402
import unlock         # noqa: E402

PORT = int(os.environ.get('SELFTEST_PORT', '18512'))
BASE = 'http://127.0.0.1:%d' % PORT


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=5) as r:
        return r.read().decode('utf-8', 'replace')


def post(path):
    req = urllib.request.Request(BASE + path, data=b'', method='POST')
    with urllib.request.urlopen(req, timeout=5) as r:
        return r.read().decode('utf-8', 'replace')


def pick_sample():
    """从 mitm/cache 里挑一份最大的录制响应当样本。"""
    cache = os.path.join(HERE, 'cache')
    best = (None, b'')
    for base, _dirs, names in os.walk(cache):
        for n in names:
            if not n.endswith('.json'):
                continue
            try:
                e = json.load(open(os.path.join(base, n), encoding='utf-8'))
                h = e.get('resp_body_hex') or ''
                if len(h) > len(best[1]) * 2:
                    best = (os.path.relpath(os.path.join(base, n), cache), bytes.fromhex(h))
            except Exception:
                continue
    return best


def main():
    out = {'ok': False, 'steps': {}}

    if not probe.attach('grpc', root=ROOT, decrypt=unlock.decrypt_body,
                        fields=unlock.fields, port=PORT):
        out['err'] = 'attach returned False (probe.json 缺 enabled=true ?)'
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 1
    out['steps']['attach'] = True
    out['steps']['port'] = probe.port()

    out['steps']['ping'] = get('/probe/ping')

    name, body = pick_sample()
    out['steps']['sample'] = {'name': name, 'bytes': len(body)}

    seq = probe.begin(7, '/client.api.Selftest/Ping', 0)
    probe.note(seq, 'SELFTEST', body, 200)
    out['steps']['seq'] = seq

    st = json.loads(get('/probe/state'))
    out['steps']['state'] = {'role': st.get('role'), 'level': st.get('level'),
                             'requests': st.get('requests'), 'branches': st.get('branches'),
                             'cache_files': st.get('cache', {}).get('files')}

    ev = get('/probe/events?since=0').strip().splitlines()
    out['steps']['events'] = len(ev)

    det = json.loads(get('/probe/request/%d' % seq))
    out['steps']['detail_meta'] = {'branch': det.get('branch'), 'resp_len': det.get('resp_len'),
                                   'resp_sha8': det.get('resp_sha8'),
                                   'resp_fields': det.get('resp_fields'),
                                   'has_resp_hex': 'resp_hex' in det,
                                   'has_req_hex': 'req_hex' in det}

    post('/probe/level?value=full')
    seq2 = probe.begin(8, '/client.api.Selftest/Full', 0)
    probe.note(seq2, 'SELFTEST-FULL', body, 200)
    det2 = json.loads(get('/probe/request/%d' % seq2))
    out['steps']['detail_full'] = {'level': json.loads(get('/probe/level')).get('level'),
                                   'resp_len': det2.get('resp_len'),
                                   'resp_fields': det2.get('resp_fields'),
                                   'has_resp_hex': bool(det2.get('resp_hex')),
                                   'resp_hex_chars': len(det2.get('resp_hex') or '')}

    post('/probe/level?value=meta')
    out['ok'] = bool(
        out['steps']['ping'].startswith('pong')
        and out['steps']['events'] >= 2
        and out['steps']['detail_meta']['resp_len'] == len(body)
        and out['steps']['detail_meta']['has_resp_hex'] is False
        and out['steps']['detail_full']['has_resp_hex'] is True
    )
    print(json.dumps(out, ensure_ascii=False, indent=1))
    return 0 if out['ok'] else 1


if __name__ == '__main__':
    sys.exit(main())
