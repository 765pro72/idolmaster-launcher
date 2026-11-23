# -*- coding: utf-8 -*-
"""
unlock.py — gakumas 本地全解锁模块（可分享版）

设计原则：
  不替换整个存档，只把「解锁列表」拉满。
  流程：官方响应密文 → 解密 → 仅替换解锁相关字段 → 重新加密 → 返回
  效果：任何人使用自己的账号，账号数据（名字/等级/货币/进度）保持不变，
        但换装/卡牌/MV/剧情/道具全部显示为已解锁。

依赖：pycryptodome
"""

import hashlib
import json
import os
import random
import string
import uuid

from Crypto.Cipher import AES

# ---------------------------------------------------------------- 常量

SECKEY = bytes.fromhex(__import__("os").environ["GKM_"+ "SECKEY"])
AESKEY = bytes.fromhex(__import__("os").environ["GKM_"+ "AESKEY"])

# protobuf 字段号（从解密后的真实响应反推，与实测集合数量吻合）
F_USER_DATA       = 1          # UserGetResponse.userData
F_IDOL_CARD       = 119514127  # UserIdolCard
F_IDOL_CARD_SKIN  = 77005383   # UserIdolCardSkin
F_COSTUME         = 469968532  # UserCostume
F_COSTUME_HEAD    = 374617052  # UserCostumeHead
F_MUSIC           = 430828780  # UserMusic
F_MOVIE           = 389419008  # UserMovie
F_STORY           = 376356693  # UserStory
F_ITEM            = 208896283  # UserItem
F_PRODUCE_GROUP   = 374217157  # 旧名有误：此字段号实际是 UserProduceProgress（见 dump.cs TypeDefIndex 21179）
F_PRODUCE_PROGRESS = 374217157  # UserProduceProgress(f2=produceGroupId, f3=produceId, f6=charId, f7=idolCardId, f100=status, f103=stepNumber)
F_USER_PREFERENCE  = 296399830  # UserPreference(f2=photoButtonExecuteType, f3..f5=三个布尔开关)
F_PRODUCE_LIVE    = 317020496  # UserProduceLive (musicId + liveType + viewed)
F_SUPPORT_CARD    = 2234692    # UserSupportCard (s_card-*)
F_CHARACTER_COSTUME = 314243425
F_HOME_CHARACTER = 88325184
F_USER_CHARACTER = 370704040
F_PRODUCE_CARD    = 230550472
F_PRODUCE_CHARACTER = 494627662
F_MEISHI_BASE       = 10530785   # UserMeishiBaseAsset(f2=id)
F_MEISHI_ILLU       = 100237367  # UserMeishiIllustrationAsset(f2=id)
F_CHARACTER_ROSTER  = 449008014  # UserCharacterRoster(f2=charId, f3=viewedPlayable)  # UserProduceCharacter(f2=charId, f3=produceId, f5=playCount, f6=clearCount)
F_PRODUCE_IDOLCARD  = 110043881  # UserProduceIdolCard(f2=produceId, f3=idolCardId, f5=clearCount)
F_DEARNESS_STORY_LEVEL = 133642946   # UserDearnessStory(f2=charId, f3=level, f4=1, f6=1)
MAX_DEARNESS_LEVEL     = 37   # UserProduceCard(f2=cardId)
F_PRODUCE_ITEM    = 99080460    # UserProduceItem(f2=id)
F_PRODUCE_DRINK   = 65968245    # UserProduceDrink(f2=id)
F_SHOP_ITEM       = 455613547   # UserShopItem(f2=id)
F_PHOTO_POSE      = 439296203   # UserPhotoPose(f2=id)
F_BADGE           = 175219847   # UserBadge(f2=badgeId)
F_PHOTO_BG        = 99273345    # UserPhotoBackground(f2=id)
MAX_ITEM_QTY      = 9999
MAX_FAN_COUNT     = 999999999
MAX_INVITE        = 9999        # 邀请点数
MAX_MISSION_POINT = 999999      # 任务点数
MAX_FOLLOW_LIMIT  = 9999        # 关注上限
MAX_PVP_POWER     = 99999999    # PVP 战力
FAN_PER_CHARACTER = 76923076   # 13 角色 × 76923076 ≈ 999,999,988
# 隐私清理：把个人账号信息换成中性值
FAKE_PROFILE_NAME  = 'プロデューサー'   # 「制作人」
FAKE_USER_ID       = 'GKM00001'
FAKE_BIRTH_MONTH   = 1
FAKE_BIRTH_DAY     = 1
F_USER_INFO        = 342861366   # User(f2=P-ID, f3=uuid, f101=viewerId)  # UserCharacter(f2=charId, f3=fanCount, f7=dearnessPoint)
MAX_DEARNESS     = 1000000
F_PRODUCE_STORY = 117364693  # UserProduceStory(f2=storyId, f4=read)   # UserHomeCharacter(f2=homeType, f3=charId, f4=isRandom)  # UserCharacterCostume(f2=charId, f3=setType, f4=costumeId, f5=headId)
F_ACTION_POINT    = 238317234  # UserActionPoint（f2=当前体力）
F_INVITATION      = 11952121   # UserInvitation（不是体力，之前搞错了）
F_PROFILE         = 49977942   # UserProfile（f10=exp → PLV）
F_GEM             = 164091159  # 钻石（f1=数量）

# 拉满目标值
MAX_ACTION_POINT  = 9999
MAX_PROFILE_EXP   = 999999999
MAX_GEM           = 99999999
F_USER_PROFILE    = 49977942   # UserProfile（f2=name, f10=exp, f11=fan）
F_USER_GEM        = 164091159  # 钻石（f1 = 数量）
F_USER_ACTION     = None       # 体力（待定位）
F_TUTORIAL        = 201375606  # UserTutorial(f2=type, f3=step)
F_SUPPORT_DECK    = 286307933  # UserProduceSupportCardDeck
F_MEMORY_DECK     = 162217088  # UserProduceMemoryDeck
F_MEMORY          = 163783316  # UserMemory

BASE62 = string.ascii_letters + string.digits


# ---------------------------------------------------------------- wire format

def _rv(b, i):
    r = 0
    s = 0
    while i < len(b):
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            return r, i
        s += 7
        if s > 63:
            raise ValueError("varint too long")
    raise ValueError("eof")


def _wv(n):
    o = bytearray()
    while True:
        x = n & 0x7F
        n >>= 7
        if n:
            o.append(x | 0x80)
        else:
            o.append(x)
            break
    return bytes(o)


def _ld(fn, payload):
    return _wv((fn << 3) | 2) + _wv(len(payload)) + payload


def _vi(fn, v):
    return _wv((fn << 3) | 0) + _wv(v)


def _replace_idol_card_skin(entry, skin_id):
    """只替换当前卡面，保留等级、开花、时间戳及未知字段。"""
    return b''.join(raw for fn, wt, raw, ln, val in fields(entry) if fn != 5) + _ld(5, skin_id.encode())


def fields(b):
    """返回 [(field_no, wire_type, raw_slice, len_or_val, value_bytes)]"""
    out = []
    i = 0
    while i < len(b):
        st = i
        k, i = _rv(b, i)
        fn = k >> 3
        wt = k & 7
        if wt == 0:
            v, i = _rv(b, i)
            out.append((fn, 0, b[st:i], v, None))
        elif wt == 2:
            ln, i = _rv(b, i)
            if i + ln > len(b):
                raise ValueError("len overflow")
            out.append((fn, 2, b[st:i + ln], ln, b[i:i + ln]))
            i += ln
        elif wt == 5:
            out.append((fn, 5, b[st:i + 4], 4, b[i:i + 4]))
            i += 4
        elif wt == 1:
            out.append((fn, 1, b[st:i + 8], 8, b[i:i + 8]))
            i += 8
        elif wt == 3:
            # STARTGROUP：跳到匹配的 ENDGROUP（wiretype 4）
            depth = 1
            body_start = i
            while depth > 0 and i < len(b):
                k2, i = _rv(b, i)
                w2 = k2 & 7
                if w2 == 3:
                    depth += 1
                elif w2 == 4:
                    depth -= 1
                elif w2 == 0:
                    _, i = _rv(b, i)
                elif w2 == 2:
                    ln2, i = _rv(b, i)
                    i += ln2
                elif w2 == 1:
                    i += 8
                elif w2 == 5:
                    i += 4
                else:
                    raise ValueError("wiretype %d in group" % w2)
            body = b[body_start:max(body_start, i - 1)]
            out.append((fn, 3, b[st:i], len(body), body))
        else:
            raise ValueError("wiretype %d" % wt)
    return out


# ---------------------------------------------------------------- 加解密

def decrypt_body(rb):
    """把完整响应体解成明文 protobuf；失败返回 None"""
    if len(rb) < 5 + 4:
        return None
    ln = int.from_bytes(rb[1:5], "big")
    if len(rb) < 5 + ln or ln < 5:
        return None
    pl = rb[5:5 + ln]
    kl = pl[3]
    if len(pl) < 4 + kl:
        return None
    hk = pl[4:4 + kl]
    ct = pl[4 + kl:]
    if not ct or len(ct) % 16:
        return None
    try:
        pt = AES.new(AESKEY, AES.MODE_CBC, hashlib.md5(SECKEY + hk).digest()).decrypt(ct)
    except Exception:
        return None
    if not pt:
        return None
    pad = pt[-1]
    if not (1 <= pad <= 16 and pt[-pad:] == bytes([pad]) * pad):
        return None
    return pt[:-pad]


def encrypt_body(pt, flags=0):
    """把明文 protobuf 重新包成完整响应体"""
    hk = "".join(random.choice(BASE62) for _ in range(29)).encode()
    hdr = len(hk).to_bytes(0, "big") if False else None
    header = (2 + len(hk)).to_bytes(2, "little") + bytes([flags]) + bytes([len(hk)]) + hk
    iv = hashlib.md5(SECKEY + hk).digest()
    pad = 16 - (len(pt) % 16)
    ct = AES.new(AESKEY, AES.MODE_CBC, iv).encrypt(pt + bytes([pad]) * pad)
    payload = header + ct
    return b"\x00" + len(payload).to_bytes(4, "big") + payload


# ---------------------------------------------------------------- 数据源

