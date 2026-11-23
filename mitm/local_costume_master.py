"""Supply the checked offline master override and its separate game cache file."""

import hashlib
import json
import os
from pathlib import Path
import re
import tempfile


def matches(path, digest):
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest() == digest
    except OSError:
        return False


def seed_cache(path, source, digest):
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != digest:
        return False
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        stream.write(data)
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
    return True


def response(here, original):
    directory = Path(here) / 'offline-fallbacks'
    prefix = directory / 'CostumeLive_Master_Get'
    try:
        receipt = json.loads(prefix.with_suffix('.json').read_text('utf-8'))
        if hashlib.sha256(original).hexdigest() != receipt['base_sha256']:
            return original
        name = receipt['cache_name']
        if not re.fullmatch(r'[0-9A-F]{32}_[0-9A-F]{32}', name):
            return original
        override = prefix.with_suffix('.bin').read_bytes()
        if hashlib.sha256(override).hexdigest() != receipt['override_sha256']:
            return original
        cache = Path(os.environ['LOCALAPPDATA']).parent / 'LocalLow/BANDAI NAMCO Entertainment Inc_/gakumas/EB0A191797624DD3A48FA681D3061212'
        table, digest = cache / name, receipt['cache_sha256']
        if matches(table, digest) or seed_cache(table, prefix.with_suffix('.db'), digest):
            return override
    except (OSError, ValueError, KeyError, TypeError):
        pass
    return original
