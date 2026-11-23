"""Validate one public GKM master cache with the selected game's SQLCipher.

This module opens immutable read-only connections. It neither exports master
rows nor accepts SQL from callers, and never tries a different manifest's key.
"""
from pathlib import Path
import ctypes
import hashlib
import os
import re
import stat
import threading


class IntegrityError(RuntimeError):
    """The specified manifest/cache pair could not be verified."""


class IntegrityReader:
    def __init__(self, game_dir):
        self._closed = False
        self._lock = threading.RLock()
        self._cookie = None
        directory = Path(game_dir).resolve(strict=True) / 'gakumas_Data' / 'Plugins' / 'x86_64'
        dll = directory / 'sqlcipher.dll'
        if not dll.is_file():
            raise IntegrityError('The selected GKM game has no SQLCipher dependency')
        try:
            self._cookie = os.add_dll_directory(str(directory))
            self._lib = ctypes.CDLL(str(dll))
            pointer = ctypes.c_void_p
            self._bind('sqlite3_open_v2', [ctypes.c_char_p, ctypes.POINTER(pointer), ctypes.c_int, ctypes.c_char_p], ctypes.c_int)
            self._bind('sqlite3_key_v2', [pointer, ctypes.c_char_p, pointer, ctypes.c_int], ctypes.c_int)
            self._bind('sqlite3_db_readonly', [pointer, ctypes.c_char_p], ctypes.c_int)
            self._bind('sqlite3_prepare_v2', [pointer, ctypes.c_char_p, ctypes.c_int, ctypes.POINTER(pointer), pointer], ctypes.c_int)
            self._bind('sqlite3_step', [pointer], ctypes.c_int)
            self._bind('sqlite3_column_count', [pointer], ctypes.c_int)
            self._bind('sqlite3_column_type', [pointer, ctypes.c_int], ctypes.c_int)
            self._bind('sqlite3_column_int64', [pointer, ctypes.c_int], ctypes.c_int64)
            self._bind('sqlite3_column_text', [pointer, ctypes.c_int], pointer)
            self._bind('sqlite3_column_bytes', [pointer, ctypes.c_int], ctypes.c_int)
            self._bind('sqlite3_finalize', [pointer], ctypes.c_int)
            self._bind('sqlite3_close', [pointer], ctypes.c_int)
            self._bind('sqlite3_errmsg', [pointer], ctypes.c_char_p)
        except Exception:
            if self._cookie is not None:
                self._cookie.close()
                self._cookie = None
            self._closed = True
            raise

    def _bind(self, name, args, result):
        function = getattr(self._lib, name)
        function.argtypes, function.restype = args, result

    def __enter__(self):
        with self._lock:
            if self._closed:
                raise IntegrityError('IntegrityReader is closed')
            return self

    def __exit__(self, kind, value, traceback):
        self.close()

    def close(self):
        with self._lock:
            if not self._closed:
                self._closed = True
                if self._cookie is not None:
                    self._cookie.close()
                    self._cookie = None

    @staticmethod
    def _plain_file(path):
        # Check before resolving: resolve() alone would hide a symlink cache.
        path = Path(os.path.abspath(os.fspath(path)))
        for component in (path, *path.parents):
            metadata = component.lstat()
            if stat.S_ISLNK(metadata.st_mode) or (
                getattr(metadata, 'st_file_attributes', 0) & 0x400
            ):
                raise IntegrityError('Master cache paths must not contain symbolic links or reparse points')
        metadata = path.stat()
        if not stat.S_ISREG(metadata.st_mode):
            raise IntegrityError('Master cache must be a regular file')
        return path

    @staticmethod
    def _snapshot(path):
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            before = os.fstat(stream.fileno())
            if not stat.S_ISREG(before.st_mode):
                raise IntegrityError('Master cache must be a regular file')
            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(chunk)
            after = os.fstat(stream.fileno())
        fields = ('st_dev', 'st_ino', 'st_size', 'st_mtime_ns')
        signature = tuple(getattr(before, name) for name in fields)
        if signature != tuple(getattr(after, name) for name in fields):
            raise IntegrityError('Master cache changed while hashing')
        return before.st_size, digest.hexdigest(), signature

    def _error(self, db, stage, status):
        value = self._lib.sqlite3_errmsg(db) if db else None
        detail = (value or b'unknown SQLite error').decode('utf-8', 'replace')
        return IntegrityError(stage + ' failed (SQLite ' + str(status) + '): ' + detail)

    def _query_one_column(self, db, sql):
        # All callers below use fixed statements or the validated simple table
        # name. No caller-provided SQL is exposed through the public API.
        statement = ctypes.c_void_p()
        encoded = sql.encode('ascii')
        status = self._lib.sqlite3_prepare_v2(db, encoded, len(encoded), ctypes.byref(statement), None)
        if status:
            if statement:
                self._lib.sqlite3_finalize(statement)
            raise self._error(db, 'Master validation prepare', status)
        rows = []
        try:
            if self._lib.sqlite3_column_count(statement) != 1:
                raise IntegrityError('Unexpected master validation result shape')
            while True:
                status = self._lib.sqlite3_step(statement)
                if status == 101:  # SQLITE_DONE
                    return rows
                if status != 100:  # SQLITE_ROW
                    raise self._error(db, 'Master validation step', status)
                kind = self._lib.sqlite3_column_type(statement, 0)
                if kind == 1:
                    value = self._lib.sqlite3_column_int64(statement, 0)
                elif kind == 3:
                    count = self._lib.sqlite3_column_bytes(statement, 0)
                    pointer = self._lib.sqlite3_column_text(statement, 0)
                    value = ctypes.string_at(pointer, count).decode('utf-8') if count else ''
                else:
                    raise IntegrityError('Unexpected master validation scalar type')
                rows.append(value)
                if len(rows) > 100:
                    raise IntegrityError('Unexpected master validation result count')
        finally:
            self._lib.sqlite3_finalize(statement)

    def validate(self, path, raw_key, expected_table, expected_size):
        """Verify exactly one manifest pack; return its source SHA256 and size.

        raw_key is bytes.fromhex(MasterTagPack.CryptoKey), exactly 32 bytes.
        expected_size is the integer MasterTagPack.FileSize. No rows are returned.
        """
        if not isinstance(raw_key, bytes) or len(raw_key) != 32:
            raise IntegrityError('Expected a raw 32-byte master key')
        if not isinstance(expected_table, str) or re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{0,127}', expected_table, re.ASCII) is None:
            raise IntegrityError('Expected a simple public master table name')
        if type(expected_size) is not int or expected_size <= 0:
            raise IntegrityError('Expected a positive integer master file size')
        with self._lock:
            if self._closed:
                raise IntegrityError('IntegrityReader is closed')
            path = self._plain_file(path)
            before_size, before_hash, before_signature = self._snapshot(path)
            if before_size != expected_size:
                raise IntegrityError('Master cache size does not match its manifest pack')
            db = ctypes.c_void_p()
            # SQLITE_OPEN_READONLY | SQLITE_OPEN_URI. immutable additionally
            # suppresses journal/WAL discovery and any cache-side writes.
            uri = path.as_uri() + '?mode=ro&immutable=1'
            status = self._lib.sqlite3_open_v2(uri.encode('utf-8'), ctypes.byref(db), 0x41, None)
            try:
                if status:
                    raise self._error(db, 'Read-only master open', status)
                if self._lib.sqlite3_db_readonly(db, b'main') != 1:
                    raise IntegrityError('SQLite did not confirm a read-only master database')
                # SQLCipher raw-key syntax, never a key-bearing SQL statement.
                key_buffer = ctypes.create_string_buffer(b"x'" + raw_key.hex().encode('ascii') + b"'")
                try:
                    status = self._lib.sqlite3_key_v2(db, b'main', key_buffer, 67)
                finally:
                    ctypes.memset(key_buffer, 0, len(key_buffer))
                if status:
                    raise self._error(db, 'Raw master key setup', status)
                query = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='" + expected_table + "'"
                if self._query_one_column(db, query) != [1]:
                    raise IntegrityError('Expected public table is absent from this master cache')
                if self._query_one_column(db, 'PRAGMA quick_check') != ['ok']:
                    raise IntegrityError('Master SQLite integrity check failed')
                if self._lib.sqlite3_db_readonly(db, b'main') != 1:
                    raise IntegrityError('Master database lost its read-only status')
            finally:
                if db:
                    close_status = self._lib.sqlite3_close(db)
                    if close_status:
                        raise self._error(db, 'Master database close', close_status)
            # Also reject path substitution or updates performed by a running
            # downloader. Time is a mutation guard, never a version selector.
            self._plain_file(path)
            after_size, after_hash, after_signature = self._snapshot(path)
            if (before_size, before_hash, before_signature) != (after_size, after_hash, after_signature):
                raise IntegrityError('Master cache changed during integrity validation')
            return {'sha256': before_hash, 'size': before_size}