def load_master(master_dir):
    """读 masterTrans/*.json，返回各列表的全量 id"""
    def ids(name):
        p = os.path.join(master_dir, name)
        if not os.path.isfile(p):
            return []
        with open(p, encoding="utf-8") as f:
            d = json.load(f)
        return [x["id"] for x in d.get("data", [])]

    cards = ids("IdolCard.json")
    skins = ids("IdolCardSkin.json")
    cards = ids("IdolCard.json")
    # 优先合并「最新 Master 提取的卡/卡面」（含 shro-3-022 等新卡）
    _lc_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'live-cards.json')
    if os.path.isfile(_lc_path):
        try:
            with open(_lc_path, encoding='utf-8') as _lf:
                _lc = json.load(_lf)
            cards = list(dict.fromkeys(cards + (_lc.get('cards') or [])))
            skins = list(dict.fromkeys(skins + (_lc.get('skins') or [])))
        except Exception:
            pass
    costumes = ids("Costume.json")
    # 合并「最新 Master 的全部服装/头饰」（含 2026 年新服装）
    _lc2_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'live-costumes.json')
    if os.path.isfile(_lc2_path):
        try:
            with open(_lc2_path, encoding='utf-8') as _lf2:
                _lc2 = json.load(_lf2)
            costumes = list(dict.fromkeys(costumes + (_lc2.get('costumes') or [])))
        except Exception:
            pass
    heads = ids("CostumeHead.json")
    if os.path.isfile(_lc2_path):
        try:
            with open(_lc2_path, encoding='utf-8') as _lf3:
                _lc3 = json.load(_lf3)
            heads = list(dict.fromkeys(heads + (_lc3.get('heads') or [])))
        except Exception:
            pass
    music = ids("Music.json")
    support_cards = ids("SupportCard.json")
    items = ids("Item.json")
    # 合并「最新 Master 的支援卡 / 道具」
    _le_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'live-extra.json')
    if os.path.isfile(_le_path):
        try:
            with open(_le_path, encoding='utf-8') as _lef:
                _le = json.load(_lef)
            support_cards = list(dict.fromkeys(support_cards + (_le.get('support') or [])))
            items = list(dict.fromkeys(items + (_le.get('items') or [])))
        except Exception:
            pass
    produce_stories = ids("ProduceStory.json")
    # 合并「最新 Master 的支援卡剧情」（新卡 15 个）
    _lps_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'live-pstories.json')
    if os.path.isfile(_lps_path):
        try:
            with open(_lps_path, encoding='utf-8') as _lpsf:
                _lps = json.load(_lpsf)
            produce_stories = list(dict.fromkeys(produce_stories + (_lps or [])))
        except Exception:
            pass
    produce_cards = ids("ProduceCard.json")
    produce_ids = ids("Produce.json")
    # 优先用「从客户端缓存解密出来的最新 Music 列表」（524 个 ID，含 music-char-* 角色歌曲）
    _live_ids_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'music-ids.json')
    if os.path.isfile(_live_ids_path):
        try:
            with open(_live_ids_path, encoding='utf-8') as _f:
                _mj = json.load(_f)
            live_music = _mj.get('every') or ids("Music.json")
        except Exception:
            live_music = ids("Music.json")
    else:
        live_music = ids("Music.json")
    meishi_base = ids("MeishiBaseAsset.json")
    photo_poses = ids("PhotoPose.json")
    photo_bgs = ids("PhotoBackground.json")
    # 合并「最新 Master 的拍照姿势/背景」
    _lp_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'live-photo.json')
    if os.path.isfile(_lp_path):
        try:
            with open(_lp_path, encoding='utf-8') as _lpf:
                _lp = json.load(_lpf)
            photo_poses = list(dict.fromkeys(photo_poses + (_lp.get('poses') or [])))
            photo_bgs = list(dict.fromkeys(photo_bgs + (_lp.get('bgs') or [])))
        except Exception:
            pass
    _badge_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'badge-ids.json')
    if os.path.isfile(_badge_path):
        try:
            with open(_badge_path, encoding='utf-8') as _bf:
                badges = json.load(_bf)
        except Exception:
            badges = ids("Badge.json")
    else:
        badges = ids("Badge.json")
    meishi_illu = ids("MeishiIllustrationAsset.json")
    produce_items = ids("ProduceItem.json") if os.path.isfile(os.path.join(master_dir, "ProduceItem.json")) else []
    produce_drinks = ids("ProduceDrink.json") if os.path.isfile(os.path.join(master_dir, "ProduceDrink.json")) else []
    shop_items = ids("ShopItem.json") if os.path.isfile(os.path.join(master_dir, "ShopItem.json")) else []
    # 可播放 Live 的曲目：music-all-* / music-char-* / music-unit-*，排除纯音乐 -inst
    # 注意：live_music 已在上方优先用 music-ids.json（524 个，含角色歌曲），此处不再覆盖
    import re as _re
    if not live_music:
        live_music = [m for m in music
                      if _re.match(r"^music-(all|char|unit)-", m) and not m.endswith("-inst")]
    story = ids("Story.json")
    skin_set = set(skins)

    def to_skin(c):
        return "i_card-skin-" + c[7:] if c.startswith("i_card-") else None

    # 全部保留（152 张）。之前排除的 5 张（amao-3-018 / hmsz-3-020 / jsna-3-017 /
    # kcna-3-018 / ssmk-3-018）实际是「消耗道具解放的特殊卡」，不是没有立绘
    # cards = [c for c in cards if to_skin(c) in skin_set]
    # nasr 的服装/头饰也加上（用户要求全开）
    # costumes/heads 不再过滤 nasr

    return {
        "cards": cards,
        "skins": skins,
        "costumes": costumes,
        "heads": heads,
        "music": music,
        "support_cards": support_cards,
        "produce_stories": produce_stories,
        "produce_cards": produce_cards,
        "produce_ids": produce_ids,
        "badges": badges,
        "photo_poses": photo_poses,
        "photo_bgs": photo_bgs,
        "live_music": live_music,
        "meishi_base": meishi_base,
        "meishi_illu": meishi_illu,
        "produce_items": produce_items,
        "produce_drinks": produce_drinks,
        "shop_items": shop_items,
        "live_music": live_music,
        "story": story,
        "items": items,
        "to_skin": to_skin,
    }


# ---------------------------------------------------------------- 合并逻辑

def patch_scalar_fields(msg, patches):
    """对 message 做「精准改值」：只替换指定字段的 varint 值，其余字段原样保留。
    patches: {field_no: new_value}
    """
    out = bytearray()
    done = set()
    for fn, wt, raw, ln, val in fields(msg):
        if wt == 0 and fn in patches:
            out += _vi(fn, patches[fn])
            done.add(fn)
        else:
            out += raw
    # 原本不存在的字段，追加上
    for fn, v in patches.items():
        if fn not in done:
            out += _vi(fn, v)
    return bytes(out)



def patch_profile_fields(msg, prof, vals):
    """精准改 UserProfile：名字/评论是 string，生日/经验/粉丝是 varint。

    和 patch_scalar_fields 的区别：那个只处理 varint，改不了名字。
    patches 语义：
      f2  名字(string)      f4 生日月   f5 生日日
      f9  评论(string)      f10 经验   f11 粉丝
    评论为空时「删掉该字段」，而不是写空串。
    """
    # 顶栏是共享存档，留空时必须落成中性值，不能带作者真名
    # 留空 = 不干预，保留存档原值（分发包里默认就是作者给的那份资料）
    name = str(prof.get('name') or '')
    comment = str(prof.get('comment') or '')
    bm = int(prof.get('birth_month') or 0)
    bd = int(prof.get('birth_day') or 0)
    exp = int(vals.get('level_exp', 999999999) or 999999999)
    fans = MAX_FAN_COUNT

    out = bytearray()
    seen = set()
    for fn, wt, raw, ln, val in fields(msg):
        if fn == 2 and wt == 2 and name:
            out += _ld(2, name.encode())
            seen.add(2)
        elif fn == 4 and wt == 0:
            out += _vi(4, bm) if bm else raw
            seen.add(4)
        elif fn == 5 and wt == 0:
            out += _vi(5, bd) if bd else raw
            seen.add(5)
        elif fn == 9 and wt == 2:
            out += _ld(9, comment.encode()) if comment else raw
            seen.add(9)
        elif fn == 10 and wt == 0:
            out += _vi(10, exp)
            seen.add(10)
        elif fn == 11 and wt == 0:
            out += _vi(11, fans)
            seen.add(11)
        else:
            out += raw

    if name and 2 not in seen:
        out += _ld(2, name.encode())
    if bm and 4 not in seen:
        out += _vi(4, bm)
    if bd and 5 not in seen:
        out += _vi(5, bd)
    if comment and 9 not in seen:
        out += _ld(9, comment.encode())
    return bytes(out)


def patch_profile_response(msg, prof):
    # 什么都没配 -> 原样返回，资料页显示使用者自己账号的真实信息
    if not (str(prof.get('name') or '') or str(prof.get('comment') or '')
            or int(prof.get('birth_month') or 0)):
        return msg
    """改 ProfileGetResponse：f3=名字 f4=评论 f5=生日{1:月,2:日}。

    保留 f1(publicUserId) 不动 —— 它是 P-ID，和官服数据要对得上。
    """
    # 顶栏是共享存档，留空时必须落成中性值，不能带作者真名
    # 留空 = 不干预，保留存档原值（分发包里默认就是作者给的那份资料）
    name = str(prof.get('name') or '')
    comment = str(prof.get('comment') or '')
    bm = int(prof.get('birth_month') or 0)
    bd = int(prof.get('birth_day') or 0)

    out = bytearray()
    seen = set()
    for fn, wt, raw, ln, val in fields(msg):
        if fn == 3 and wt == 2 and name:
            out += _ld(3, name.encode())
            seen.add(3)
        elif fn == 4 and wt == 2:
            out += _ld(4, comment.encode()) if comment else raw
            seen.add(4)
        elif fn == 5 and wt == 2:
            out += _ld(5, _vi(1, bm) + _vi(2, bd)) if (bm and bd) else raw
            seen.add(5)
        else:
            out += raw

    if name and 3 not in seen:
        out += _ld(3, name.encode())
    if comment and 4 not in seen:
        out += _ld(4, comment.encode())
    if bm and bd and 5 not in seen:
        out += _ld(5, _vi(1, bm) + _vi(2, bd))
    return bytes(out)


