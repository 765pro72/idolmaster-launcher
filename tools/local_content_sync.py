"""从已下载缓存同步内容；不启动游戏，不访问网络，不修改系统代理。"""
import argparse
from collections import Counter
from contextlib import contextmanager
from datetime import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import struct
import sys
import uuid

from Crypto.Cipher import AES

TARGETS = ('IdolCard', 'IdolCardSkin', 'Costume', 'CostumeHead', 'Music',
           'SupportCard', 'Item', 'ProduceStory', 'ProduceCard', 'Produce',
           'MeishiBaseAsset', 'PhotoPose', 'PhotoBackground', 'Badge',
           'MeishiIllustrationAsset', 'ProduceItem', 'ProduceDrink', 'ShopItem', 'Story')
LABELS = {'IdolCard': 'P卡本体', 'IdolCardSkin': 'P偶像版本', 'Costume': '服装',
          'CostumeHead': '头饰', 'Music': '音乐', 'SupportCard': '支援卡',
          'Story': '剧情', 'ProduceStory': '培育剧情'}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def load_module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=1) + '\n').encode('utf-8')


def sqlite_varint(data, pos):
    value = 0
    for i in range(9):
        byte = data[pos]
        pos += 1
        if i == 8:
            return (value << 8) | byte, pos
        value = (value << 7) | (byte & 127)
        if byte < 128:
            return value, pos
    raise ValueError('invalid SQLite varint')


def valid_database(data):
    # 缓存的页首另有加密头；不交给 sqlite，也不修改原始缓存。
    return (len(data) >= 4096 and len(data) % 4096 == 0
            and data[44:48] == b'\0\0\0\4' and data[56:60] == b'\0\0\0\1'
            and data[100] in (5, 13) and b'CREATE TABLE' in data)


def records(data, wire):
    """校验 SQLite 行头、列长度和 protobuf 主键，避免把引用 ID 当成本表记录。"""
    result = {}
    for match in re.finditer(rb'\x0a([\x03-\x78])', data):
        ident_size = match.group(1)[0]
        ident = data[match.end():match.end() + ident_size]
        if not re.fullmatch(rb'[A-Za-z][A-Za-z0-9_-]{2,120}', ident):
            continue
        column = data.rfind(ident, max(0, match.start() - len(ident) - 64), match.start())
        if column < 0:
            continue
        end = column + len(ident)
        marker = bytes((10, len(ident))) + ident
        if data.find(marker, end, end + len(marker) + 64) < 0:
            continue
        for start in range(max(0, column - 24), column):
            try:
                length, pos = sqlite_varint(data, start)
                _, pos = sqlite_varint(data, pos)
                payload_start = pos
                header_size, pos = sqlite_varint(data, pos)
                serials = []
                header_end = payload_start + header_size
                while pos < header_end and len(serials) < 32:
                    serial, pos = sqlite_varint(data, pos)
                    serials.append(serial)
                if pos != column or header_end != pos or len(serials) < 2:
                    continue
                if serials[0] != len(ident) * 2 + 13 or serials[-1] < 12 or serials[-1] % 2:
                    continue
                sizes = [(serial - 12) // 2 if serial >= 12 else
                         {0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 6, 6: 8, 7: 8, 8: 0, 9: 0}[serial]
                         for serial in serials]
                blob_size = sizes[-1]
                if length != header_size + sum(sizes):
                    continue
                blob_start = end + sum(sizes[1:-1])
                if data[blob_start:blob_start + len(marker)] != marker:
                    continue
                blob = data[blob_start:blob_start + blob_size]
                parsed = wire.fields(blob)
                if not any(f == 1 and w == 2 and v == ident for f, w, r, l, v in parsed):
                    continue
                result[ident.decode('ascii')] = blob
                break
            except (ValueError, KeyError, IndexError, UnicodeError):
                continue
    return result


