"""Keep newly playable idols and selected costumes usable in offline Live."""

from contextlib import contextmanager
import ctypes
import json
from pathlib import Path
import shutil

OVERRIDE = Path('mitm/offline-fallbacks/CostumeLive_Master_Get')
MOTIF = b'othr-0002'
LIVE_FEATURE = 3
COSTUME_RULESET = 2
STORY_ONLY_DATE = 4133901600000
STORY_TOWELS = frozenset((b'hrnm-othr-0000', b'hume-othr-0000'))
HIDDEN_LIVE_COSTUMES = STORY_TOWELS | frozenset((
    b'hkne-othr-0004', b'hrnm-othr-0003', b'kcna-othr-0003',
))


def text_field(rows, tag):
    return next((value for f, kind, _, _, value in rows if (f, kind) == (tag, 2)), None)


@contextmanager
def open_table(reader, path, pack, writable=False):
    db = ctypes.c_void_p()
    dll = reader._lib

    def check(status):
        if status:
            message = dll.sqlite3_errmsg(db)
            raise ValueError(f'{pack["type"]} master SQL: {message.decode("utf-8", "replace")}')

    uri = path.as_uri() if writable else path.as_uri() + '?mode=ro&immutable=1'
    try:
        check(dll.sqlite3_open_v2(uri.encode('utf-8'), ctypes.byref(db), 0x42 if writable else 0x41, None))
        key = ctypes.create_string_buffer(b"x'" + pack['key'].hex().encode() + b"'")
        try:
            check(dll.sqlite3_key_v2(db, b'main', key, 67))
        finally:
            ctypes.memset(key, 0, len(key))
        yield db, check
    finally:
        if db:
            dll.sqlite3_close(db)


@contextmanager
def statement(reader, db, check, sql):
    cursor = ctypes.c_void_p()
    check(reader._lib.sqlite3_prepare_v2(db, sql, -1, ctypes.byref(cursor), None))
    try:
        yield cursor
    finally:
        reader._lib.sqlite3_finalize(cursor)


def read_rows(reader, db, check, name):
    if name not in ('Character', 'Costume'):
        raise ValueError('Unexpected offline compatibility table')
    ptr = ctypes.c_void_p
    reader._bind('sqlite3_column_blob', [ptr, ctypes.c_int], ptr)
    dll = reader._lib
    rows = {}
    with statement(reader, db, check, f'SELECT Id, data FROM {name}'.encode()) as cursor:
        while (status := dll.sqlite3_step(cursor)) == 100:
            key = ctypes.string_at(dll.sqlite3_column_text(cursor, 0), dll.sqlite3_column_bytes(cursor, 0))
            data = dll.sqlite3_column_blob(cursor, 1)
            if not key or not data or key in rows:
                raise ValueError(f'Invalid {name} row')
            rows[key] = ctypes.string_at(data, dll.sqlite3_column_bytes(cursor, 1))
        if status != 101:
            check(status)
    return rows


def append_playable_characters(plain, rows, wire):
    fields = list(wire.fields(plain))
    user_fields = [row for row in fields if row[:2] == (wire.F_USER_DATA, 2)]
    if len(user_fields) != 1:
        raise ValueError('Invalid local UserData envelope')
    user = user_fields[0][4]
    existing = {text_field(wire.fields(blob), 2) for f, kind, _, _, blob in wire.fields(user)
                if (f, kind) == (wire.F_USER_CHARACTER, 2)}
    additions = []
    for ident, blob in rows.items():
        if ident in existing:
            continue
        character = list(wire.fields(blob))
        if not any(f == 6 and kind == 0 and number == 1 for f, kind, _, number, _ in character):
            continue
        if text_field(character, 1) != ident:
            raise ValueError('Playable Character master ID mismatch')
        record = wire._ld(2, ident) + wire._vi(3, wire.FAN_PER_CHARACTER) + wire._vi(7, wire.MAX_DEARNESS)
        additions.append(wire._ld(wire.F_USER_CHARACTER, record))
    if not additions:
        return plain, 0
    userdata = user + b''.join(additions)
    return b''.join(wire._ld(wire.F_USER_DATA, userdata) if row is user_fields[0] else row[2]
                    for row in fields), len(additions)


def allow_live(blob, wire):
    rows = list(wire.fields(blob))
    costume_id = text_field(rows, 1)
    motif = text_field(rows, 4)
    if motif != MOTIF and not (
        costume_id in HIDDEN_LIVE_COSTUMES
        and costume_id == (text_field(rows, 2) or b'') + b'-' + (motif or b'')
    ):
        return blob
    output = []
    for f, kind, raw, value, payload in rows:
        if costume_id in STORY_TOWELS and (f, kind, value) == (98, 0, STORY_ONLY_DATE):
            continue
        if f == 13 and kind == 0 and value == LIVE_FEATURE:
            continue
        if f == 13 and kind == 2:
            retained = bytearray()
            pos = 0
            while pos < len(payload):
                enum, end = wire._rv(payload, pos)
                if enum != LIVE_FEATURE:
                    retained += payload[pos:end]
                pos = end
            if len(retained) == len(payload):
                output.append(raw)
            elif retained:
                output.append(wire._ld(13, bytes(retained)))
        else:
            output.append(raw)
    return b''.join(output)