def merge_unlock(pt, data, created_time=None):
    """
    把明文 UserGetResponse 里的解锁列表替换成全量。
    其他字段（余额/进度/名字/等级等）原样保留。
    """
    top = fields(pt)
    ud = None
    for fn, wt, raw, ln, val in top:
        if fn == F_USER_DATA and wt == 2:
            ud = val
            break
    if ud is None:
        return pt

    udf = fields(ud)
    # FILL_CHARACTER_COSTUME: 先收集旧的 (charId, setType) 和默认衣装
    _old_cc = {}
    _char_default = {}
    for _fn, _wt, _raw, _ln, _val in fields(ud):
        if _fn != F_CHARACTER_COSTUME or _wt != 2:
            continue
        _c = _st = _cos = _hd = None
        for _a, _b, _, _dd, _vv in fields(_val):
            if _a == 2 and _b == 2: _c = _vv.decode('utf-8', 'replace')
            elif _a == 3 and _b == 0: _st = _dd
            elif _a == 4 and _b == 2: _cos = _vv.decode('utf-8', 'replace')
            elif _a == 5 and _b == 2: _hd = _vv.decode('utf-8', 'replace')
        if _c and _st is not None:
            _old_cc[(_c, _st)] = (_cos, _hd)
            if _c not in _char_default and _cos:
                _char_default[_c] = (_cos, _hd)

    replace = {F_IDOL_CARD, F_IDOL_CARD_SKIN, F_COSTUME, F_COSTUME_HEAD,
               F_MUSIC, F_STORY, F_PRODUCE_LIVE, F_SUPPORT_CARD,
               F_CHARACTER_COSTUME, F_PRODUCE_STORY,
               F_ITEM, F_PRODUCE_CARD, F_PRODUCE_ITEM, F_PRODUCE_DRINK, F_SHOP_ITEM,
               F_DEARNESS_STORY_LEVEL, F_PRODUCE_CHARACTER, F_PRODUCE_IDOLCARD,
               F_MEISHI_BASE, F_MEISHI_ILLU, F_CHARACTER_ROSTER, F_HOME_CHARACTER,
               F_PRODUCE_GROUP}

    # 取一个已有的 createdTime 作为新卡的时间戳
    ts = created_time
    if ts is None:
        for fn, wt, raw, ln, val in udf:
            if fn == F_IDOL_CARD and wt == 2:
                try:
                    for f2, w2, _, l2, v2 in fields(val):
                        if f2 == 999 and w2 == 0:
                            ts = l2
                            break
                except Exception:
                    pass
                if ts:
                    break

    out = bytearray()
    for fn, wt, raw, ln, val in udf:
        if fn in replace:
            continue
        out += raw

    to_skin = data["to_skin"]

    # 卡牌
    for c in data["cards"]:
        # 真实响应的 IdolCard 只有 f2(id) + f3(等级) + f5(skin) + f999(时间戳)，没有 f4
        e = _ld(2, c.encode()) + _vi(3, 5) + _vi(4, 5) + _ld(5, to_skin(c).encode())
        if ts:
            e += _vi(999, ts)
        out += _ld(F_IDOL_CARD, e)

    # 卡面皮肤
    for s in data["skins"]:
        out += _ld(F_IDOL_CARD_SKIN, _ld(2, s.encode()))

    # 3D 服装（f2=costumeId, f3=checked）
    for c in data["costumes"]:
        out += _ld(F_COSTUME, _ld(2, c.encode()) + _vi(3, 1))

    # 头饰（f2=costumeHeadId, f3=checked）
    for h in data["heads"]:
        out += _ld(F_COSTUME_HEAD, _ld(2, h.encode()) + _vi(3, 1))

    # 音乐：用「客户端缓存解密出的最新列表」（524 个，含 music-all-hski-019 等角色版）
    _music_src = data.get("live_music") or data["music"]
    _music_all = list(dict.fromkeys(list(data["music"]) + list(_music_src)))
    for m in _music_all:
        out += _ld(F_MUSIC, _ld(2, m.encode()))

    # FILL_CHARACTER_COSTUME: 从 Costume 全量数据提取所有角色（排除 nasr），每个 4 槽位
    _all_chars = {}
    for _cos in data.get("costumes", []):
        _p = _cos.split('-')[0]
        if _p in ('nasr',) or not _p:
            continue
        if _p not in _all_chars:
            _all_chars[_p] = _cos
    # 合并：旧数据优先，其次用 Costume 数据
    # 从 CostumeHead 全量数据建立「角色 -> 默认发型」
    _heads_by_char = {}
    for _hd in data.get("heads", []):
        _body = _hd[len('costume_head_'):] if _hd.startswith('costume_head_') else _hd
        _cp = _body.split('-')[0]
        if _cp and _cp not in _heads_by_char:
            _heads_by_char[_cp] = _hd
    for _c, _cos in _all_chars.items():
        if _c not in _char_default:
            _char_default[_c] = (_cos, _heads_by_char.get(_c))

    # FILL_CHARACTER_COSTUME: 每个角色 4 个槽位（1=Home 2=ProduceSchedule 3=ProduceLive 4=ProduceTraining）
    for _c, (_dcos, _dhd) in _char_default.items():
        for _st in (1, 2, 3, 4):
            _cos, _hd = _old_cc.get((_c, _st), (None, None))
            if not _cos:
                _cos, _hd = _dcos, _dhd
            _e = _ld(2, _c.encode()) + _vi(3, _st) + _ld(4, _cos.encode())
            if _hd:
                _e += _ld(5, _hd.encode())
            out += _ld(F_CHARACTER_COSTUME, _e)

    # 「記録の鍵」（活動剧情解锁道具）必须先存在才能解锁
    for _k in ('item-commu-key-001',):
        out += _ld(F_ITEM, _ld(2, _k.encode()) + _vi(4, 9999))

    # 剧情（f2=storyId, f4=isUnlocked, f6=read）
    for s in data["story"]:
        out += _ld(F_STORY, _ld(2, s.encode()) + _vi(4, 1) + _vi(6, 1))

    # ★ 补充：masterTrans 里缺失的「新活动」剧情（从游戏内 Story/Unlock 请求抓到的真实 ID）
    # 只加确认存在的：story-box-gasha-event-022 = 「ワタシ、小悪魔♡」5 话
    # ⚠ 不能乱加不存在的 ID，客户端解析会崩
    for _ep in range(1, 6):
        _sid = f'story-box-gasha-event-022-{_ep:02d}'
        out += _ld(F_STORY, _ld(2, _sid.encode()) + _vi(4, 1) + _vi(6, 1))



    # 支援卡（f2=id, f3=stockQuantity(解放数), f4=level, f5=levelLimitRank(突破), f999=createdTime）
    for sc in data.get("support_cards", []):
        e = (_ld(2, sc.encode())
             + _vi(3, 5)      # 解放数 5/5
             + _vi(4, 60)     # 等级满
             + _vi(5, 4))     # 突破等级最大
        if ts:
            e += _vi(999, ts)
        out += _ld(F_SUPPORT_CARD, e)

    # 亲密度等级拉满：每个角色 level 1-37（决定「10/37」里的等级）
    _chars_for_dearness = set()
    for _cc in data.get("costumes", []):
        _p = _cc.split('-')[0]
        if _p and _p != 'nasr':
            _chars_for_dearness.add(_p)
    for _ch in sorted(_chars_for_dearness):
        for _lv in range(1, MAX_DEARNESS_LEVEL + 1):
            _e = _ld(2, _ch.encode()) + _vi(3, _lv) + _vi(4, 1)
            if ts:
                _e += _vi(5, ts)
            _e += _vi(6, 1)
            out += _ld(F_DEARNESS_STORY_LEVEL, _e)

    # 道具全量（item-* 357 个，各 9999）
    for _it in data.get("items", []):
        out += _ld(F_ITEM, _ld(2, _it.encode()) + _vi(4, MAX_ITEM_QTY))

    # 偶像解放：每个角色 × 每个培育路线
    _all_chars_pc = set()
    for _cc in data.get("costumes", []):
        _p = _cc.split('-')[0]
        if _p and _p != 'nasr':
            _all_chars_pc.add(_p)
    _produce_ids = data.get("produce_ids", [])
    for _ch in sorted(_all_chars_pc):
        for _pd in _produce_ids:
            out += _ld(F_PRODUCE_CHARACTER,
                       _ld(2, _ch.encode()) + _ld(3, _pd.encode()) + _vi(5, 24) + _vi(6, 24))

    # 照片姿势 / 背景（拍照功能用）
    for _pp in data.get("photo_poses", []):
        out += _ld(F_PHOTO_POSE, _ld(2, _pp.encode()))
    for _pb in data.get("photo_bgs", []):
        out += _ld(F_PHOTO_BG, _ld(2, _pb.encode()))

    # 徽章（全量）
    for _bd in data.get("badges", []):
        out += _ld(F_BADGE, _ld(2, _bd.encode()))

    # 名片底座 / 名片插画（全量）
    for _aid in data.get("meishi_base", []):
        out += _ld(F_MEISHI_BASE, _ld(2, _aid.encode()))
    for _aid in data.get("meishi_illu", []):
        out += _ld(F_MEISHI_ILLU, _ld(2, _aid.encode()))

    # ★ 主页角色配置必须为空！
    # 原因：HomeCharacterChangeSheetPresenter.SelectCharacterAsync 会把
    #   UserHomeCharacterList + UserCharacterList 合并后 ToDictionary(x=>x.id)，
    # 两个源都有同一个角色 -> ArgumentException: duplicate key
    # 正常游戏初始状态这个列表是空的，客户端用默认值渲染。
    # F_HOME_CHARACTER 仍在 replace 集合里，所以模板里的旧数据会被删掉。

    # 角色名册（13 角色全部 viewedPlayable）
    for _ch in sorted(_all_chars_pc):
        out += _ld(F_CHARACTER_ROSTER, _ld(2, _ch.encode()) + _vi(3, 1))

    # 偶像卡 × 培育路线（每张卡都在每个路线下开放）
    for _card in data.get("cards", []):
        for _pd in _produce_ids:
            out += _ld(F_PRODUCE_IDOLCARD,
                       _ld(2, _pd.encode()) + _ld(3, _card.encode()) + _vi(5, 1))

    # 培育卡全量（p_card-* 1724）
    for _pc in data.get("produce_cards", []):
        out += _ld(F_PRODUCE_CARD, _ld(2, _pc.encode()))

    # 培育道具 / 饮料 / 商店物品
    for _pi in data.get("produce_items", []):
        out += _ld(F_PRODUCE_ITEM, _ld(2, _pi.encode()))
    for _pd in data.get("produce_drinks", []):
        out += _ld(F_PRODUCE_DRINK, _ld(2, _pd.encode()))
    for _si in data.get("shop_items", []):
        out += _ld(F_SHOP_ITEM, _ld(2, _si.encode()))

    # 培育剧情（p_story-* 全部 3427 条，含支援卡 514 / 活动 1677 / 培育 442 / 偶像卡 342 / 亲密度 141）
    for _ps in data.get("produce_stories", []):
        out += _ld(F_PRODUCE_STORY, _ld(2, _ps.encode()) + _vi(4, 1))

    # MV 演出（f2=musicId, f3=liveType, f4=viewed, f5=unlockCharacterIds, f6=viewedCharacterIds）
    # ProduceLiveType: TrueEnd=1, A=2, B=3, C=4, D=5, E=6
    # f5 是「这个 MV 有哪些角色的版本可看」—— 不填则所有角色版本都锁着
    # True LIVE 每个曲目下面有「角色版本」格子（Campus mode!! 有 1 格，
    # ミラクルナナウ 为 0 格 = 「培育用偶像未登场」），那是 f5/f6 决定的。
    # 所以：每个 liveType 都发一条，并且 f5/f6 带上全部角色。
    _mv_chars = sorted({
        _c.split('-')[0] for _c in data.get("costumes", [])
        if _c.split('-')[0] not in ('nasr', '')
    })
    _mv_uc = b"".join(_ld(5, _ch.encode()) for _ch in _mv_chars)
    _mv_vc = b"".join(_ld(6, _ch.encode()) for _ch in _mv_chars)
    # 实验：master 的 ProduceLive 表里有记录的曲目（如 music-all-atbm-018）走「master 定义」
    # 那条路，我们给的 f5/f6 不被采用 -> 0 格。对这些曲目不发 ProduceLive，
    # 让它落到「用户数据」路径（Campus mode!! 那条）。
    # master 的 ProduceLive 表里已有定义的曲目，客户端走 master 判定（我们给的
    # f5/f6 不生效 -> 0 格）；跳过它们，让它落到「用户数据」路径（老歌就是这条）。
    _mv_skip = set()  # 2026-10-23 回滚：跳过会让曲目从列表消失
    for m in data.get("live_music", []):
        if m in _mv_skip:
            continue
        for _lt in (1, 2, 3, 4, 5, 6):
            out += _ld(F_PRODUCE_LIVE,
                       _ld(2, m.encode()) + _vi(3, _lt) + _vi(4, 1) + _mv_uc + _mv_vc)

    # 育成组记录（True LIVE 图鉴按 produce_group 展示）。
    # 真实存档里只有 1 条（produce_group-001 -> produce-002 -> kcna -> i_card-kcna-3-000），
    # 于是只有玩过的那组能看，其他组一律「培育用偶像未登场」。这里补齐 3 个组 × 全部角色。
    # 组的育成主线（来自 master-live/ProduceGroup.db）：
    #   001 -> produce-001/002/003/006
    #   002 -> produce-004/005   (NEXT IDOL AUDITION)
    #   003 -> produce-007/008   (Hatsuboshi IDOL FESTIVAL)
    # 回滚（2026-10-22 23:55）：注入 UserProduceGroup 会让育成界面异常，先停用。
    # True LIVE 的「角色版本」需要 master 层的定义，用户数据侧伪造无效。
    _group_produces = {}
    for _g, _pds in _group_produces.items():
        for _pd in _pds:
            for _ch in _mv_chars:
                out += _ld(F_PRODUCE_GROUP,
                           _ld(2, _g.encode()) + _ld(3, _pd.encode()) + _vi(5, 1)
                           + _ld(6, _ch.encode()) + _ld(7, ("i_card-%s-3-000" % _ch).encode()))

    # ── 等级 / 货币 / 体力：对已保留的 message 做精准改值 ──
    patched = bytearray()
    for fn, wt, raw, ln, val in fields(bytes(out)):
        if wt == 2 and fn == F_ACTION_POINT:
            patched += _ld(fn, patch_scalar_fields(val, {2: MAX_ACTION_POINT}))
        elif wt == 2 and fn == F_GEM:
            patched += _ld(fn, patch_scalar_fields(val, {1: MAX_GEM, 2: MAX_GEM}))
        elif wt == 2 and fn == F_USER_CHARACTER:
            patched += _ld(fn, patch_scalar_fields(val, {3: FAN_PER_CHARACTER, 7: MAX_DEARNESS}))
        elif wt == 2 and fn == F_PROFILE:
            # 只拉满 exp 和 fanCount，不改名字/评论/生日（保持原始资料）
            patched += _ld(fn, patch_scalar_fields(val, {10: MAX_PROFILE_EXP, 11: MAX_FAN_COUNT}))
        elif wt == 2 and fn == F_INVITATION:
            patched += _ld(fn, patch_scalar_fields(val, {2: MAX_INVITE, 3: MAX_INVITE}))
        elif wt == 2 and fn == F_USER_INFO:
            # 不改 P-ID（改了官服就查不到 Profile/Get 数据）
            patched += raw
        else:
            patched += raw

    return _ld(F_USER_DATA, bytes(patched))