def read_manifests(mitm, wire):
    paths = list((mitm / 'records').glob('*Master_Get*.json'))
    paths += list((mitm / 'cache/client.api.Master_Get').glob('*.json'))
    manifests = []
    seen = set()
    for path in sorted(paths, key=lambda p: p.stat().st_mtime_ns, reverse=True):
        try:
            obj = json.loads(path.read_text('utf-8-sig'))
            encrypted = bytes.fromhex(obj['resp_body_hex'])
            identity = digest(encrypted)
            if identity in seen:
                continue
            plain = wire.decrypt_body(encrypted)
            container = next(v for f, w, r, l, v in wire.fields(plain) if f == 1 and w == 2)
            tables = {}
            for f, w, r, l, v in wire.fields(container):
                if w != 2:
                    continue
                try:
                    row = {k: x for k, t, rr, ll, x in wire.fields(v)}
                    name = row[1].decode()
                    key = bytes.fromhex(row[4].decode())
                    if len(key) == 32:
                        tables[name] = key
                except (ValueError, KeyError, AttributeError, UnicodeError):
                    continue
            if len(tables) >= 100:
                manifests.append((identity, tables))
                seen.add(identity)
        except (OSError, ValueError, KeyError, TypeError, StopIteration, UnicodeError):
            continue
    if not manifests:
        raise ValueError('没有可用的本地 master 清单；原数据未改动。请先完成一次官方资源更新。')
    return manifests


def read_tables(cache, manifests, wire, selected_paths=None):
    if not cache.is_dir():
        raise ValueError('未找到已下载的 master 缓存：' + str(cache))
    index = {}
    for path in cache.iterdir():
        if path.is_file() and '_' in path.name:
            index.setdefault(path.name.split('_', 1)[0].upper(), []).append(path)
    result, source = {}, {}
    for name in TARGETS:
        prefix = hashlib.md5(name.encode()).hexdigest().upper()
        candidates = ([cache / selected_paths[name]] if selected_paths is not None else sorted(index.get(prefix, []), key=lambda p: p.stat().st_mtime_ns, reverse=True))
        keys = list(dict.fromkeys(tables[name] for _, tables in manifests if name in tables))
        chosen = None
        for path in candidates:
            encrypted = path.read_bytes()
            if len(encrypted) % 16:
                continue
            for key in keys:
                plain = AES.new(key, AES.MODE_CBC, bytes(16)).decrypt(encrypted)
                if valid_database(plain):
                    chosen = (path, plain, digest(encrypted))
                    break
            if chosen:
                break
        if not chosen:
            raise ValueError(name + ' 缓存缺失、未下载完整或本地密钥不匹配；原数据未改动。')
        path, plain, identity = chosen
        if candidates[0] != path:
            raise ValueError(name + ' 最新缓存无法验证，拒绝用旧版本假报更新成功。')
        rows = records(plain, wire)
        if not rows:
            raise ValueError(name + ' 未能解析出有效记录，已停止同步。')
        result[name] = rows
        source[name] = {'file': path.name, 'sha256': identity, 'parsed': len(rows)}
    return result, source


def allowed(name, ident):
    if name == 'Item':
        return ident.startswith(('item-', 'item_'))
    if name == 'ProduceCard':
        return ident.startswith('p_card-')
    if name in ('PhotoPose', 'PhotoBackground') and 'demon' in ident:
        return False
    return True


