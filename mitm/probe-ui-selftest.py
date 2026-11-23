#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""probe-ui-selftest — 启动器探针（L2）可操作面自测。

前置：学マス启动器.exe 已启动，probe.json enabled=true（端口 18500）。
只点一个无害按钮（优先「刷新」），其余端点只读。

用法：
    python\\python.exe -X utf8=1 -u mitm\\probe-ui-selftest.py
"""
import json
import os
import sys
import urllib.parse
import urllib.request

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass


def _port():
    if len(sys.argv) > 1:
        return int(sys.argv[1])
    try:
        p = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'probe.json')
        return int(json.load(open(p, encoding='utf-8-sig')).get('port') or 18510)
    except Exception:
        return 18510


BASE = 'http://127.0.0.1:%d' % _port()
out = {'ok': False, 'steps': {}}


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=8) as r:
        return r.read().decode('utf-8', 'replace')


def main():
    try:
        st = json.loads(get('/state'))
    except Exception as e:
        out['err'] = 'state unreachable: %r (启动器没起来 / probe.json 未启用)' % e
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 1
    out['steps']['ping'] = get('/ping')
    out['steps']['state'] = {'page': st.get('app', {}).get('page'),
                             'screen': st.get('app', {}).get('screen'),
                             'buttons': [b.get('text') for b in st.get('buttons', [])],
                             'gameRunning': st.get('gameRunning'),
                             'serviceRunning': st.get('serviceRunning')}

    # 1) 按钮通道：只点无害按钮（刷新 / 服务器详情——可随时切回）
    clickable = None
    for b in st.get('buttons', []):
        if not b.get('enabled'):
            continue
        label = b.get('text') or ''
        if '刷新' in label or '服务器详情' in label:
            clickable = label
            break
    if clickable:
        out['steps']['action'] = get('/action?text=' + urllib.parse.quote(clickable))
    else:
        out['steps']['action'] = 'no harmless button found (skipped)'

    # 2) 合成点击：在启动器窗口内部偏下位置点一下（无控件处 = 无副作用）
    screen = st.get('app', {}).get('screen') or [0, 0, 0, 0]
    x = int(screen[0]) + int(screen[2]) // 2
    y = int(screen[1]) + int(screen[3]) - 12
    out['steps']['click'] = get('/click?x=%d&y=%d' % (x, y))

    # 3) 键盘：F13（0x7C）——绝大多数程序无绑定
    out['steps']['key'] = get('/key?vk=124')

    # 4) 游戏窗口：没开游戏时应诚实报 not found
    out['steps']['focus'] = get('/focus?target=game')

    ev = get('/events?since=0').strip().splitlines()
    kinds = []
    for line in ev:
        try:
            kinds.append(json.loads(line).get('kind'))
        except Exception:
            pass
    out['steps']['events'] = kinds[-8:]

    out['ok'] = ('queued' in out['steps']['action'] or 'skipped' in out['steps']['action']) \
        and '"ok":true' in out['steps']['click'] \
        and '"ok":true' in out['steps']['key'] \
        and bool(out['steps']['events'])
    text = json.dumps(out, ensure_ascii=False, indent=1)
    try:
        d = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'logs', 'probe')
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, 'ui-selftest.last.json'), 'w', encoding='utf-8') as f:
            f.write(text)
    except Exception:
        pass
    print(text)
    return 0 if out['ok'] else 1


if __name__ == '__main__':
    sys.exit(main())