def edit_costume(reader, path, pack, wire):
    ptr, integer = ctypes.c_void_p, ctypes.c_int
    reader._bind('sqlite3_bind_text', [ptr, integer, ctypes.c_char_p, integer, ptr], integer)
    reader._bind('sqlite3_bind_blob', [ptr, integer, ptr, integer, ptr], integer)
    dll = reader._lib
    with open_table(reader, path, pack, writable=True) as (db, check):
        original = read_rows(reader, db, check, 'Costume')
        edited = {}
        for ident, blob in original.items():
            replacement = allow_live(blob, wire)
            if replacement != blob:
                edited[ident] = replacement
        for ident, blob in edited.items():
            with statement(reader, db, check, b'UPDATE Costume SET data=? WHERE Id=?') as cursor:
                data = ctypes.create_string_buffer(blob)
                check(dll.sqlite3_bind_blob(cursor, 1, data, len(blob), None))
                check(dll.sqlite3_bind_text(cursor, 2, ident, len(ident), None))
                status = dll.sqlite3_step(cursor)
                if status != 101:
                    check(status)
        if read_rows(reader, db, check, 'Costume') != original | edited:
            raise ValueError('Costume master changes differed from the selected rows')
    return set(edited)


def costume_manifest(master, figure, lifecycle, wire):
    filename = lifecycle.digest(figure)
    url = f'https://stat.game-gakuen-idolmaster.jp/master/Costume/{filename}'
    version = lifecycle.digest(bytes.fromhex(master['version'] + filename))
    plain = list(wire.fields(wire.decrypt_body(master['raw'])))
    tag = list(wire.fields(text_field(plain, 1)))
    parts = []
    for f, kind, raw, _, payload in tag:
        if (f, kind) == (1, 2):
            raw = wire._ld(1, version.encode())
        if (f, kind) == (2, 2):
            descriptor = list(wire.fields(payload))
            if text_field(descriptor, 1) == b'Costume':
                patch = {2: wire._ld(2, filename.encode()), 3: wire._vi(3, len(figure)),
                         5: wire._ld(5, url.encode())}
                raw = wire._ld(2, b''.join(patch.get(row[0], row[2]) for row in descriptor))
        parts.append(raw)
    frame = wire.encrypt_body(b''.join(wire._ld(1, b''.join(parts)) if row[:2] == (1, 2) else row[2]
                                      for row in plain))
    local = lifecycle.describe(frame, wire)
    previous = [pack for pack in master['packs'] if pack['type'] != 'Costume']
    current = [pack for pack in local['packs'] if pack['type'] != 'Costume']
    costume = next(pack for pack in local['packs'] if pack['type'] == 'Costume')
    if previous != current or local['proto_hash'] != master['proto_hash'] or local['version'] != version:
        raise ValueError('Local Costume manifest validation failed')
    return frame, lifecycle.cache_name(costume)


def existing_receipt(root, master, lifecycle):
    base = root / OVERRIDE
    try:
        receipt = json.loads(base.with_suffix('.json').read_text('utf-8'))
        if (receipt.get('format') == 1 and receipt.get('ruleset') == COSTUME_RULESET
                and receipt.get('base_sha256') == master['response_sha256']
                and lifecycle.digest(base.with_suffix('.bin').read_bytes()) == receipt['override_sha256']
                and lifecycle.digest(base.with_suffix('.db').read_bytes()) == receipt['cache_sha256']):
            return receipt
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        pass
    return None


def prepare(root, staging, cache, master, reader, merged, lifecycle, wire):
    packs = {pack['type']: pack for pack in master['packs']}
    character = packs['Character']
    with open_table(reader, cache / lifecycle.cache_name(character), character) as (db, check):
        rows = read_rows(reader, db, check, 'Character')
    merged, added = append_playable_characters(merged, rows, wire)

    previous = existing_receipt(root, master, lifecycle)
    if previous:
        return merged, added, previous['costumes'], previous['override_sha256'], {}
    costume = packs['Costume']
    staged_table = staging / 'live-costume-copy.db'
    shutil.copyfile(cache / lifecycle.cache_name(costume), staged_table)
    edited = edit_costume(reader, staged_table, costume, wire)
    if not edited:
        return merged, added, 0, None, {}
    hidden = sorted(edited & HIDDEN_LIVE_COSTUMES)
    count = len(edited) - len(hidden)
    figure = staged_table.read_bytes()
    reader.validate(staged_table, costume['key'], 'Costume', len(figure))
    frame, name = costume_manifest(master, figure, lifecycle, wire)
    receipt = {
        'format': 1, 'ruleset': COSTUME_RULESET, 'base_sha256': master['response_sha256'],
        'override_sha256': lifecycle.digest(frame),
        'cache_name': name, 'cache_sha256': lifecycle.digest(figure),
        'cache_size': len(figure), 'costumes': count, 'hidden_costumes': [ident.decode('ascii') for ident in hidden],
    }
    files = {OVERRIDE.with_suffix('.bin'): frame, OVERRIDE.with_suffix('.db'): figure,
             OVERRIDE.with_suffix('.json'): lifecycle.json_bytes(receipt)}
    return merged, added, count, receipt['override_sha256'], files