def prepare_indexes(mitm, staged, tables, wire):
    payloads, changes, skipped, snapshots = {}, {}, [], {}

    def put(relative, value):
        target = mitm / relative
        old = target.read_bytes() if target.is_file() else None
        if old is not None:
            try:
                if json.loads(old.decode('utf-8-sig')) == value:
                    raw = old
                else:
                    raw = json_bytes(value)
            except (ValueError, UnicodeError):
                raise ValueError(str(relative) + ' 现有文件损坏；已停止同步。')
        else:
            raw = json_bytes(value)
        dest = staged / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(raw)
        if raw != old:
            key = Path('mitm') / relative
            payloads[key] = raw
            snapshots[key] = old

    def old_json(relative, default):
        path = mitm / relative
        return json.loads(path.read_text('utf-8-sig')) if path.is_file() else default

    merged = {}
    for name in TARGETS:
        relative = Path('masterTrans') / (name + '.json')
        obj = old_json(relative, {'data': []})
        existing = [row['id'] for row in obj.get('data', [])]
        existing_set = set(existing)
        additions = [ident for ident in tables[name] if ident not in existing_set and allowed(name, ident)]
        skipped.extend({'table': name, 'id': ident, 'reason': 'existing_compatibility_exclusion'}
                       for ident in tables[name] if ident not in existing_set and not allowed(name, ident))
        obj['data'] = list(obj.get('data', [])) + [{'id': ident} for ident in additions]
        merged[name] = existing + additions
        changes[name] = len(additions)
        put(relative, obj)

    def merge_lists(filename, values):
        obj = old_json(Path(filename), {} if isinstance(values, dict) else [])
        if isinstance(values, dict):
            for key, ids in values.items():
                obj[key] = list(dict.fromkeys(list(obj.get(key) or []) + list(ids)))
        else:
            obj = list(dict.fromkeys(list(obj) + list(values)))
        put(Path(filename), obj)

    merge_lists('live-cards.json', {'cards': merged['IdolCard'], 'skins': merged['IdolCardSkin']})
    merge_lists('live-costumes.json', {'costumes': merged['Costume'], 'heads': merged['CostumeHead']})
    merge_lists('live-extra.json', {'support': merged['SupportCard'], 'items': merged['Item']})
    merge_lists('live-pstories.json', merged['ProduceStory'])
    merge_lists('live-photo.json', {'poses': merged['PhotoPose'], 'bgs': merged['PhotoBackground']})
    merge_lists('badge-ids.json', merged['Badge'])
    playable = [x for x in merged['Music'] if re.match(r'^music-(all|char|unit)-', x) and not x.endswith('-inst')]
    merge_lists('music-ids.json', {'every': playable})
    mapping = old_json(Path('idol-card-skin-map.json'), {})
    for ident, blob in tables['IdolCardSkin'].items():
        parent = next((v.decode() for f, w, r, l, v in wire.fields(blob) if f == 2 and w == 2), None)
        if parent:
            mapping[ident] = parent
    put(Path('idol-card-skin-map.json'), mapping)
    return payloads, changes, skipped, snapshots


def user_data(plain, wire):
    return next(v for f, w, r, l, v in wire.fields(plain) if f == 1 and w == 2)


def append_unlocks(original, generated, wire):
    old = user_data(original, wire)
    whitelist = wire.UNLOCK_LIST_FIELDS | {wire.F_BADGE, wire.F_PHOTO_POSE, wire.F_PHOTO_BG}
    composite = {wire.F_PRODUCE_LIVE, wire.F_PRODUCE_CHARACTER, wire.F_PRODUCE_IDOLCARD,
                 wire.F_DEARNESS_STORY_LEVEL, wire.F_CHARACTER_COSTUME}

    def key(tag, entry):
        values = {f: (w, v if w == 2 else length)
                  for f, w, raw, length, v in wire.fields(entry) if f in (2, 3)}
        if 2 not in values:
            return (tag, 'opaque', digest(entry))
        return (tag, values[2], values.get(3) if tag in composite else None)

    known = {key(f, v) for f, w, r, l, v in wire.fields(old) if f in whitelist and w == 2}
    additions = bytearray()
    counts = Counter()
    for f, w, raw, length, value in wire.fields(user_data(generated, wire)):
        if f not in whitelist or w != 2:
            continue
        identity = key(f, value)
        if identity in known:
            continue
        known.add(identity)
        additions += raw
        counts[str(f)] += 1
    if not additions:
        return original, counts
    merged = old + bytes(additions)
    result = b''.join(wire._ld(f, merged) if f == 1 and w == 2 else raw
                      for f, w, raw, length, value in wire.fields(original))
    assert user_data(result, wire).startswith(old)
    return result, counts


@contextmanager
def lock(root):
    import msvcrt
    path = root / 'out/local-sync.lock'
    path.parent.mkdir(exist_ok=True)
    with path.open('a+b') as stream:
        stream.seek(0)
        if not stream.read(1):
            stream.write(b'0')
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            raise ValueError('已有同步任务正在运行，请等待该任务完成。')
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def transaction_path(root, relative):
    target = (root / relative).resolve()
    target.relative_to((root / 'mitm').resolve())
    return target


