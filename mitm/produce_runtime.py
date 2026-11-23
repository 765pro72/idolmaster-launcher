"""Local progression for the shipped Selection Exam schedule."""

import json
from functools import lru_cache
from pathlib import Path
import secrets

import unlock as wire


PREFIX = "/client.api.Produce/"
SCHEDULE = 22482116
DECK = 309641228
SHOP = 498183736
OPENING, SELECT, ACTION = 1, 4, 8
EVENTS = (11, 12)
DEVICE_FIELD = {
    "ReadAdv": 3, "Next": 4, "StepEvent": 5, "StepRefresh": 2,
    "StepLessonStart": 2, "StepLessonEnd": 2, "StepPresentEnd": 2,
    "StepShopStart": 2, "StepShopBuy": 4, "StepShopEnd": 2,
}
METHODS = {"Start", "CheckProgress", "Retire", *DEVICE_FIELD}


class ProgressError(ValueError):
    pass


@lru_cache(maxsize=1)
def catalog():
    return json.loads(Path(__file__).with_name("produce_catalog.json").read_text("utf-8"))


def get(data, tag, kind, default):
    return next((v if kind == 2 else n for f, w, _, n, v in wire.fields(data)
                 if f == tag and w == kind), default)


def num(data, tag):
    return get(data, tag, 0, 0)


def text(data, tag):
    return get(data, tag, 2, b"").decode("utf-8")


def packed(data, tag):
    result = []
    for field, kind, _, n, value in wire.fields(data):
        if field != tag:
            continue
        if kind == 0:
            result.append(n)
        if kind == 2:
            offset = 0
            while offset < len(value):
                n, offset = wire._rv(value, offset)
                result.append(n)
    return result


def insert(row, *updates):
    replacements = dict(updates)
    return (b"".join(raw for tag, _, raw, _, _ in wire.fields(row) if tag not in replacements)
            + b"".join(wire._vi(tag, value) for tag, value in replacements.items()))


def start(owner, data, plain):
    data["client_id"] = text(plain, 99)
    data["step"] = 1
    data["stats"] = {200: 207, 201: 192, 202: 267, 220: 31, 221: 31, 230: 80}
    data["schedules"] = {}
    data["old_deck"] = {}
    data["shop_template"] = {}
    data["shop"] = {}
    data["drinks"] = []
    for tag, kind, _, _, row in wire.fields(owner._static_user_data() or b""):
        if kind == 2 and tag == SCHEDULE:
            data["schedules"][num(row, 2)] = row
        if kind == 2 and tag == DECK:
            data["old_deck"][num(row, 2)] = row
        if kind == 2 and tag == SHOP:
            resource = num(row, 4)
            data["shop_template"][num(row, 2)] = insert(
                row, (3, 0), (12, int(resource not in (1, 3, 997, 998))))

    data["idol"] = catalog()["idols"].get(data["card"]) or {}
    if data["idol"]:
        data["char"] = data["idol"]["character"]
        data["stats"][220] = data["stats"][221] = data["idol"]["stamina"]
    data["deck"] = list(data["idol"].get("deck", ()))
    data["exam_seed"] = 0
    data["exam_result"] = b""
    data["lesson_growth"] = b""
    data["exam_result_type"] = 0
    data["action_type"] = 0
    data["action_id"] = ""
    data["battle_started"] = False
    data["replies"] = {}


def common(updated=b"", deleted=b""):
    return wire._ld(9999, wire._ld(1, updated) + wire._ld(2, deleted))


def progress(owner, data):
    return wire._ld(wire.F_PRODUCE_PROGRESS, owner._build_produce_progress(
        data, status=data["status"], step=data["step"], nav_normal=max(1, data["step"]),
        step_type=data["action_type"], step_id=data["action_id"]))


def deck_changes(data):
    updated, deleted = bytearray(), bytearray()
    current = {}
    for number, card in enumerate(data["deck"], 1):
        if card is None:
            continue
        ident, upgrade, origin = card
        row = wire._vi(2, number) + wire._ld(3, ident.encode()) + wire._vi(4, upgrade) + wire._vi(7, origin)
        current[number] = row
        updated += wire._ld(DECK, row)
    for number in data["old_deck"]:
        if number not in current:
            deleted += wire._ld(DECK, wire._vi(2, number))
    data["old_deck"] = current
    return bytes(updated), bytes(deleted)