# ---------------------------------------------------------------- 对外入口

# ---------------------------------------------------------------- 静态存档（不保留原账号）

STATIC_ARCHIVE = None          # 由 Unlocker.build_static() 填充
STATIC_MODE = os.environ.get("GKM_STATIC_USER", "1") == "1"   # 默认静态：不保留原账号


# ---------------------------------------------------------------- 用户配置

CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'config.json')

# 中文键 -> 内部键
_PROF_MAP = {'名字': 'name', 'P-ID': 'user_id', 'PID': 'user_id',
             '生日_月': 'birth_month', '生日_日': 'birth_day',
             '评论': 'comment'}
_VAL_MAP = {'等级经验': 'level_exp', '钻石': 'gem', '体力': 'action_point',
            '亲密度点数': 'dearness_point', '道具数量': 'item_quantity'}

DEFAULT_PROFILE = {'name': 'プロデューサー', 'user_id': 'GKM00001',
                   'birth_month': 0, 'birth_day': 0, 'comment': ''}
DEFAULT_VALUES = {'level_exp': 999999999, 'gem': 99999999, 'action_point': 9999,
                  'dearness_point': 1000000, 'item_quantity': 9999}


def load_config():
    """读 config.json（同时支持中文和英文键）"""
    cfg = {'profile': dict(DEFAULT_PROFILE), 'values': dict(DEFAULT_VALUES), 'unlock': {}}
    if not os.path.isfile(CONFIG_PATH):
        return cfg
    try:
        with open(CONFIG_PATH, encoding='utf-8-sig') as f:
            raw = json.load(f)
    except Exception:
        return cfg

    p = raw.get('个人资料') or raw.get('profile')
    if p is None:
        # 扁平结构：顶层直接写名字/P-ID
        p = raw if any(k in raw for k in _PROF_MAP) else {}
    for cn, en in _PROF_MAP.items():
        if cn in p:
            cfg['profile'][en] = p[cn]
        if en in p:
            cfg['profile'][en] = p[en]

    v = raw.get('数值') or raw.get('values') or {}
    for cn, en in _VAL_MAP.items():
        if cn in v:
            cfg['values'][en] = v[cn]
        if en in v:
            cfg['values'][en] = v[en]

    cfg['unlock'] = raw.get('解锁开关') or raw.get('unlock') or {}
    return cfg


def apply_config_to_plain(pt, cfg=None):
    """对【明文】UserGetResponse 应用 config（个人资料 + 数值）。

    抽出来的原因：build_static 重建存档后也要洗身份，
    否则模板里的真实名字/评论会一直带回来。
    """
    """把 config.json 里的个人资料 / 数值应用到已有的静态存档上。
    这样不需要 masterTrans 也能改配置。
    """
    if cfg is None:
        cfg = load_config()
    prof = cfg.get('profile') or {}
    vals = cfg.get('values') or {}

    ud = None
    for fn, wt, raw, ln, val in fields(pt):
        if fn == F_USER_DATA and wt == 2:
            ud = val
            break
    if ud is None:
        return pt

    new_ud = bytearray()
    for fn, wt, raw, ln, val in fields(ud):
        if wt == 2 and fn == F_PROFILE:
            new_ud += _ld(fn, patch_profile_fields(val, prof, vals))
        elif wt == 2 and fn == F_USER_INFO:
            # 不改 P-ID（改了官服就查不到 Profile/Get 数据）
            new_ud += raw
        elif wt == 2 and fn == F_GEM:
            new_ud += _ld(fn, patch_scalar_fields(val, {1: int(vals.get('gem', 99999999)), 2: int(vals.get('gem', 99999999))}))
        elif wt == 2 and fn == F_ACTION_POINT:
            ap = int(vals.get('action_point', 9999))
            new_ud += _ld(fn, patch_scalar_fields(val, {2: ap, 3: ap}))
        elif wt == 2 and fn == F_USER_CHARACTER:
            new_ud += _ld(fn, patch_scalar_fields(val, {3: FAN_PER_CHARACTER,
                                                        7: int(vals.get('dearness_point', 1000000))}))
        else:
            new_ud += raw

    return b''.join(_ld(fn, bytes(new_ud)) if fn == F_USER_DATA and wt == 2 else raw
                    for fn, wt, raw, ln, val in fields(pt))


# ---------------------------------------------------------------- 合并模式

# 要覆盖的「解锁列表」（保留真实身份信息，只拿这些）
UNLOCK_LIST_FIELDS = {
    F_COSTUME, F_COSTUME_HEAD, F_IDOL_CARD, F_IDOL_CARD_SKIN,
    F_MUSIC, F_STORY, F_PRODUCE_LIVE, F_SUPPORT_CARD,
    F_CHARACTER_COSTUME, F_PRODUCE_STORY, F_ITEM,
    F_PRODUCE_CARD, F_PRODUCE_ITEM, F_PRODUCE_DRINK, F_SHOP_ITEM,
    F_DEARNESS_STORY_LEVEL, F_PRODUCE_CHARACTER, F_PRODUCE_IDOLCARD,
    F_MEISHI_BASE, F_MEISHI_ILLU, F_CHARACTER_ROSTER, F_HOME_CHARACTER,
}

# 要覆盖的「数值」（体力/钻石/亲密度）
UNLOCK_VALUE_FIELDS = {F_ACTION_POINT, F_GEM, F_USER_CHARACTER}


def apply_config_to_blob(blob, cfg=None):
    """对【加密】存档应用 config（供服务加载文件时用）"""
    pt = decrypt_body(blob)
    if pt is None:
        return blob
    return encrypt_body(apply_config_to_plain(pt, cfg))


def merge_with_static(real_blob, static_blob):
    """用「官服真实数据」做基础，用「静态存档」的解锁列表覆盖。
    名字 / P-ID / 粉丝数 / 等级 → 保留真实
    服装 / 卡牌 / 剧情 / MV / 道具 → 全解锁
    """
    real_pt = decrypt_body(real_blob)
    static_pt = decrypt_body(static_blob)
    if real_pt is None or static_pt is None:
        return real_blob

    def get_ud(pt):
        for fn, wt, raw, ln, val in fields(pt):
            if fn == F_USER_DATA and wt == 2:
                return val
        return None

    real_ud = get_ud(real_pt)
    static_ud = get_ud(static_pt)
    if real_ud is None or static_ud is None:
        return real_blob

    # 1) 先把真实数据里要覆盖的字段跳过；F_PROFILE 保留但 exp 拉满
    out = bytearray()
    for fn, wt, raw, ln, val in fields(real_ud):
        if fn in UNLOCK_LIST_FIELDS or fn in UNLOCK_VALUE_FIELDS:
            continue
        if fn == F_PROFILE and wt == 2:
            # 保留名字/P-ID/生日/粉丝数，只把 exp 拉满（等级）
            out += _ld(fn, patch_scalar_fields(val, {10: MAX_PROFILE_EXP}))
            continue
        out += raw

    # 2) 从静态存档追加解锁列表
    for fn, wt, raw, ln, val in fields(static_ud):
        if fn in UNLOCK_LIST_FIELDS:
            out += raw

    # 3) 从静态存档取数值（体力/钻石），亲密度需要合并到真实的 UserCharacter 上
    real_chars = {}
    for fn, wt, raw, ln, val in fields(real_ud):
        if fn == F_USER_CHARACTER and wt == 2:
            cid = None
            for a, b, _, dd, vv in fields(val):
                if a == 2 and b == 2:
                    cid = vv.decode('utf-8', 'replace')
                    break
            if cid:
                real_chars[cid] = (fn, val)

    static_chars = {}
    for fn, wt, raw, ln, val in fields(static_ud):
        if fn == F_USER_CHARACTER and wt == 2:
            cid = None
            for a, b, _, dd, vv in fields(val):
                if a == 2 and b == 2:
                    cid = vv.decode('utf-8', 'replace')
                    break
            if cid:
                static_chars[cid] = val

    # 对真实的每个角色，把亲密度和粉丝数拉满
    for cid, (fn, val) in real_chars.items():
        out += _ld(fn, patch_scalar_fields(val, {3: FAN_PER_CHARACTER, 7: MAX_DEARNESS}))

    # 体力 / 钻石：从静态存档取
    for fn, wt, raw, ln, val in fields(static_ud):
        if fn in (F_ACTION_POINT, F_GEM) and wt == 2:
            out += raw

    return encrypt_body(_ld(F_USER_DATA, bytes(out)))