def recover_transaction(root):
    pending = root / 'out/local-sync-transaction'
    if not pending.exists():
        return
    journal = pending / 'journal.json'
    if not journal.is_file():
        # No destination is written until a complete journal exists.
        shutil.rmtree(pending)
        return
    state = json.loads(journal.read_text('utf-8'))
    if state['status'] != 'committed':
        for row in reversed(state['files']):
            target = transaction_path(root, row['path'])
            current = target.read_bytes() if target.is_file() else None
            current_hash = digest(current) if current is not None else None
            if current_hash not in (row['before'], row['after']):
                raise ValueError('待恢复文件已被其他操作修改，请保留临时回退数据：' + row['path'])
            if row['before'] is None:
                if target.exists():
                    target.unlink()
            else:
                saved = pending / 'before' / row['path']
                content = saved.read_bytes()
                if digest(content) != row['before']:
                    raise ValueError('临时回退数据校验失败：' + row['path'])
                temporary = target.with_name(target.name + '.restore-tmp')
                temporary.write_bytes(content)
                os.replace(temporary, target)
    for row in state['files']:
        target = transaction_path(root, row['path'])
        target.with_name(target.name + '.sync-tmp').unlink(missing_ok=True)
        target.with_name(target.name + '.restore-tmp').unlink(missing_ok=True)
    shutil.rmtree(pending)


def apply_transaction(root, payloads, before, job):
    pending = root / 'out/local-sync-transaction'
    pending.mkdir()
    state = {'status': 'applying', 'files': []}
    for relative in payloads:
        target = transaction_path(root, relative)
        current = target.read_bytes() if target.is_file() else None
        if current != before[relative]:
            raise ValueError('同步期间文件被其他操作修改，已停止：' + str(relative))
        if current is not None:
            saved = pending / 'before' / relative
            saved.parent.mkdir(parents=True, exist_ok=True)
            saved.write_bytes(current)
        state['files'].append({'path': str(relative), 'before': digest(current) if current is not None else None,
                               'after': digest(payloads[relative])})
    journal = pending / 'journal.json'
    temporary = journal.with_suffix('.tmp')
    temporary.write_bytes(json_bytes(state))
    os.replace(temporary, journal)
    try:
        for relative, content in payloads.items():
            target = root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(target.name + '.sync-tmp')
            temporary.write_bytes(content)
            os.replace(temporary, target)
            if target.read_bytes() != content:
                raise OSError('写入后校验失败：' + str(relative))
    except Exception:
        recover_transaction(root)
        raise
    state['status'] = 'committed'
    temporary.write_bytes(json_bytes(state))
    os.replace(temporary, journal)
    recover_transaction(root)