def action_key(data, action_type, row):
    plan = data["idol"].get("plan")
    if 1 <= action_type <= 9:
        return catalog()["lessons"].get(f"{plan}:{action_type}")
    # The template marks the two exam days with step types 16 and 18.
    # This provides deterministic local stories for the available options.
    stage = 1 + sum(any(16 <= t <= 18 for t in packed(r, 24))
                    for n, r in data["schedules"].items() if n <= data["step"])
    if action_type == 11:
        # The local catalog has activity stories for the second and third arcs.
        part = "3rd" if stage >= 2 else "2nd"
        return f"event-detail-activity-003-produce_007-before_{part}-01"
    options = packed(row, 24)
    subs = packed(row, 23)
    index = options.index(12)
    axis = {1: "vocal", 2: "dance", 3: "visual"}.get(subs[index] if index < len(subs) else 0, "vocal")
    part = "1st" if stage == 1 else "3rd"
    prefix = f"event-detail-school-{axis}-003-produce_007-plan{plan}-before_{part}-01"
    if prefix in catalog()["events"]:
        return prefix
    suffix = f"-003-produce_007-plan{plan}-before_{part}-01"
    return next((key for key in catalog()["events"] if key.startswith("event-detail-school-")
                 and key.endswith(suffix)), None)


def begin(data, action_type):
    if data["status"] == ACTION and data["action_type"] == action_type:
        return
    if data["status"] != SELECT:
        raise ProgressError("Finish the current action before selecting the next one")
    if data["produce"] != "produce-007" or not data["idol"]:
        raise ProgressError("This local schedule supports Selection Exam idols with matching master data")
    row = data["schedules"].get(data["step"] + 1, b"")
    if action_type not in packed(row, 24):
        raise ProgressError("Selected action is not in this day's local schedule")
    if action_type in EVENTS or 1 <= action_type <= 9:
        key = action_key(data, action_type, row)
        if not key or (action_type in EVENTS and key not in catalog()["events"]):
            raise ProgressError("No matching local master record for this action")
    elif action_type in (13, 14):
        key = ""
    else:
        raise ProgressError(f"Local action {action_type} is not implemented")
    # A new action ends the retry window for the preceding action.
    data["replies"].clear()
    data.update(status=ACTION, action_type=action_type, action_id=key, battle_started=False)
    data["exam_seed"] = secrets.randbelow(2**31 - 1) + 1 if 1 <= action_type <= 9 else 0


def advance(data, *metadata):
    step = data["step"] + 1
    row = data["schedules"][step]
    row = insert(row, (25, data["action_type"]), (26, 0), (28, 0), *metadata)
    data["schedules"][step] = row
    data.update(step=step, status=SELECT, action_type=0, action_id="", battle_started=False)
    updated = wire._ld(SCHEDULE, row)
    upcoming = data["schedules"].get(step + 1)
    # This H.I.F. template includes hard-lesson rows, but the local
    # ProduceSplitAdv table lacks the corresponding before-hard stories.
    if data["produce"] == "produce-007" and upcoming:
        options = packed(upcoming, 24)
        normal = {3: 1, 6: 4, 9: 7}
        if any(kind in normal for kind in options):
            upcoming = b"".join(raw for tag, _, raw, _, _ in wire.fields(upcoming) if tag != 24)
            upcoming += wire._ld(24, b"".join(wire._wv(normal.get(kind, kind)) for kind in options))
            upcoming = insert(upcoming, (25, 0), (26, 0))
            data["schedules"][step + 1] = upcoming
            updated += wire._ld(SCHEDULE, upcoming)
    return updated


