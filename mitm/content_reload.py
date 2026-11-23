"""Load a completed local content transaction without discarding session choices."""
from pathlib import Path
import threading
import time


class ContentReloader:
    def __init__(self, mitm, unlocker, wire, logger):
        self.mitm = Path(mitm)
        self.unlocker, self.wire, self.log = unlocker, wire, logger
        self.lock = threading.Lock()
        self.next_check = 0
        self.generation = self.signature()

    def signature(self):
        paths = [self.mitm / 'static_userget.bin']
        paths += sorted((self.mitm / 'masterTrans').glob('*.json'))
        paths += [self.mitm / name for name in (
            'live-cards.json', 'live-costumes.json', 'live-extra.json', 'live-pstories.json',
            'live-photo.json', 'badge-ids.json', 'music-ids.json', 'idol-card-skin-map.json')]
        return tuple((str(p), p.stat().st_mtime_ns, p.stat().st_size) for p in paths if p.is_file())

    def refresh(self):
        if time.monotonic() < self.next_check:
            return False
        with self.lock:
            self.next_check = time.monotonic() + 1
            pending = self.mitm.parent / 'out/local-sync-transaction'
            if pending.exists():
                return False
            try:
                current = self.signature()
                if current == self.generation:
                    return False
                blob = (self.mitm / 'static_userget.bin').read_bytes()
                plain = self.wire.decrypt_body(blob)
                if plain is None or not any(f == 1 and w == 2 for f, w, raw, size, v in self.wire.fields(plain)):
                    raise ValueError('invalid static archive')
                data = self.wire.load_master(str(self.mitm / 'masterTrans'))
                blob = self.wire.apply_config_to_blob(blob, self.wire.load_config())
                if pending.exists() or self.signature() != current:
                    return False
                self.unlocker.data = data
                self.unlocker.set_static(blob)
                self.generation = current
                self.log('LOCAL CONTENT reloaded; session selections preserved')
                return True
            except Exception as exc:
                self.log('LOCAL CONTENT reload postponed:', str(exc))
                return False