def synchronize(root, cache, dry_run=False):
    root = Path(root).resolve()
    mitm = root / 'mitm'
    if not (mitm / 'unlock.py').is_file() or not (mitm / 'static_userget.bin').is_file():
        raise ValueError('缺少解锁组件或现役存档，请检查安装目录。')
    with lock(root):
        if not dry_run:
            recover_transaction(root)
        elif (root / 'out/local-sync-transaction').exists():
            raise ValueError('上次写入被中断，请正常运行一键更新恢复后再预览。')
        lifecycle = load_module(mitm / 'master_lifecycle.py', 'gkm_master_lifecycle')
        state = lifecycle.read_pending(root) if dry_run else lifecycle.recover_preparing(root)
        if not dry_run and state and state['status']=='confirmed':
            lifecycle.cleanup_confirmed(root,load_module(mitm/'unlock.py','gkm_cleanup_wire'))
            state=None
        if state:
            raise ValueError('上次 master 等待游戏进入大厅确认，或等待清理；只保留这一份旧版，请勿重复更新。需要时可回退未确认更新。')
        job = root / 'out/local-sync-jobs' / (datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8])
        staged = job / 'staging/mitm'
        staged.mkdir(parents=True)
        report = {'format': 1, 'operation': 'local_sync', 'status': 'running', 'dry_run': dry_run}
        try:
            print('[1/4] 检查已下载的本地数据……', flush=True)
            shutil.copy2(mitm / 'unlock.py', staged / 'unlock.py')
            wire = load_module(staged / 'unlock.py', 'gkm_sync_unlock')
            old_blob = (mitm / 'static_userget.bin').read_bytes()
            old_plain = wire.decrypt_body(old_blob)
            if old_plain is None:
                raise ValueError('现役存档无法解密，已停止同步。')
            user_data(old_plain, wire)
            game_file = root / 'game_path.txt'
            if not game_file.is_file():
                raise ValueError('请先在启动器自动寻找或手动选择学园游戏目录，以使用游戏自身的 SQLCipher 校验 master。')
            game_dir = Path(game_file.read_text('utf-8-sig').strip().strip('"'))
            IntegrityReader = load_module(Path(__file__).with_name('master_integrity.py'),'gkm_master_integrity').IntegrityReader
            integrity = IntegrityReader(game_dir)
            active_master, chosen_master = lifecycle.select(root, cache, wire, integrity)
            manifests = [(chosen_master['tag_sha256'], {p['type']:p['key'] for p in chosen_master['packs']})]
            selected = {p['type']:lifecycle.cache_name(p) for p in chosen_master['packs']}
            tables, source = read_tables(Path(cache), manifests, wire, selected)
            report['master_version'] = chosen_master['version']
            report['master_tables_verified'] = len(chosen_master['packs'])
            master_changed = active_master['tag_sha256'] != chosen_master['tag_sha256']
            report['master_outcome'] = 'new_version_validated' if master_changed else 'current_version_verified'
            print('master 清单与本机缓存完整校验：%d 张表。' % len(chosen_master['packs']), flush=True)
            report['source'] = source
            print('[2/4] 比对 P偶像、服装、歌曲与剧情……', flush=True)
            payloads, changes, skipped, before = prepare_indexes(mitm, staged, tables, wire)
            if master_changed:
                if game_running():
                    raise ValueError('发现新 master，请先关闭学园游戏后再更新；现有版本未改动。')
                publication = {
                    Path('mitm/offline-fallbacks/Master_Get.bin'):chosen_master['raw'],
                    Path('mitm/bootstrap-master.json'):lifecycle.json_bytes(lifecycle.metadata(chosen_master)),
                    Path('mitm/records/public_Master_Get_active.json'):lifecycle.json_bytes({'path':'/client.api.Master/Get','resp_body_hex':chosen_master['raw'].hex()})}
                for relative,content in publication.items():
                    payloads[relative]=content;before[relative]=(root/relative).read_bytes() if (root/relative).is_file() else None
            data = wire.load_master(str(staged / 'masterTrans'))
            defaults = {ident: next((v.decode() for f, w, r, l, v in wire.fields(blob) if f == 3 and w == 2), '')
                        for ident, blob in tables['IdolCard'].items()}
            old_to_skin = data['to_skin']
            data['to_skin'] = lambda card: defaults.get(card) or old_to_skin(card)
            if any(data['to_skin'](card) not in set(data['skins']) for card in data['cards']):
                raise ValueError('P卡与卡面关联不完整，已停止同步。')
            generated = wire.merge_unlock(old_plain, data)
            merged, added = append_unlocks(old_plain, generated, wire)
            playability = load_module(Path(__file__).with_name('local_playability.py'), 'gkm_local_playability')
            merged, playable, costumes, override_sha, override_files = playability.prepare(
                root, job, Path(cache).resolve(), chosen_master, integrity, merged, lifecycle, wire)
            if playable:
                added[str(wire.F_USER_CHARACTER)] += playable
            if override_sha:
                chosen_master['local_response_sha256'] = override_sha
            for relative, content in override_files.items():
                path = root / relative
                payloads[relative] = content
                before[relative] = path.read_bytes() if path.is_file() else None
            report.update(added_playable_characters=playable, live_hot_spring_costumes=costumes)
            print(f'  缺失偶像记录 {playable} 条，Live 温泉泳装 {costumes} 套。', flush=True)
            print('[3/4] 验证原有内容、等级及选择记录完整保留……', flush=True)
            if merged != old_plain:
                candidate = wire.encrypt_body(merged)
                if wire.decrypt_body(candidate) != merged:
                    raise ValueError('新存档回读校验失败。')
                payloads[Path('mitm/static_userget.bin')] = candidate
                before[Path('mitm/static_userget.bin')] = old_blob
            report.update(added_records=sum(added.values()), added_by_field=dict(added),
                          index_additions=changes, skipped_count=len(skipped), skipped=skipped, verified=True,
                          changed_files=[str(p) for p in payloads])
            if (mitm / 'static_userget.bin').read_bytes() != old_blob:
                raise ValueError('同步期间现役存档发生变化，已停止。')
            for item in source.values():
                if digest((Path(cache) / item['file']).read_bytes()) != item['sha256']:
                    raise ValueError('同步期间官方缓存仍在更新，请下载完成后重试。')
            print('[4/4] ' + ('预览完成，未应用改动。' if dry_run else '应用通过校验的内容……'), flush=True)
            if payloads and not dry_run:
                if master_changed:
                    lifecycle.prepare(root,before,payloads,active_master,chosen_master,cache,integrity)
                try:
                    apply_transaction(root,payloads,before,job)
                finally:
                    if master_changed:lifecycle.recover_preparing(root)
                report['temporary_rollback_cleaned'] = not master_changed
                report['master_previous_slot_retained'] = master_changed
                if master_changed:
                    print('新 master 已发布，旧版暂留一份；启动游戏进入大厅确认后自动清理。',flush=True)
            report['status'] = 'succeeded'
            report['outcome'] = 'synchronized' if added else ('indexes_repaired' if payloads else 'no_changes')
            if added:
                print('新增解锁记录：%d 条。' % sum(added.values()))
            else:
                print('本地已下载内容均已包含，没有新增解锁。')
            for name, count in changes.items():
                if count:
                    print('  %s：新增 %d 个 ID' % (LABELS.get(name, name), count))
            if payloads and not added:
                print('索引同步：%d 个文件。' % len(payloads))
            if skipped:
                print('按现有兼容规则排除 %d 项，明细已写入报告。' % len(skipped))
            if not dry_run and (added or master_changed or override_files):
                print('返回标题重新进入后生效；无需重启本地服务。')
            return report
        except Exception as exc:
            report.update(status='failed', error=str(exc), verified=False)
            raise
        finally:
            if 'integrity' in locals(): integrity.close()
            (root / 'out/local-sync-result.json').write_bytes(json_bytes(report))
            shutil.rmtree(job)
            print('报告：' + str(root / 'out/local-sync-result.json'), flush=True)