class DynamicState:
    """在静态存档基础上叠加「游戏内写操作」的变更。
    这是为了让换装 / 解锁剧情等操作能生效（否则客户端状态不一致 → 报错 1001）。
    """
    def __init__(self, base_blob=None):
        self.base = base_blob
        self._costume_changes = {}    # (charId, setType) -> (costumeId, headId)
        self._unlocked_stories = set()
        self._read_stories = set()
        self._home_chars = {}    # homeType -> (charId, isRandom)
        self._produce_stories = set()   # 已解锁/已读的 p_story-*
        self._tutorials = {}            # tutorialType -> step
        self._idol_card_skins = {}      # idolCardId -> selected idolCardSkinId
        self._produce_decks = {}        # (field, produceType, number) -> serialized user deck
        self._state_file = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                        'dynamic-state.json')
        self._load_state()
        # 培育新手引导（TutorialType 34..76 全是 Produce*）预先标记为已看过，
        # 否则每次进入培育都会把同一批引导弹窗重新弹一遍。
        _added = False
        for _t in range(0, 301):
            if _t not in self._tutorials:
                self._tutorials[_t] = 1
                _added = True
        if _added:
            self._save_state()

    # ---- 持久化：游戏内的换装/解锁重启后不丢 ----
    def _load_state(self):
        if not os.path.isfile(self._state_file):
            return
        try:
            with open(self._state_file, encoding='utf-8') as f:
                d = json.load(f)
            for k, v in (d.get('costume') or {}).items():
                cid, st = k.rsplit('|', 1)
                self._costume_changes[(cid, int(st))] = (v[0], v[1] if len(v) > 1 else None)
            self._unlocked_stories = set(d.get('unlocked') or [])
            self._read_stories = set(d.get('read') or [])
            for k, v in (d.get('home') or {}).items():
                self._home_chars[int(k)] = (v[0], v[1] if len(v) > 1 else 0)
            self._produce_stories = set(d.get('produce') or [])
            for k, v in (d.get('tutorial') or {}).items():
                self._tutorials[int(k)] = int(v)
            self._idol_card_skins = dict(d.get('card_skins') or {})
            self._produce_decks = {tuple(map(int, k.split('|'))): bytes.fromhex(v)
                                   for k, v in (d.get('produce_decks') or {}).items()}
        except Exception:
            pass

    def _save_state(self):
        try:
            d = {
                'costume': {f'{k[0]}|{k[1]}': list(v) for k, v in self._costume_changes.items()},
                'unlocked': sorted(self._unlocked_stories),
                'read': sorted(self._read_stories),
                'home': {str(k): list(v) for k, v in self._home_chars.items()},
                'produce': sorted(self._produce_stories),
                'tutorial': {str(k): v for k, v in self._tutorials.items()},
                'card_skins': dict(self._idol_card_skins),
                'produce_decks': {'|'.join(map(str, k)): v.hex() for k, v in self._produce_decks.items()},
            }
            with open(self._state_file, 'w', encoding='utf-8') as f:
                json.dump(d, f, ensure_ascii=False, indent=1)
        except Exception:
            pass

    def set_base(self, blob):
        self.base = blob

    def apply_costume_set(self, char_id, set_type, costume_id, head_id):
        self._costume_changes[(char_id, set_type)] = (costume_id, head_id)
        self._save_state()

    def apply_idol_card_skin(self, card_id, skin_id):
        self._idol_card_skins[card_id] = skin_id
        self._save_state()

    def apply_produce_decks(self, changes):
        self._produce_decks.update(changes)
        self._save_state()

    def apply_story_unlock(self, story_id):
        self._unlocked_stories.add(story_id)
        self._save_state()

    def apply_produce_story(self, story_id):
        self._produce_stories.add(story_id)
        self._save_state()

    def apply_tutorial(self, ttype, step):
        self._tutorials[int(ttype)] = int(step)
        self._save_state()

    def apply_home_character(self, home_type, char_id, is_random=False):
        self._home_chars[int(home_type)] = (char_id, 1 if is_random else 0)
        self._save_state()

    def apply_story_read(self, story_id):
        self._read_stories.add(story_id)
        self._save_state()


    @property
    def dirty(self):
        return bool(self._costume_changes or self._unlocked_stories or self._read_stories
                    or self._home_chars or self._produce_stories or self._tutorials
                    or self._idol_card_skins or self._produce_decks)

    def render(self):
        """基于 base + 变更 生成新的加密完整帧"""
        if self.base is None:
            return None
        if not self.dirty:
            return self.base
        pt = decrypt_body(self.base)
        if pt is None:
            return self.base
        ud = None
        for fn, wt, raw, ln, val in fields(pt):
            if fn == F_USER_DATA and wt == 2:
                ud = val
                break
        if ud is None:
            return self.base

        out = bytearray()
        seen_costume_keys = set()
        seen_decks = set()
        for fn, wt, raw, ln, val in fields(ud):
            if wt == 2 and fn in (F_SUPPORT_DECK, F_MEMORY_DECK) and self._produce_decks:
                numbers = {f: l for f, w, r, l, v in fields(val) if w == 0 and f in (2, 3)}
                key = (fn, numbers.get(2, 0), numbers.get(3, 0))
                seen_decks.add(key)
                out += _ld(fn, self._produce_decks[key]) if key in self._produce_decks else raw
            elif wt == 2 and fn == F_IDOL_CARD and self._idol_card_skins:
                card_id = next((v.decode('utf-8') for f, w, r, l, v in fields(val)
                                if f == 2 and w == 2), None)
                if card_id in self._idol_card_skins:
                    out += _ld(fn, _replace_idol_card_skin(val, self._idol_card_skins[card_id]))
                else:
                    out += raw
            elif wt == 2 and fn == F_CHARACTER_COSTUME and self._costume_changes:
                cid = st = None
                for a, b, _, dd, vv in fields(val):
                    if a == 2 and b == 2: cid = vv.decode('utf-8', 'replace')
                    elif a == 3 and b == 0: st = dd
                key = (cid, st) if cid is not None and st is not None else None
                if key is not None:
                    seen_costume_keys.add(key)
                if key in self._costume_changes:
                    nc, nh = self._costume_changes[key]
                    e = _ld(2, cid.encode()) + _vi(3, st) + _ld(4, nc.encode())
                    if nh:
                        e += _ld(5, nh.encode())
                    out += _ld(fn, e)
                    continue
                out += raw
            elif wt == 2 and fn == F_TUTORIAL and self._tutorials:
                tt = None
                for a, b, _, dd, vv in fields(val):
                    if a == 2 and b == 0:
                        tt = dd
                        break
                if tt is not None and tt in self._tutorials:
                    out += _ld(fn, _vi(2, tt) + _vi(3, self._tutorials[tt]))
                    continue
                out += raw
            elif wt == 2 and fn == F_HOME_CHARACTER and self._home_chars:
                ht = None
                for a, b, _, dd, vv in fields(val):
                    if a == 2 and b == 0:
                        ht = dd
                        break
                if ht is not None and ht in self._home_chars:
                    cid2, rnd = self._home_chars[ht]
                    e = _vi(2, ht) + _ld(3, cid2.encode())
                    if rnd:
                        e += _vi(4, 1)
                    out += _ld(fn, e)
                    continue
                out += raw
            elif wt == 2 and fn == F_STORY and (self._unlocked_stories or self._read_stories):
                sid = None
                for a, b, _, dd, vv in fields(val):
                    if a == 2 and b == 2:
                        sid = vv.decode('utf-8', 'replace')
                        break
                if sid is not None and (sid in self._unlocked_stories or sid in self._read_stories):
                    e = _ld(2, sid.encode()) + _vi(4, 1) + _vi(6, 1)
                    out += _ld(fn, e)
                    continue
                out += raw
            else:
                out += raw

        for key, entry in self._produce_decks.items():
            if key not in seen_decks:
                out += _ld(key[0], entry)

        # 对不存在于列表里的换装变更，追加新条目
        if self._costume_changes:
            for (cid, st), (nc, nh) in self._costume_changes.items():
                key2 = (cid, st)
                if key2 in seen_costume_keys:
                    continue
                e = _ld(2, cid.encode()) + _vi(3, st) + _ld(4, nc.encode())
                if nh:
                    e += _ld(5, nh.encode())
                out += _ld(F_CHARACTER_COSTUME, e)

        # 对 base 里不存在的首页角色设置，追加新条目
        # （原先只做替换，base 里没有对应 homeType 时用户的换人会丢失）
        if self._home_chars:
            existing_hc = set()
            for fn, wt, raw, ln, val in fields(bytes(out)):
                if fn != F_HOME_CHARACTER or wt != 2:
                    continue
                for a, b, _, dd, vv in fields(val):
                    if a == 2 and b == 0:
                        existing_hc.add(dd)
                        break
            for ht2, (cid_h, rnd_h) in self._home_chars.items():
                if ht2 in existing_hc:
                    continue
                e = _vi(2, ht2) + _ld(3, cid_h.encode())
                if rnd_h:
                    e += _vi(4, 1)
                out += _ld(F_HOME_CHARACTER, e)

        # 新解锁的培育剧情：追加到 userProduceStoryList
        if self._produce_stories:
            existing_ps = set()
            for fn, wt, raw, ln, val in fields(bytes(out)):
                if fn != F_PRODUCE_STORY or wt != 2:
                    continue
                for a, b, _, dd, vv in fields(val):
                    if a == 2 and b == 2:
                        existing_ps.add(vv.decode('utf-8', 'replace'))
                        break
            for sid in self._produce_stories:
                if sid not in existing_ps:
                    out += _ld(F_PRODUCE_STORY, _ld(2, sid.encode()) + _vi(4, 1))

        new_pt = b''.join(_ld(fn, bytes(out)) if fn == F_USER_DATA and wt == 2 else raw
                          for fn, wt, raw, ln, val in fields(pt))
        return encrypt_body(new_pt)


def _read_costume_selection(body):
    plain = decrypt_body(body)
    if plain is None:
        return None
    values = {field: blob.decode('utf-8', 'replace') if kind == 2 else number
              for field, kind, _, number, blob in fields(plain)}
    character, costume = values.get(1), values.get(4)
    if character and costume:
        return character, int(values.get(2, 1)), costume, values.get(3, '') or ''
    return None