def finish_event(data, request):
    if data["status"] != ACTION or data["action_type"] not in EVENTS:
        raise ProgressError("No active school or activity event")
    suggestions = catalog()["events"][data["action_id"]]
    index = num(request, 2)
    if index >= len(suggestions):
        raise ProgressError("Invalid event choice")
    suggestion = suggestions[index]
    stats = data["stats"]
    if stats[230] < suggestion["point_cost"] or stats[220] < suggestion["stamina_cost"]:
        raise ProgressError("Insufficient points or stamina for this event choice")
    stats[230] -= suggestion["point_cost"]
    stats[220] -= suggestion["stamina_cost"]
    for kind, value in suggestion["effects"]:
        if 1 <= kind <= 3:
            stat = 199 + kind
            stats[stat] = min(3000, stats[stat] + value)
        elif 4 <= kind <= 6:
            stat = 196 + kind
            stats[stat] = max(0, stats[stat] - value)
        elif kind in (13, 14):
            recovery = value if kind == 13 else stats[221] * value // 1000
            stats[220] = min(stats[221], stats[220] + recovery)
        elif kind in (15, 16):
            cost = value if kind == 15 else stats[221] * value // 1000
            stats[220] = max(0, stats[220] - cost)
        elif kind == 17:
            stats[220] = min(stats[221], value)
        elif kind in (26, 27):
            cost = value if kind == 26 else stats[230] * value // 1000
            stats[230] = max(0, stats[230] - cost)
        elif kind == 28:
            stats[230] = value
        elif kind == 39:
            stats[230] += value
    updated, deleted = b"", b""
    if suggestion["card"]:
        data["deck"].append((suggestion["card"], suggestion["upgrade"], 1))
        updated, deleted = deck_changes(data)
    return wire._vi(1, 1), updated + advance(data), deleted


def shop_action(data, method, request):
    if data["status"] != ACTION or data["action_type"] != 13:
        raise ProgressError("No active consultation")
    if method == "StepShopStart":
        if not data["shop"]:
            data["shop"] = dict(data["shop_template"])
        return b"", b"".join(wire._ld(SHOP, row) for row in data["shop"].values()), b""
    if method == "StepShopEnd":
        changed = advance(data)
        data["shop"] = {}
        return b"", changed, b""

    position = num(request, 2)
    row = data["shop"].get(position)
    if not row or num(row, 3) or num(row, 12):
        raise ProgressError("This consultation product is unavailable")
    kind, upgrade, price = (num(row, tag) for tag in (4, 6, 7))
    ident = text(row, 5)
    stats = data["stats"]
    before = stats[230]
    if price > before:
        raise ProgressError("Insufficient P points")
    if kind == 3 and len(data["drinks"]) >= 3:
        raise ProgressError("P drink slots are full")
    updated, deleted, reward = b"", b"", b""
    if kind in (1, 3):
        if kind == 1:
            data["deck"].append((ident, upgrade, 1))
            updated, deleted = deck_changes(data)
        else:
            data["drinks"].append(ident)
        reward = wire._ld(1, wire._vi(1, kind) + wire._ld(2, ident.encode())
                          + wire._vi(3, upgrade) + wire._vi(4, 1))
    else:
        active = [index for index, card in enumerate(data["deck"]) if card is not None]
        index = num(request, 3)
        if index >= len(active):
            raise ProgressError("Invalid skill card index")
        slot = active[index]
        ident, upgrade, origin = data["deck"][slot]
        if kind == 997:
            next_level = upgrade + 1
            if next_level not in catalog()["card_levels"].get(ident, ()):
                raise ProgressError("The selected skill card cannot be upgraded")
            data["deck"][slot] = (ident, next_level, origin)
        elif kind == 998:
            if len(active) < 2:
                raise ProgressError("The deck must contain at least one skill card")
            data["deck"][slot] = None
        updated, deleted = deck_changes(data)
    stats[230] -= price
    product = insert(row, (3, 1))
    data["shop"][position] = product
    header = reward + wire._vi(2, before) + wire._vi(3, stats[230])
    return header, updated + wire._ld(SHOP, product), deleted


