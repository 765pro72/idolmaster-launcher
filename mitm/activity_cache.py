"""Validate and replay the existing launcher activity records by event ID."""

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path
import tempfile

import unlock as wire


LIST_ROUTE = "/client.api.Home/ListEvent"
TOP_ROUTE = "/client.api.StoryEvent/Top"


def message(frame):
    if len(frame) < 5 or frame[0] != 0 or int.from_bytes(frame[1:5], "big") != len(frame) - 5:
        raise ValueError("invalid activity frame")
    plain = wire.decrypt_body(frame)
    if plain is None:
        raise ValueError("invalid activity payload")
    records = list(wire.fields(plain))
    if sum(len(row[2]) for row in records) != len(plain):
        raise ValueError("incomplete activity payload")
    return records


def value(rows, tag, kind, default=None):
    return next((row[4] if kind == 2 else row[3]
                 for row in rows if row[0] == tag and row[1] == kind), default)


def event_id(request):
    raw = value(message(request), 1, 2, b"")
    if not raw:
        raise ValueError("missing StoryEventId")
    return raw.decode("utf-8")


def activity_ids(response):
    rows = validate_response(LIST_ROUTE, response)
    results = {}
    for tag, kind, _, _, blob in rows:
        if tag == 1 and kind == 2:
            event = list(wire.fields(blob))
            if value(event, 1, 0) in (5, 6, 7, 8):
                ident = value(event, 2, 2, b"").decode("utf-8")
                if ident:
                    results[ident] = value(event, 14, 2, b"").decode("utf-8") or ident
    return results


def validate_response(route, frame):
    rows = message(frame)
    if value(rows, 9999, 2) is None:
        raise ValueError("missing CommonResponse")
    if route == TOP_ROUTE and not value(rows, 1, 2):
        raise ValueError("missing activity details")
    return rows


def read_record(path, route, *, upstream=False):
    entry = json.loads(path.read_text(encoding="utf-8"))
    if entry.get("path") != route or entry.get("status") != 200:
        raise ValueError("wrong route or status")
    if upstream and entry.get("source") != "UPSTREAM":
        raise ValueError("unexpected activity record source")
    headers = entry.get("resp_headers") or {}
    if str(headers.get("grpc-status", "0")) != "0":
        raise ValueError("gRPC response failed")
    response = bytes.fromhex(entry["resp_body_hex"])
    validate_response(route, response)
    return entry, response


def files(directory, pattern):
    return sorted(directory.glob(pattern), key=lambda path: path.stat().st_mtime_ns, reverse=True)


def detail_path(fallbacks, ident):
    return fallbacks / "story-events" / (hashlib.sha256(ident.encode()).hexdigest() + ".bin")


def replay_detail(fallbacks, cache, request):
    try:
        ident = event_id(request)
    except (ValueError, IndexError):
        return None

    path = detail_path(fallbacks, ident)
    try:
        frame = path.read_bytes()
        validate_response(TOP_ROUTE, frame)
        return frame
    except (OSError, ValueError, IndexError):
        pass

    for record in files(cache, "*.json"):
        try:
            entry, frame = read_record(record, TOP_ROUTE)
            if event_id(bytes.fromhex(entry["req_body_hex"])) == ident:
                return frame
        except (OSError, ValueError, IndexError, KeyError, TypeError):
            continue

    # The unkeyed file in older packages is usable only with its original ID.
    try:
        binding = json.loads((fallbacks / "StoryEvent_Top.binding.json").read_text("utf-8"))
        if binding["event_id"] == ident:
            frame = (fallbacks / "StoryEvent_Top.bin").read_bytes()
            if hashlib.sha256(frame).hexdigest() == binding["sha256"]:
                validate_response(TOP_ROUTE, frame)
                return frame
    except (OSError, ValueError, IndexError, KeyError, TypeError):
        pass
    return None


def collect(directory, since):
    found_list, details = None, {}
    for path in files(directory, "*_client.api.*Event*.json"):
        try:
            if path.stat().st_mtime < since:
                continue
            if path.name.endswith("_client.api.Home_ListEvent.json") and found_list is None:
                _, frame = read_record(path, LIST_ROUTE, upstream=True)
                activity_ids(frame)
                found_list = frame
            elif path.name.endswith("_client.api.StoryEvent_Top.json"):
                entry, frame = read_record(path, TOP_ROUTE, upstream=True)
                ident = event_id(bytes.fromhex(entry["req_body_hex"]))
                details.setdefault(ident, frame)
        except (OSError, ValueError, IndexError, KeyError, TypeError):
            continue
    return found_list, details


def replace_file(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temp = Path(stream.name)
        stream.write(data)
    try:
        temp.replace(path)
    finally:
        temp.unlink(missing_ok=True)


def main():
    encoding = os.environ.get("GKM_SYNC_OUTPUT_ENCODING")
    if encoding:
        sys.stdout.reconfigure(encoding=encoding, errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--since", required=True, type=float)
    parser.add_argument("--save", action="store_true")
    args = parser.parse_args()
    mitm = args.root / "mitm"
    home, details = collect(mitm / "records", args.since)
    if home is None:
        print("等待活动列表（进入主页后由游戏请求）。")
        return 3
    missing = {i: name for i, name in activity_ids(home).items() if i not in details}
    if args.save:
        fallbacks = mitm / "offline-fallbacks"
        for ident, frame in details.items():
            replace_file(detail_path(fallbacks, ident), frame)
        replace_file(fallbacks / "Home_ListEvent.bin", home)
        print(f"已更新活动列表和 {len(details)} 个活动详情。")
    if missing:
        print("等待活动详情，请点开对应活动页：" + "、".join(missing.values()))
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