class Unlocker:
    def __init__(self, master_dir=None):
        try:
            self.data = load_master(master_dir) if master_dir else {"cards": [], "skins": [],
                                                                     "costumes": [], "heads": [],
                                                                     "music": [], "story": [],
                                                                     "live_music": [],
                                                                     "to_skin": lambda c: None}
        except Exception:
            self.data = {"cards": [], "skins": [], "costumes": [], "heads": [],
                         "music": [], "story": [], "live_music": [],
                         "to_skin": lambda c: None}
        self.enabled = len(self.data.get("costumes") or []) > 0
        self.static_archive = None
        self.state = DynamicState()

    def set_static(self, blob):
        """分发包模式：直接注入预生成的静态存档，不需要 masterTrans"""
        self.static_archive = blob
        self.state.set_base(blob)
        self.enabled = True

    def stats(self):
        d = self.data
        return ("cards=%d skins=%d costumes=%d heads=%d music=%d story=%d live=%d"
                % (len(d["cards"]), len(d["skins"]), len(d["costumes"]),
                   len(d["heads"]), len(d["music"]), len(d["story"]),
                   len(d.get("live_music", []))))
        # support=%d

    def build_static(self, template_body):
        """用一条真实响应做模板，把解锁列表拉满，得到「全解锁静态存档」"""
        pt = decrypt_body(template_body)
        if pt is None:
            return None
        new_pt = merge_unlock(pt, self.data)
        # ★ 必须在这里洗身份：否则模板里的真实名字/评论会被带回来
        try:
            new_pt = apply_config_to_plain(new_pt)
        except Exception:
            pass
        self.static_archive = encrypt_body(new_pt)
        return self.static_archive

    def build_gasha_response(self, body, path=''):
        """本地构造 GashaDrawResponse（抽卡结果）
        请求：f1=gashaId, f2=gashaButtonId, f3=drawCount, f4=consumeType, f6=consumeQty
        响应：f1=GashaDrawResult{f1=RewardResult{f1=type, f2=id, f3=qty, f6=isNew}}, f9999=Response
        """
        pt = decrypt_body(body)
        if pt is None:
            return None
        gasha_id = button_id = ''
        draw_count = 10
        for fn, wt, raw, ln, val in fields(pt):
            if fn == 1 and wt == 2:
                gasha_id = val.decode('utf-8', 'replace')
            elif fn == 2 and wt == 2:
                button_id = val.decode('utf-8', 'replace')
        # ★ 完全忽略 f3：Gasha/DrawSelectPickup 里 f3 不是「抽几张」，
        #   读成 100 会让客户端演出灯光表溢出 -> 序列取消 -> 抽卡卡死。
        #   只按 buttonId 判断，官方 1 连按钮以 -1 结尾，10 连以 -10 结尾。
        _is_pickup = 'DrawSelectPickup' in (path or '')
        # ★ 只有「明确 10 连按钮」（buttonId 以 -10 结尾）才出 10 张。
        #   之前 buttonId 为空时会落到 else，默认 10 张 -> 点一次出十连。
        self._last_gasha = {'gasha': gasha_id, 'button': button_id,
                             'f3': locals().get('draw_count', 0)}
        # 统一按 buttonId 判断（日志实测）：
        #   gasha_button-pickup-001-01        -> 单抽
        #   gasha_button-pickup-001-10        -> 十连
        #   gasha_button-select-pickup-01-discount -> 单抽
        if button_id.endswith('-10'):
            draw_count = 10
        else:
            draw_count = 1

        skins = list(self.data.get("skins") or [])
        if not skins:
            return None

        # 按稀有度加权抽（允许重复，和官方一致）
        # i_card-skin-amao-3-022 -> 第 4 段 3 = SSR
        # 关键：weight = 目标概率 / 该档卡数量，否则皮肤多的档会压倒其他档
        def _rarity_of(sid):
            parts = sid.split('-')
            if len(parts) >= 5:
                try:
                    return int(parts[3])
                except Exception:
                    pass
            return 0

        # 稀有度段（i_card-skin-<chara>-<rarity>-<n>）实测分布：
        #   3 -> 270 张（R，每个偶像约 20 张）
        #   1 ->  25 张（SR）
        #   2 ->  13 张（SSR，每个偶像 1 张）
        # 之前写成 {1:0.80, 2:0.15, 3:0.05} 把 R 和 SR 弄反了，
        # 结果是 80% 出 SR、只有 5% 出 R，和官方差很远。
        _TARGET = {3: 0.80, 1: 0.15, 2: 0.05}     # R / SR / SSR
        _cnt = {}
        for _s in skins:
            _r = _rarity_of(_s)
            _cnt[_r] = _cnt.get(_r, 0) + 1
        _w = {}
        for _r, _n in _cnt.items():
            _p = _TARGET.get(_r, 0.02)
            _w[_r] = _p / max(1, _n)

        _weights = [_w.get(_rarity_of(s), 0.001) for s in skins]
        picked = random.choices(skins, weights=_weights, k=draw_count)

        o = bytearray()
        for _s in picked:
            # RewardResult { f1=ResourceType.IdolCardSkin(5), f2=id, f3=1, f6=isNew }
            # RewardResult 完整：f1=type, f2=id, f3=qty, f4=before, f5=after, f6=isNew
            _is_new = 1 if random.random() < 0.25 else 0
            _rr = (_vi(1, 5) + _ld(2, _s.encode()) + _vi(3, 1)
                   + _vi(4, 0) + _vi(5, 1) + _vi(6, _is_new))
            # GashaDrawResponse.drawResults.f1 = GashaDrawResult { f1 = RewardResult }
            # f1=result, f2=providedResult（官方用它区分「新卡」和「重复转换」）
            o += _ld(1, _ld(1, _rr) + _ld(2, _rr))
        # GashaButton { f1=id, f2=remainingDrawCount, f3=remainingDiscount }
        o += _ld(2, _ld(1, button_id.encode()) + _vi(2, 100) + _vi(3, 0))
        # GashaPoint { f1=id, f2=point, f3=exchangeable }
        _gp_id = gasha_id.replace('gasha-', 'gasha_point-')
        _gp_pt = 20 + draw_count * 10
        o += _ld(4, _ld(1, _gp_id.encode()) + _vi(2, _gp_pt) + _vi(3, 1))
        # commonResponse.updatedData：带余额+卡片列表，让客户端确认抽卡成功
        _ud = _ld(F_GEM, _vi(1, MAX_GEM) + _vi(2, MAX_GEM))
        o += _ld(9999, _ld(1, _ud))
        return encrypt_body(bytes(o))

    def build_profile_response(self, body):
        """本地构造 ProfileGetResponse（个人资料页面）
        字段：f1=publicUserId f2=producerLevel f3=name f4=comment f5=birthday{month,day} f100=totalFanCount
        """
        cfg = load_config()
        prof = cfg.get('profile') or {}
        # 解析请求里的 publicUserId（看客户端要查谁）
        _req_uid = ''
        try:
            _rpt = decrypt_body(body) if body else None
            if _rpt:
                for _f, _w, _, _d, _v in fields(_rpt):
                    if _f == 1 and _w == 2:
                        _req_uid = _v.decode('utf-8', 'replace')
                        break
        except Exception:
            pass
        # 从 User/Get 里取 PLV 和粉丝数
        plv = 90
        fans = 0
        try:
            pt = decrypt_body(self.static_archive or b'')
            if pt:
                ud = None
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == F_USER_DATA and wt == 2:
                        ud = val
                        break
                if ud is not None:
                    for fn, wt, raw, ln, val in fields(ud):
                        if fn == F_PROFILE and wt == 2:
                            for a, b, _, d2, v2 in fields(val):
                                if a == 11 and b == 0:
                                    fans = d2
                                elif a == 10 and b == 0:
                                    # exp → PLV：保守估算，直接用 90
                                    pass
                            break
        except Exception:
            pass

        uid = _req_uid or str(prof.get('user_id', 'GKM00001'))
        name = str(prof.get('name', 'プロデューサー'))
        comment = str(prof.get('comment', ''))
        bm = int(prof.get('birth_month', 1))
        bd = int(prof.get('birth_day', 1))

        o = bytearray()
        o += _ld(1, uid.encode())          # publicUserId
        o += _vi(2, plv)                   # producerLevel
        o += _ld(3, name.encode())         # name
        if comment:
            o += _ld(4, comment.encode())  # comment
        o += _ld(5, _vi(1, bm) + _vi(2, bd))   # birthday{month, day}
        o += _vi(6, 1)                     # friendStatus = None(1)
        o += _vi(7, 1)                     # followable = true
        o += _vi(9, 0)                     # isBlocked = false
        if fans:
            o += _vi(100, fans)            # totalFanCount
        o += _vi(101, 1218)                # achievementCount
        o += _vi(102, 4)                   # mainTaskMainStoryClearCount
        o += _vi(103, 1)                   # mainTaskProducerClearCount
        o += _vi(104, 1)                   # mainTaskProducer2ClearCount
        o += _vi(105, 1)                   # mainTaskProducer3ClearCount
        o += _vi(106, 1)                   # mainTaskProducer4ClearCount
        # 展示用：支援卡 / 回忆 / 名片（从 UserProfile 读）
        _sc_id = _mem_id = ''
        _meishi = 0
        try:
            _pt3 = decrypt_body(self.static_archive or b'')
            if _pt3:
                _ud3 = None
                for _f3, _w3, _, _d3, _v3 in fields(_pt3):
                    if _f3 == F_USER_DATA and _w3 == 2:
                        _ud3 = _v3
                        break
                if _ud3 is not None:
                    for _f4, _w4, _, _d4, _v4 in fields(_ud3):
                        if _f4 == F_PROFILE and _w4 == 2:
                            for _a4, _b4, _, _dd4, _vv4 in fields(_v4):
                                if _a4 == 7 and _b4 == 2: _sc_id = _vv4.decode()
                                elif _a4 == 8 and _b4 == 2: _mem_id = _vv4.decode()
                                elif _a4 == 13 and _b4 == 0: _meishi = _dd4
                            break
        except Exception:
            pass
        # f201 = SupportCard { f1=id, f2=level, f3=levelLimitRank }
        if _sc_id:
            o += _ld(201, _ld(1, _sc_id.encode()) + _vi(2, 60) + _vi(3, 4))
        # f202 = Memory { f1=userMemoryId, ... } 需要 assetId/imagePath，暂不填
        # f204 = Meishi { f1=publicUserId, f2=meishiBase } 需要 meishiBase，暂不填
        # CommonResponse（空 message，proto3 里显式序列化 a 00\）
        o += _ld(9999, b'')

        return encrypt_body(bytes(o))

    def tamper_profile(self, blob):
        """篡改 Profile/Get 响应里的名字/评论/生日（保留 P-ID 等其余字段）

        分发包默认写的是中性值（プロデューサー / 空评论 / 1月1日），
        使用者可在 config.json 的「个人资料」里改成自己的。
        """
        cfg = load_config()
        prof = cfg.get('profile') or {}
        if not prof:
            return blob
        pt = decrypt_body(blob)
        if pt is None:
            return blob
        try:
            return encrypt_body(patch_profile_response(pt, prof))
        except Exception:
            return blob

    def build_preparation_response(self, path):
        """Use the client's NPC rental bucket for local support cards."""
        api = path.rstrip('/')
        common = _ld(9999, _ld(1, b'') + _ld(2, b''))
        if api == '/client.api.SupportCard/ListDeckRanking':
            return encrypt_body(common)
        if api == '/client.api.Produce/ListRentalSupportCard':
            known = set(self.data.get('support_cards') or [])
            # Packages without masterTrans can still use real owned card IDs.
            if self.state.base is not None:
                plain = decrypt_body(self.state.base)
                if plain is not None:
                    user = next((v for f, w, r, l, v in fields(plain) if f == F_USER_DATA and w == 2), b'')
                    known.update(x.decode('utf-8') for f, w, r, l, v in fields(user)
                                 if f == F_SUPPORT_CARD and w == 2
                                 for k, t, rr, ll, x in fields(v) if k == 2 and t == 2)
            result = bytearray()
            for card in sorted(known):
                # The current SSR level table supports level 60 / limit rank 4.
                # Other rarities have different level limits; do not invent them.
                if not card.startswith('s_card-3-'):
                    continue
                npc_id = 'N' + hashlib.sha256(card.encode()).hexdigest()[:7].upper()
                profile = (_ld(1, npc_id.encode()) + _ld(2, '离线支援'.encode('utf-8'))
                           + _vi(3, 1) + _ld(6, '本地 NPC 支援卡'.encode('utf-8')))
                entry = _ld(1, profile) + _ld(2, card.encode()) + _vi(3, 60) + _vi(4, 4)
                result += _ld(3, entry)  # npcSupportCards, NOT real player/friend buckets
            return encrypt_body(bytes(result) + common)
        if api == '/client.api.Produce/ListRentalMemory':
            # UserMemory and common.Memory use different field numbers.
            # Preserve the local memory's actual stats, skills and card data.
            remap = {6: 2, 7: 3, 8: 4, 9: 5, 11: 6, 12: 7, 13: 8, 14: 9,
                     15: 10, 16: 11, 18: 12, 60: 13, 105: 14, 50: 15,
                     22: 20, 23: 21, 26: 22, 30: 30, 31: 31, 32: 32, 33: 33, 36: 34, 37: 35}
            plain = decrypt_body(self.state.base or b'')
            user = next((v for f, w, r, l, v in fields(plain or b'') if f == F_USER_DATA and w == 2), b'')
            result = bytearray()
            seen = set()
            for tag, wt, raw, length, entry in fields(user):
                if tag != F_MEMORY or wt != 2:
                    continue
                parts = list(fields(entry))
                source = next((v.decode('utf-8') for f, w, r, l, v in parts if f == 2 and w == 2), '')
                if not source or source in seen:
                    continue
                seen.add(source)
                # Skip incomplete memories rather than produce unusable entries.
                if not {11, 12, 13, 22}.issubset({f for f, w, r, l, v in parts if w == 2}):
                    continue
                local_id = str(uuid.uuid5(uuid.NAMESPACE_URL, 'gkm-local-memory:' + source))
                memory = bytearray(_ld(1, local_id.encode()))
                for f, w, r, l, v in parts:
                    if f in remap and w in (0, 2):
                        memory += _ld(remap[f], v) if w == 2 else _vi(remap[f], l)
                npc_id = 'M' + hashlib.sha256(source.encode()).hexdigest()[:7].upper()
                profile = (_ld(1, npc_id.encode()) + _ld(2, '离线记忆'.encode('utf-8'))
                           + _vi(3, 1) + _ld(6, '本地记忆副本'.encode('utf-8')))
                rental = _ld(1, profile) + _ld(3, local_id.encode()) + _ld(100, bytes(memory))
                result += _ld(1, rental)
            return encrypt_body(bytes(result) + common)
        if api in ('/client.api.Produce/CheckProgress',
                   '/client.api.Produce/History',
                   '/client.api.Produce/ListLatestHistory'):
            # 进入培育画面必经：没有进行中的培育、没有历史记录。
            # 三个响应都只需要 commonResponse 容器（其余字段为空/默认）。
            return encrypt_body(common)
        return None

    def _static_user_data(self):
        """取 static_userget.bin 里的完整 UserData（干净存档，无 produceProgress）。"""
        arch = getattr(self, 'static_archive', None)
        if not arch:
            return None
        try:
            p = decrypt_body(arch)
            top = list(fields(p))
            if not top or top[0][1] != 2:
                return None
            return top[0][4]
        except Exception:
            return None

    def _build_produce_progress(self, d, status=1, step=1, nav_normal=None,
                                step_type=None, step_id=None):
        """构造 UserProduceProgress。Start / ReadAdv 等响应共用。

        ReadAdvResponse 实现了 IUpdateUserDataHolder：如果 updatedData 是空
        UserData，客户端合并后拿不到进度对象，会每隔几秒重试同一段 ADV。
        """
        prog = bytearray()
        if d.get('group'):
            prog += _ld(2, d['group'].encode())
        if d.get('produce'):
            prog += _ld(3, d['produce'].encode())
        prog += _vi(5, 50)      # producerLevel
        if d.get('char'):
            prog += _ld(6, d['char'].encode())
        if d.get('card'):
            prog += _ld(7, d['card'].encode())
            # 培育场景显示立绘需要皮肤 id：i_card-xxx -> i_card-skin-xxx
            # 不给时 ProduceApiUtility.UpdateProgressImmediate 会空引用。
            _skin = d.get('idol', {}).get('skin') or d['card'].replace('i_card-', 'i_card-skin-', 1)
            prog += _ld(8, _skin.encode())
        if step_type is not None:
            prog += _vi(105, step_type)
        if step_id is not None:
            prog += _ld(106, step_id.encode('utf-8'))
        prog += _vi(9, 0)       # idolCardLevelLimitRank
        prog += _vi(10, 0)      # idolCardPotentialRank
        prog += _vi(101, 0)     # produceScore
        prog += _vi(109, 1)     # stepSelectNumber
        prog += _vi(113, 0)     # continueCount
        # 未设置的 message 字段在 C# 里是 null；客户端 UpdateProgressImmediate
        # 若直接访问会空引用。官服服务端总是下发空 message，这里照做。
        prog += _ld(118, b'')   # resultMemory
        prog += _ld(131, b'')   # resultSelectionMemory
        prog += _vi(100, status)    # ProduceProgressStatus
        prog += _vi(103, step)      # stepNumber
        if nav_normal is not None:
            prog += _vi(114, nav_normal)
        for tag, value in d.get('stats', {200: 207, 201: 192, 202: 267, 220: 31, 221: 31, 230: 80}).items():
            prog += _vi(tag, value)
        for drink_id in d.get('drinks', ()):
            prog += _ld(400, drink_id.encode())
        if d.get('exam_seed'):
            prog += _vi(600, d['exam_seed'])
        if d.get('exam_result_type'):
            prog += _vi(616, d['exam_result_type'])
        prog += _ld(618, d.get('lesson_growth', b''))
        prog += _ld(620, d.get('exam_result', b''))
        return bytes(prog)

    def build_empty_response(self):
        """未实现的接口用：返回合法空 commonResponse（必须含 f9999）。

        纯 5 字节空帧会让客户端把 commonResponse 解析成 null 并报「通信错误」；
        带 f9999 的空 Response 才是可接受的最小成功响应。
        """
        return encrypt_body(_ld(9999, _ld(1, b'') + _ld(2, b'')))

    def build_write_response(self, path, request_body=None):
        """构造写操作的响应：XxxResponse { 9999: Response { 1: UserData { <变更字段> } } }
        关键：客户端靠 updatedData 同步本地状态，空帧会导致静默失败。
        """
        if path.rstrip('/') == '/client.api.Preference/Update':
            d = getattr(self, '_last_preference', None)
            if not d:
                return encrypt_body(_ld(9999, _ld(1, b'') + _ld(2, b'')))
            types, vals = d
            entry = bytearray()
            for t in types:
                entry += _vi(t + 1, int(vals.get(t, 0)))
            ud = _ld(F_USER_PREFERENCE, bytes(entry))
            return encrypt_body(_ld(9999, _ld(1, ud) + _ld(2, b'')))
        if path.rstrip('/') == getattr(self, '_produce_reply_path', None):
            return self._produce_reply
        ud = b''
        if path.rstrip('/') in ('/client.api.Produce/UpdateSupportCardDeck', '/client.api.Produce/UpdateMemoryDeck'):
            for key, entry in getattr(self, '_last_deck_changes', {}).items():
                ud += _ld(key[0], entry)
        elif path.rstrip('/') == '/client.api.IdolCard/ChangeSkin':
            change = getattr(self, '_last_card_skin_change', None)
            if change:
                card_id, skin_id, entry = change
                ud += _ld(F_IDOL_CARD, _replace_idol_card_skin(entry, skin_id))
        elif '/client.api.Costume/Set' in path:
            selection = (_read_costume_selection(request_body) if request_body is not None
                         else getattr(self, '_last_costume_selection', None))
            if selection:
                cid, st, cos, head = selection
                e = _ld(2, cid.encode()) + _vi(3, st) + _ld(4, cos.encode())
                if head:
                    e += _ld(5, head.encode())
                ud += _ld(F_CHARACTER_COSTUME, e)
        elif '/client.api.Story/ReadProduceStory' in path:
            # 培育剧情：必须返回 F_PRODUCE_STORY，不能用 F_STORY
            sid = getattr(self, "last_produce_story", None) or (list(self.state._produce_stories)[-1] if self.state._produce_stories else None)
            if sid and not sid.startswith('p_story'):
                sid = None
            if sid:
                ud += _ld(F_PRODUCE_STORY, _ld(2, sid.encode()) + _vi(4, 1))  # UserProduceStory 只有 f2/f4
        elif '/client.api.Story/ReadDearnessStory' in path:
            _cd = getattr(self, 'last_dearness_read', None)
            if _cd:
                _c2, _l2 = _cd
                ud += _ld(F_DEARNESS_STORY_LEVEL, _ld(2, _c2.encode()) + _vi(3, _l2) + _vi(6, 1))
        elif '/client.api.Story/Read' in path:
            sid = getattr(self, 'last_story_read', None) or (list(self.state._read_stories)[-1] if self.state._read_stories else None)
            # 只接受合法的剧情 id（防止脏数据导致客户端解析失败）
            if sid and not (sid.startswith('story-') or sid.startswith('dearness-story-')):
                sid = None
            if sid:
                ud += _ld(F_STORY, _ld(2, sid.encode()) + _vi(4, 1) + _vi(6, 1))
        elif '/client.api.Story/UnlockProduceStory' in path:
            ps = self.state._produce_stories
            if ps:
                sid = list(ps)[-1]
                ud += _ld(F_PRODUCE_STORY, _ld(2, sid.encode()) + _vi(4, 1))
        elif '/client.api.Story/Unlock' in path:
            us = self.state._unlocked_stories
            if us:
                sid = list(us)[-1]
                ud += _ld(F_STORY, _ld(2, sid.encode()) + _vi(4, 1) + _vi(6, 1))
        elif '/client.api.Tutorial/Progress' in path:
            tt = self.state._tutorials
            if tt:
                for t, sv in tt.items():
                    ud += _ld(F_TUTORIAL, _vi(2, t) + _vi(3, sv))
        elif '/client.api.Home/SetCharacters' in path:
            # 响应用 UserHomeCharacter 格式：f2=homeType f3=charId f4=isRandom
            hc = self.state._home_chars
            for ht, (cid, rnd) in hc.items():
                e = _vi(2, ht) + _ld(3, cid.encode())
                if rnd:
                    e += _vi(4, 1)
                ud += _ld(F_HOME_CHARACTER, e)
        if not ud:
            # 没有 updatedData 时，也要返回「合法的空响应」：
            # XxxResponse { commonResponse(f9999) = Response{} }
            # 纯空帧（5B）会让客户端一直等，必须带 f9999 字段（9B）
            return encrypt_body(_ld(9999, _ld(1, b'') + _ld(2, b'')))
        # 某些 Response 在 f9999 之外还有顶层字段
        _extra = b''
        if '/client.api.Story/ReadDearnessStory' in path:
            _cd2 = getattr(self, 'last_dearness_read', None)
            if _cd2:
                _c3, _l3 = _cd2
                _extra += _ld(1, _c3.encode()) + _vi(2, _l3) + _vi(3, _l3)
        resp = _ld(1, ud) + _ld(2, b'')  # updatedData + allocated empty deletedData
        body = _extra + _ld(9999, resp)     # 顶层字段 + commonResponse
        return encrypt_body(body)

    def handle_write(self, path, body):
        """本地处理写操作。返回 True 表示已处理（不应再转发）。"""
        try:
            deck_api = path.rstrip('/')
            if deck_api.startswith('/client.api.Produce/'):
                import produce_runtime
                method = deck_api.removeprefix(produce_runtime.PREFIX)
                if method in produce_runtime.METHODS:
                    pt = decrypt_body(body) if body else b''
                    if pt is None:
                        raise produce_runtime.ProgressError('Invalid local Produce request')
                    if method == 'Start':
                        card_id = produce_runtime.text(pt, 3)
                        produce_id = produce_runtime.text(pt, 2)
                        group_id = produce_runtime.text(pt, 1)
                        if not (produce_id and card_id and group_id):
                            raise produce_runtime.ProgressError('Missing idol or scenario')
                        client_id = produce_runtime.text(pt, 99)
                        previous = getattr(self, '_produce_start', None)
                        if not (previous and client_id and client_id == previous['client_id']):
                            parts = card_id.split('-')
                            data = dict(group=group_id, produce=produce_id, card=card_id,
                                        char=parts[1] if len(parts) > 2 else '',
                                        puuid=str(uuid.uuid4()), dpuuid=str(uuid.uuid4()), status=1)
                            produce_runtime.start(self, data, pt)
                            self._produce_start = data
                    self._produce_reply_path = None
                    self._produce_reply = produce_runtime.response(self, method, pt)
                    self._produce_reply_path = deck_api
                    return True
            if deck_api == '/client.api.Preference/Update':
                # PreferenceUpdateRequest f1..f4 -> UserPreference f2..f5
                # f999 = PreferenceType 掩码（Packed 或 Unpacked 都可能）
                # 掩码里出现但请求体省略的布尔，表示要清除为 false
                pt = decrypt_body(body)
                if pt is None:
                    return False
                vals, mask = {}, []
                for f, w, r, l, v in fields(pt):
                    if f in (1, 2, 3, 4) and w == 0:
                        vals[f] = l
                    elif f == 999:
                        if w == 0:
                            mask.append(l)
                        elif w == 2:
                            i = 0
                            buf = v
                            while i < len(buf):
                                shift, val = 0, 0
                                while i < len(buf):
                                    b = buf[i]
                                    i += 1
                                    val |= (b & 0x7f) << shift
                                    if not (b & 0x80):
                                        break
                                    shift += 7
                                mask.append(val)
                types = sorted({t for t in mask if t in (1, 2, 3, 4)})
                if not types:
                    types = sorted(vals)
                if not types:
                    return False
                for t in types:
                    vals.setdefault(t, 0)
                self._last_preference = (types, vals)
                return True
            if deck_api in ('/client.api.Produce/UpdateSupportCardDeck', '/client.api.Produce/UpdateMemoryDeck'):
                pt = decrypt_body(body)
                base = decrypt_body(self.state.render() or b'')
                if pt is None or base is None:
                    return False
                user = next(v for f, w, r, l, v in fields(base) if f == F_USER_DATA and w == 2)
                support = deck_api.endswith('/UpdateSupportCardDeck')
                tag = F_SUPPORT_DECK if support else F_MEMORY_DECK
                owned_tag = F_SUPPORT_CARD if support else F_MEMORY
                owned = {x.decode('utf-8') for f, w, r, l, v in fields(user) if f == owned_tag and w == 2
                         for k, t, rr, ll, x in fields(v) if k == 2 and t == 2}
                existing = {}
                for f, w, r, l, v in fields(user):
                    if f == tag and w == 2:
                        nums = {k: ll for k, t, rr, ll, x in fields(v) if k in (2, 3) and t == 0}
                        existing[(nums.get(2, 0), nums.get(3, 0))] = v
                changes = {}
                for f, w, r, l, v in fields(pt):
                    if f != 1 or w != 2:
                        continue
                    nums = {k: ll for k, t, rr, ll, x in fields(v) if k in (1, 2) and t == 0}
                    produce_type, number = nums.get(1, 0), nums.get(2, 0)
                    resources = [x.decode('utf-8') for k, t, rr, ll, x in fields(v) if k == 3 and t == 2]
                    if not (1 <= produce_type <= 20 and 1 <= number <= 20 and len(resources) <= 6):
                        return False
                    if any(ident and ident not in owned for ident in resources):
                        return False
                    old = existing.get((produce_type, number), b'')
                    entry = b''.join(rr for k, t, rr, ll, x in fields(old) if k not in (2, 3, 4, 5))
                    entry += _vi(2, produce_type) + _vi(3, number)
                    entry += b''.join(_ld(4, ident.encode('utf-8')) for ident in resources)
                    entry += b''.join(rr for k, t, rr, ll, x in fields(v) if k == 5 and t == 2)
                    changes[(tag, produce_type, number)] = entry
                self.state.apply_produce_decks(changes)
                self._last_deck_changes = changes
                return True
            if path.rstrip('/') == '/client.api.IdolCard/ChangeSkin':
                # 来自本机 v3.5.0 协议：f1=idolCardSkinId，f2=idolCardId。
                pt = decrypt_body(body)
                if pt is None:
                    return False
                request = {f: v.decode('utf-8') for f, w, r, l, v in fields(pt)
                           if w == 2 and f in (1, 2)}
                skin_id = request.get(1, '')
                card_id = request.get(2, '')
                if not card_id:
                    # 兼容只发卡面的请求；关联使用实际 master 数据，不猜编号。
                    map_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                            'idol-card-skin-map.json')
                    if os.path.isfile(map_path):
                        with open(map_path, encoding='utf-8') as f:
                            card_id = (json.load(f) or {}).get(skin_id, '')
                if not (skin_id.startswith('i_card-skin-') and card_id.startswith('i_card-')):
                    return False
                if skin_id.split('-')[2] != card_id.split('-')[1]:
                    return False
                base = decrypt_body(self.state.base or b'')
                if base is None:
                    return False
                ud = next((v for f, w, r, l, v in fields(base)
                           if f == F_USER_DATA and w == 2), None)
                if ud is None:
                    return False
                entry = None
                owns_skin = False
                for f, w, r, l, v in fields(ud):
                    if w != 2 or f not in (F_IDOL_CARD, F_IDOL_CARD_SKIN):
                        continue
                    ident = next((x.decode('utf-8') for k, t, rr, ll, x in fields(v)
                                  if k == 2 and t == 2), None)
                    if f == F_IDOL_CARD and ident == card_id:
                        entry = v
                    elif f == F_IDOL_CARD_SKIN and ident == skin_id:
                        owns_skin = True
                if entry is None or not owns_skin:
                    return False
                self.state.apply_idol_card_skin(card_id, skin_id)
                self._last_card_skin_change = (card_id, skin_id, entry)
                return True

            if '/client.api.Costume/Set' in path:
                selection = _read_costume_selection(body)
                if selection is None:
                    return False
                self.state.apply_costume_set(*selection)
                self._last_costume_selection = selection
                return True

            if '/client.api.Tutorial/Progress' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                ttype = step = None
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == 1 and wt == 0:
                        ttype = ln
                    elif fn == 2 and wt == 0:
                        step = ln
                if ttype is not None:
                    self.state.apply_tutorial(ttype, step if step is not None else 1)
                    return True
                return False

            if '/client.api.Home/SetCharacters' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                # 注意：请求里的 HomeCharacter 字段号是 f1/f2/f3，
                # 而响应里的 UserHomeCharacter 是 f2/f3/f4，不能混用！
                _n = 0
                for fn, wt, raw, ln, val in fields(pt):
                    if fn != 1 or wt != 2:
                        continue
                    ht = cid = rnd = None
                    for a, b, _, dd, vv in fields(val):
                        if a == 1 and b == 0: ht = dd
                        elif a == 2 and b == 2: cid = vv.decode('utf-8', 'replace')
                        elif a == 3 and b == 0: rnd = dd
                    if ht is not None and cid:
                        self.state.apply_home_character(ht, cid, rnd)
                        _n += 1
                return _n > 0

            if '/client.api.Story/UnlockProduceStory' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == 1 and wt == 2:
                        self.state.apply_produce_story(val.decode('utf-8', 'replace'))
                        return True
                return False

            if '/client.api.Story/Unlock' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == 1 and wt == 2:
                        _sid = val.decode('utf-8', 'replace')
                        self.state.apply_story_unlock(_sid)
                        self.last_story_unlock = _sid
                        return True
                return False

            if '/client.api.Story/ReadProduceStory' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == 1 and wt == 2:
                        _sid = val.decode('utf-8', 'replace')
                        self.state.apply_produce_story(_sid)
                        self.last_produce_story = _sid
                        return True
                return False
            if '/client.api.Story/UnlockDearnessStory' in path:
                return True   # 空成功（该 Response 只有 commonResponse）
            if '/client.api.Story/ReadDearnessStory' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                _cid = _lv = None
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == 1 and wt == 2:
                        _cid = val.decode('utf-8', 'replace')
                    elif fn == 2 and wt == 0:
                        _lv = ln
                if _cid:
                    self.last_dearness_read = (_cid, _lv or 0)
                    return True
                return False
            if '/client.api.Story/Read' in path:
                pt = decrypt_body(body)
                if pt is None:
                    return False
                for fn, wt, raw, ln, val in fields(pt):
                    if fn == 1 and wt == 2:
                        _sid = val.decode('utf-8', 'replace')
                        self.state.apply_story_read(_sid)
                        self.last_story_read = _sid
                        return True
                return False
        except Exception:
            if deck_api.startswith('/client.api.Produce/'):
                import produce_runtime
                if deck_api.removeprefix(produce_runtime.PREFIX) in produce_runtime.METHODS:
                    raise
            return False
        return False

    def inject_home_chars(self, resp_body):
        """在 Home/Enter 响应的 updatedData 里注入 UserHomeCharacter

        原因：换人后客户端退出设置会重新拉 Home/Enter，
        如果响应不带当前配置，客户端就回落到默认角色。
        """
        raw_list = list(getattr(self, '_home_req_raw', None) or [])
        if not raw_list:
            hc = getattr(self.state, '_home_chars', None) or {}
            for ht, (cid, rnd) in hc.items():
                e = _vi(2, ht) + _ld(3, cid.encode())
                if rnd:
                    e += _vi(4, 1)
                raw_list.append(e)
        if not raw_list:
            return resp_body
        pt = decrypt_body(resp_body)
        if pt is None:
            return resp_body
        parts = []
        found = False
        try:
            for fn, wt, raw, ln, val in fields(pt):
                if fn == 9999 and wt == 2:
                    inner = []
                    has_ud = False
                    for a, b, ra, la, va in fields(val):
                        if a == 1 and b == 2:
                            has_ud = True
                            new_ud = va
                            for e in raw_list:
                                new_ud += _ld(F_HOME_CHARACTER, e)
                            inner.append(_ld(1, new_ud))
                        else:
                            inner.append(ra)
                    if not has_ud:
                        ud_new = b''.join(_ld(F_HOME_CHARACTER, e) for e in raw_list)
                        inner.append(_ld(1, ud_new))
                    parts.append(_ld(9999, b''.join(inner)))
                    found = True
                else:
                    parts.append(raw)
        except Exception:
            return resp_body
        if not found:
            return resp_body
        try:
            return encrypt_body(b''.join(parts))
        except Exception:
            return resp_body

    def inject_home_into_userget(self, resp_body):
        """在 User/Get 响应的 UserData(f1) 里注入当前主页角色

        User/Get 的结构是 { f1 = UserData }（不是 f9999）。
        客户端渲染主页角色最终用的就是这里的 UserHomeCharacterList。
        """
        hc = getattr(self.state, '_home_chars', None) or {}
        if not hc:
            return resp_body
        pt = decrypt_body(resp_body)
        if pt is None:
            return resp_body
        parts = []
        found = False
        try:
            for fn, wt, raw, ln, val in fields(pt):
                if fn == 1 and wt == 2:
                    new_ud = val
                    for ht, (cid, rnd) in hc.items():
                        e = _vi(2, ht) + _ld(3, cid.encode())
                        if rnd:
                            e += _vi(4, 1)
                        new_ud += _ld(F_HOME_CHARACTER, e)
                    parts.append(_ld(1, new_ud))
                    found = True
                else:
                    parts.append(raw)
        except Exception:
            return resp_body
        if not found:
            return resp_body
        try:
            return encrypt_body(b''.join(parts))
        except Exception:
            return resp_body

    def inject_costume_into_userget(self, resp_body):
        """在 User/Get 的 UserData 里用 _costume_changes 覆盖对应槽位的衣服

        F_CHARACTER_COSTUME 是「同一个 (charId, setType) 只能有一条」，
        所以必须就地替换，不能追加（否则客户端取哪条不确定）。
        """
        cc = getattr(self.state, '_costume_changes', None) or {}
        if not cc:
            return resp_body
        pt = decrypt_body(resp_body)
        if pt is None:
            return resp_body
        out = bytearray()
        seen = set()
        try:
            for fn, wt, raw, ln, val in fields(pt):
                if fn != 1 or wt != 2:
                    out += raw
                    continue
                ud = bytearray()
                for a, b, ra, la, va in fields(val):
                    if a == F_CHARACTER_COSTUME and b == 2:
                        cid = st = None
                        for x, y, _, z, w in fields(va):
                            if x == 2 and y == 2:
                                cid = w.decode('utf-8', 'replace')
                            elif x == 3 and y == 0:
                                st = z
                        key = (cid, st)
                        if key in cc:
                            cos, head = cc[key]
                            e = _ld(2, cid.encode()) + _vi(3, st) + _ld(4, cos.encode())
                            if head:
                                e += _ld(5, head.encode())
                            ud += _ld(F_CHARACTER_COSTUME, e)
                            seen.add(key)
                        else:
                            ud += ra
                    else:
                        ud += ra
                for (cid, st), (cos, head) in cc.items():
                    if (cid, st) in seen:
                        continue
                    e = _ld(2, cid.encode()) + _vi(3, st) + _ld(4, cos.encode())
                    if head:
                        e += _ld(5, head.encode())
                    ud += _ld(F_CHARACTER_COSTUME, e)
                out += _ld(1, bytes(ud))
        except Exception:
            return resp_body
        try:
            return encrypt_body(bytes(out))
        except Exception:
            return resp_body

    def tamper(self, path, resp_body):
        """响应后处理：只对 User/Get 生效"""
        if "/client.api.User/Get" not in path:
            return resp_body

        # ★ 合并模式：用官服真实数据做基础，只覆盖解锁列表
        # 这样每个玩家看到的是「他自己的名字/P-ID/粉丝数/等级」，内容全解锁
        if self.static_archive is not None and resp_body:
            try:
                merged = merge_with_static(resp_body, self.static_archive)
                # 注入当前主页角色配置（否则返回标题再进会回落默认角色）
                try:
                    _m2 = self.inject_home_into_userget(merged)
                    if _m2 is not merged:
                        merged = _m2
                except Exception:
                    pass
                # 再叠加运行时的换装/剧情变更
                if self.state.dirty:
                    try:
                        mt = decrypt_body(merged)
                        if mt is not None:
                            merged = encrypt_body(mt)
                    except Exception:
                        pass
                return merged
            except Exception:
                pass

        # 兜底：没有静态存档 / 合并失败 → 尝试用 masterTrans 生成
        if not self.enabled:
            return resp_body
        if STATIC_MODE:
            if self.static_archive is None and resp_body:
                try:
                    self.static_archive = self.build_static(resp_body)
                    setattr(self, "_static_logged", True)
                except Exception as _e:
                    setattr(self, "_static_err", str(_e))
            if self.static_archive is not None:
                return self.static_archive
        if not resp_body:
            return resp_body
        pt = decrypt_body(resp_body)
        if pt is None:
            return resp_body
        try:
            new_pt = merge_unlock(pt, self.data)
        except Exception:
            return resp_body
        return encrypt_body(new_pt)