def game_running():
    import subprocess
    result=subprocess.run(['tasklist.exe','/FI','IMAGENAME eq gakumas.exe','/FO','CSV','/NH'],capture_output=True,creationflags=0x08000000,timeout=10)
    if result.returncode:raise ValueError('无法确认游戏进程状态，已停止 master 切换')
    return b'gakumas.exe' in result.stdout.lower()


def rollback_master(root):
    root=Path(root).resolve()
    if game_running():raise ValueError('请先关闭学园游戏，再回退未确认的更新。')
    with lock(root):
        recover_transaction(root)
        lifecycle=load_module(root/'mitm/master_lifecycle.py','gkm_master_rollback')
        result=lifecycle.rollback(root)
        print('已恢复上次可用 master，现役存档一并恢复；动态选择记录保留。' if result else '没有待回退的 master 更新。',flush=True)
        return result


def main():
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding=os.environ.get('GKM_SYNC_OUTPUT_ENCODING', 'utf-8'), errors='replace')
    parser = argparse.ArgumentParser(description='同步已下载的本地内容')
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--cache', type=Path)
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--rollback-master',action='store_true')
    parser.add_argument('--skip-refresh', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.cache is None:
        local_cache = args.root / 'gamecache'
        args.cache = local_cache if local_cache.is_dir() else Path(os.environ.get('LOCALAPPDATA', '')) / '..' / 'LocalLow/BANDAI NAMCO Entertainment Inc_/gakumas/EB0A191797624DD3A48FA681D3061212'
    try:
        if args.rollback_master:rollback_master(args.root)
        else:synchronize(args.root, args.cache.resolve(), args.dry_run)
        return 0
    except Exception as exc:
        print('同步未完成：' + str(exc), flush=True)
        return 1


if __name__ == '__main__':
    sys.exit(main())