def finish_lesson(data, request):
    if data["status"] != ACTION or not data["battle_started"] or not 1 <= data["action_type"] <= 9:
        raise ProgressError("No active lesson")
    result = get(request, 1001, 2, None)
    action = data["action_type"]
    if result is None or num(result, 1) not in (0, action):
        raise ProgressError("Lesson result does not match the selected lesson")
    # The normal LessonEnd request can omit the redundant step type.
    # Result screens still read the type from the stored result rather than progress.f105.
    result = insert(result, (1, action))
    score, threshold, limit = (num(result, f) for f in (7, 8, 9))
    stamina = num(result, 5)
    point = num(result, 4)
    stats = data["stats"]
    if stamina > stats[221] or point > 2**31 - 1 or score > 2**31 - 1:
        raise ProgressError("Invalid lesson result values")
    stat = 200 + (data["action_type"] - 1) // 3
    growth = min(score, limit or score, 3000 - stats[stat])
    stats[stat] += growth
    stats[220], stats[230] = stamina, point
    result_type = 1 if threshold and score >= threshold else (2 if score else 3)
    data.update(exam_result=result, lesson_growth=wire._vi(stat - 199, growth), exam_result_type=result_type)
    return advance(data, (26, result_type))


def response(owner, method, request):
    data = getattr(owner, "_produce_start", None)
    if method == "CheckProgress":
        if not data:
            return wire.encrypt_body(common())
        if text(request, 3) not in ("", data["dpuuid"]) or text(request, 2) not in ("", data["client_id"]):
            return wire.encrypt_body(common())
        ids = wire._vi(1, 1) + wire._ld(4, data["puuid"].encode()) + wire._ld(5, data["dpuuid"].encode())
        return wire.encrypt_body(ids + common(progress(owner, data)))
    if method == "Retire":
        owner._produce_start = None
        deleted = wire._ld(wire.F_PRODUCE_PROGRESS, b"") if data else b""
        return wire.encrypt_body(common(deleted=deleted))
    if not data:
        raise ProgressError("No local produce session")
    if method in DEVICE_FIELD:
        device = text(request, DEVICE_FIELD[method])
        if device and device != data["dpuuid"]:
            raise ProgressError("Request belongs to another produce session")

    # The same completion retried after a transport failure replays its original response.
    cache_key = (method, request)
    completing = method in ("StepEvent", "StepRefresh", "StepLessonEnd", "StepShopBuy", "StepShopEnd")
    if completing and cache_key in data["replies"]:
        return data["replies"][cache_key]
    header, updated, deleted = b"", b"", b""
    if method == "Start":
        header = wire._ld(1, data["puuid"].encode()) + wire._ld(2, data["dpuuid"].encode())
    elif method == "ReadAdv":
        if data["status"] == OPENING:
            data["status"] = SELECT
    elif method == "Next":
        begin(data, num(request, 2))
    elif method == "StepEvent":
        header, updated, deleted = finish_event(data, request)
    elif method == "StepRefresh":
        if data["status"] != ACTION or data["action_type"] != 14:
            raise ProgressError("No active rest day")
        stats = data["stats"]
        before = stats[220]
        stats[220] = min(stats[221], before + stats[221] * catalog()["refresh"] // 1000)
        header = wire._vi(1, before) + wire._vi(2, stats[220])
        updated = advance(data, (27, stats[220] - before))
    elif method == "StepLessonStart":
        if data["status"] != ACTION or not 1 <= data["action_type"] <= 9:
            raise ProgressError("No active lesson")
        if data.get("lesson_start_response") and data["battle_started"]:
            return data["lesson_start_response"]
        data["battle_started"] = True
        updated, deleted = deck_changes(data)
    elif method == "StepLessonEnd":
        updated = finish_lesson(data, request)
    elif method in ("StepShopStart", "StepShopBuy", "StepShopEnd"):
        header, updated, deleted = shop_action(data, method, request)
    elif method == "StepPresentEnd":
        if data["status"] != SELECT or not data["exam_result"]:
            raise ProgressError("No local lesson results to complete")

    frame = wire.encrypt_body(header + common(progress(owner, data) + updated, deleted))
    if method == "StepLessonStart":
        data["lesson_start_response"] = frame
    if completing:
        data["replies"][cache_key] = frame
    return frame
