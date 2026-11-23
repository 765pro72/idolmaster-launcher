"""Download only a freshly observed official master; publish verified cache files."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
import argparse
import base64
from datetime import datetime
import importlib.util
import json
import os
import sys
import uuid
import urllib.request


class OfficialCheckRejected(RuntimeError):
    pass


def check_rejection(root, since):
    for path in (root / 'mitm/cache/client.api.System_Check').glob('*.json'):
        try:
            obj = json.loads(path.read_text('utf-8-sig'))
            observed = datetime.fromisoformat(obj['cached_at'].replace('Z', '+00:00')).timestamp()
            if observed < since:
                continue
            headers = {k.lower(): v for k, v in obj.get('resp_headers', {}).items()}
            code = str(headers.get('x-error-code', '0'))
            if code in ('', '0'):
                continue
            message = base64.b64decode(headers.get('x-error-message-bin', ''), validate=True).decode('utf-8', 'replace')[:512]
        except (OSError, ValueError, KeyError, TypeError):
            continue
        report = dict(status='official-system-check-rejected', code=code, message=message,
                      client_update_required=code == '2004')
        output = root / 'out/official-update-check.json'
        output.parent.mkdir(parents=True, exist_ok=True)
        temporary = output.with_suffix('.tmp')
        temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2), 'utf-8')
        os.replace(temporary, output)
        raise OfficialCheckRejected('Official System/Check rejected the client (code %s): %s. Existing local content retained.' % (code, message))


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fresh_candidate(root, since):
    wire = load(root / 'mitm/unlock.py', 'gkm_download_wire')
    life = load(root / 'mitm/master_lifecycle.py', 'gkm_download_lifecycle')
    active, candidates = life.candidates(root, wire)
    fresh = [item for item in candidates if item.get('observed_at', 0) >= since]
    if not fresh:
        return None, life
    candidate = max(fresh, key=lambda item: item['observed_at'])
    if len({item['tag_sha256'] for item in fresh if item['observed_at'] == candidate['observed_at']}) != 1:
        raise ValueError('Conflicting official master observations; existing content retained.')
    life.ensure_protocol_compatible(root,active,candidate)
    return candidate, life


class FixedOriginRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # CDN origin/path is already validated by master_lifecycle.describe.
        raise ValueError('Unexpected master download redirect; existing content retained.')


def download(root, cache, game_dir, since):
    candidate, life = fresh_candidate(root, since)
    if candidate is None:
        return 3
    reader_type = load(Path(__file__).with_name('master_integrity.py'), 'gkm_download_integrity').IntegrityReader
    cache.mkdir(parents=True, exist_ok=True)
    proxy = os.environ.get('GKM_UPSTREAM_PROXY', '127.0.0.1:7897')
    proxy_url = proxy if '://' in proxy else 'http://' + proxy
    proxies = {'https': proxy_url} if proxy else {}
    # Network transfers can run concurrently; SQLCipher validation stays serial.
    def fetch(pack):
        target = cache / life.cache_name(pack)
        if target.is_symlink():
            raise ValueError('Refusing symlink cache target.')
        temp = target.with_name(target.name + '.download-' + uuid.uuid4().hex)
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxies), FixedOriginRedirect())
            request = urllib.request.Request(pack['url'], headers={'Accept-Encoding': 'identity'})
            with opener.open(request, timeout=45) as response, temp.open('xb') as output:
                if response.status != 200 or response.headers.get('Content-Encoding', 'identity') not in ('identity', ''):
                    raise ValueError('Unexpected master transfer envelope.')
                received = 0
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    received += len(chunk)
                    if received > pack['size']:
                        raise ValueError('Master transfer exceeds manifest length.')
                    output.write(chunk)
                if received != pack['size']:
                    raise ValueError('Master transfer is truncated.')
            return pack, target, temp
        except BaseException:
            temp.unlink(missing_ok=True)
            raise

    reader = reader_type(game_dir)
    temporary = []
    verified = []
    try:
        needed = []
        for pack in candidate['packs']:
            target = cache / life.cache_name(pack)
            try:
                reader.validate(target, pack['key'], pack['type'], pack['size'])
            except (OSError, ValueError, RuntimeError):
                needed.append(pack)
        print('Official master: %s; tables: %d; missing/invalid: %d' %
              (candidate['version'], len(candidate['packs']), len(needed)), flush=True)
        # Collect every future, including successful results after another fails,
        # so no untracked .download file remains on an interrupted batch.
        failure = None
        with ThreadPoolExecutor(max_workers=4) as executor:
            futures = [executor.submit(fetch, pack) for pack in needed]
            for future in as_completed(futures):
                try:
                    pack, target, temp = future.result()
                    temporary.append(temp)
                    check = reader.validate(temp, pack['key'], pack['type'], pack['size'])
                    verified.append((target, temp, check))
                    print('Verified %d/%d: %s' % (len(verified), len(needed), pack['type']), flush=True)
                except Exception as exc:
                    failure = failure or exc
        if failure:
            raise failure
        # All new tables passed SQL integrity before any cache is replaced.
        for target, temp, check in verified:
            if target.is_symlink() or life.digest(temp.read_bytes()) != check['sha256']:
                raise ValueError('Cache changed during download verification.')
            os.replace(temp, target)
        report = {'status': 'tables_verified', 'version': candidate['version'],
                  'pack_count': len(candidate['packs']), 'downloaded': len(verified),
                  'response_sha256': candidate['response_sha256']}
        life.atomic(root / 'out/official-master-download.json', life.json_bytes(report))
        return 0
    finally:
        reader.close()
        for temp in temporary:
            temp.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--since', type=float, required=True)
    parser.add_argument('--probe', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    check_rejection(root, args.since)
    if args.probe:
        return 0 if fresh_candidate(root, args.since)[0] else 3
    game_dir = Path((root / 'game_path.txt').read_text('utf-8-sig').strip().strip('"'))
    return download(root, args.cache.resolve(), game_dir, args.since)


if __name__ == '__main__':
    try:
        sys.exit(main())
    except OfficialCheckRejected as exc:
        print(str(exc), flush=True)
        sys.exit(4)
    except Exception as exc:
        print('Official update failed: ' + str(exc), flush=True)
        sys.exit(4 if getattr(exc, 'permanent_compatibility_failure', False) else 1)
