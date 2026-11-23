"""Supply a no-change Octo catalog for the client's installed catalog revision."""

from functools import lru_cache
from hashlib import sha256
from pathlib import Path
import re
import secrets
import struct

from Crypto.Cipher import AES
from Crypto.Util.Padding import pad, unpad


_ROUTE = re.compile(r"/v2/pub/a/400/v/705100/list/([0-9]{1,9})")
_LEGACY_REVISION = 73


def metadata_strings(path):
    data = path.read_bytes()
    if data[:8] != struct.pack("<II", 0xFAB11BAF, 31):
        raise ValueError("unsupported Octo metadata layout")
    directory, size, strings, strings_size = struct.unpack_from("<4I", data, 8)
    if (size % 8 or directory + size > len(data)
            or strings + strings_size > len(data)):
        raise ValueError("invalid metadata string table")
    for size, start in struct.iter_unpack("<2I", data[directory:directory + size]):
        if 8 <= size <= 128 and start + size <= strings_size:
            yield data[strings + start:strings + start + size]


@lru_cache(maxsize=2)
def catalog_format(metadata, fixture):
    fixture_iv, ciphertext = fixture[:16], fixture[16:]
    # Octo hashes its client asset setting to obtain the AES-CBC key.
    # Validate the candidate with the shipped revision-73 no-change reply.
    expected = bytes((8, _LEGACY_REVISION, 42, 50))
    for literal in metadata_strings(metadata):
        key = sha256(literal).digest()
        try:
            message = unpad(AES.new(key, AES.MODE_CBC, fixture_iv).decrypt(ciphertext), 16)
        except ValueError:
            continue
        if len(message) != 54 or not message.startswith(expected):
            continue
        template = message[2:]
        url = template[2:]
        if url.startswith(b"https://") and url.isascii():
            return key, template
    raise ValueError("could not validate the Octo catalog fixture for this client")


def varint(number):
    encoded = bytearray()
    while number > 127:
        encoded.append((number & 127) | 128)
        number >>= 7
    encoded.append(number)
    return bytes(encoded)


def reply(target, fixture, game_path):
    match = _ROUTE.fullmatch(target.split("?", 1)[0])
    if not match or not fixture:
        return None
    revision = int(match[1])
    if revision == _LEGACY_REVISION:
        return fixture
    if revision == 0:
        # A client with no installed catalog needs a full initial resource download.
        return None
    metadata = game_path / "gakumas_Data/il2cpp_data/Metadata/global-metadata.dat"
    key, template = catalog_format(metadata, fixture)
    iv = secrets.token_bytes(16)
    return iv + AES.new(key, AES.MODE_CBC, iv).encrypt(
        pad(bytes((8,)) + varint(revision) + template, 16))
