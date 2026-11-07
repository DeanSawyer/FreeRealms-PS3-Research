#!/usr/bin/env python3
"""Check the FR_SANCTUARY=1 flow (bootstrap/sanctuary_flow.py) without a PS3.

Runs a client session through the real gateway code (bootstrap/gateway_probe.py, the harness of
tools/gen_ps3_gateway_vectors.py), reassembles every reliable packet and fragment the server sends, and checks:

  * the inner packets arrive in Sanctuary's order, at login, ClientIsReady and ClientFinishedLoading;
  * each packet parses to the end with the layout of Sanctuary's serializer, read independently here;
  * the welcome screen also parses with the PS3 client's own handler layout (docs/protocol/gateway.md);
  * no reply to 141/1, no AddPc, no world-ready fallback; FR_SKIP drops packets by name.

    python tools/check_sanctuary_flow.py

Standard library only. Exit code 1 on the first failure.
"""
import datetime
import os
import struct
import sys
from unittest import mock

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import gen_ps3_gateway_vectors as harness  # noqa: E402  (sets FR_TRACE_DIR and imports gateway_probe)

gp = harness.gp
import sanctuary_flow as sf  # noqa: E402

LOGIN = ["EncounterOverworldCombat", "InitializationParameters", "ZoneDetails", "ClientGameSettings", "ItemDefinitions",
         "SendSelfToClient"]
READY = ["QuickChatSendData", "UpdateStat", "Hitpoints", "Mana", "UpdateActiveWieldType", "GuildCanCreateGuild",
         "ItemClassDefinitions", "ItemCategoryDefinitions", "ClientProfileData", "CustomizationData",
         "MembershipSubscriptionInfo", "EffectTagCompositeEffectsEnable", "ZoneDoneSendingInitialData",
         "DoneSendingPreloadCharacters", "FriendList", "IgnoreList", "PointOfInterestDefinitionReply",
         "AdventurersJournalInfo", "CoinStoreItemList", "InGamePurchaseEnableMarketplace",
         "InGamePurchaseEnablePaymentSources", "InGamePurchaseBundleCategoryGroups", "InGamePurchaseBundleCategories",
         "InGamePurchaseBundleGroups", "AnnouncementDataSend", "LoadWelcomeScreen", "ObjectiveTargetUpdate"]

FAILURES = []


def check(condition, message):
    if not condition:
        FAILURES.append(message)
        print("FAIL:", message)


class Reader:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def take(self, fmt):
        size = struct.calcsize("<" + fmt)
        if self.pos + size > len(self.data):
            raise ValueError("read past the end at %d" % self.pos)
        values = struct.unpack_from("<" + fmt, self.data, self.pos)
        self.pos += size
        return values if len(values) > 1 else values[0]

    def string(self):
        n = self.take("i")
        if n < 0 or self.pos + n > len(self.data):
            raise ValueError("bad string length %d" % n)
        text = self.data[self.pos:self.pos + n].decode("utf-8")
        self.pos += n
        return text

    def done(self):
        return self.pos == len(self.data)


def parse(name, inner):
    """Read one packet with Sanctuary's layout; return a short description, raise on any mismatch."""
    r = Reader(inner)
    op = r.take("H")
    if name == "AdventurersJournalInfo":
        assert (op, r.take("i")) == (209, 1)
        counts = []
        for width in (7, 10, 3, 9):
            n = r.take("i")
            for _ in range(n):
                key = r.take("i")
                rec = r.take("%di" % width)
                assert key == (rec[1] if width == 3 else rec[0]), "dictionary key differs from the record id"
            counts.append(n)
        desc = "regions %d, hubs %d, hub quests %d, stickers %d" % tuple(counts)
    elif name == "UpdateStat":
        assert r.take("H") == 7
        r.take("Q")
        n = r.take("i")
        for _ in range(n):
            _stat_id, kind = r.take("ii")
            r.take("i" if kind == 0 else "f")
            assert kind in (0, 1)
        desc = "%d stats" % n
    elif name == "CustomizationData":
        assert r.take("H") == 65
        rows = [(r.take("i"), r.string(), r.take("i"), r.take("i")) for _ in range(r.take("i"))]
        assert [row[0] for row in rows] == [0, 1, 2, 3, 4, 5, 6, 8]
        desc = "%d customizations" % len(rows)
    elif name == "AnnouncementDataSend":
        assert r.take("B") == 2
        for _ in range(r.take("i")):
            r.take("6i"), r.string(), r.string(), r.take("3i"), r.string()
        desc = "announcements"
    elif name == "LoadWelcomeScreen":
        show = r.take("B")
        pairs = [r.take("ii") for _ in range(r.take("i"))]
        codes = [(r.string(), r.take("3i"), r.string()) for _ in range(r.take("i"))]
        seconds, cash = r.take("ii")
        assert show == 1 and len(pairs) == 3 and len(codes) == 6 and seconds == -1 and cash == 0
        desc = "welcome screen, first login"
    elif name == "PointOfInterestDefinitionReply":
        payload = r.take("i")
        assert payload == 1 and r.take("B") == 0
        desc = "no points of interest"
    elif name == "LobbyGameDefinitions":
        assert r.take("H") == 2
        size = r.take("i")
        assert size == len(inner) - r.pos
        n = r.take("i")
        for _ in range(n):
            key, entry_id = r.take("ii")
            assert key == entry_id
            r.take("3i")
        r.take("4i")
        desc = "%d lobby games" % n
    elif name == "AbilitySetDefinition":
        assert r.take("H") == 5
        r.take("i")
        n = r.take("i")
        assert n == 8 and all(r.take("i") == 0 for _ in range(n))
        desc = "empty toolbar"
    else:
        r.pos = len(inner)     # fixed-size packets: compared byte for byte below
        desc = ""
    if not r.done():
        raise ValueError("%d bytes left over" % (len(inner) - r.pos))
    return desc


FIXED = {
    "EncounterOverworldCombat": "2900840001",
    "ClientGameSettings": "9000" + "04000000" + "07000000" + "0c010000" + "01" + "0000803f",
    "ItemDefinitions": "2300250004000000" + "00000000",
    "QuickChatSendData": "4300010000000000",
    "Hitpoints": "26000100c4090000c4090000",
    "Mana": "26000d006400000064000000" + "00",
    "GuildCanCreateGuild": "8100200000",
    "ItemClassDefinitions": "2c00010000000000",
    "ItemCategoryDefinitions": "2c0002000000000000000000",
    "ClientProfileData": "2c00030000000000",
    "MembershipSubscriptionInfo": "bd000100",
    "EffectTagCompositeEffectsEnable": "23002b0001",
    "ZoneDoneSendingInitialData": "0e00",
    "DoneSendingPreloadCharacters": "26001a0001",
    "FriendList": "4a000100000000",
    "IgnoreList": "70000100000000",
    "CoinStoreItemList": "a5000100" + "00000000" + "00000000",
    "InGamePurchaseEnableMarketplace": "4200180001",
    "InGamePurchaseEnablePaymentSources": "420021000101",
    "InGamePurchaseBundleCategoryGroups": "4200060000000000",
    "InGamePurchaseBundleCategories": "420007000000000000",
    "InGamePurchaseBundleGroups": "4200090000000000",
    "ObjectiveTargetUpdate": "2f000e00",
}


def reassemble(datagrams):
    """Server datagrams -> application payloads (reliable data and reassembled fragments, in send order)."""
    payloads, partial, total = [], b"", None
    for _port, data in datagrams:
        if len(data) < 4 or data[0] != 0:
            continue
        kind = data[1]
        if 9 <= kind <= 12:
            payloads.append(data[4:])
        elif 13 <= kind <= 16:
            body = data[4:]
            if total is None:
                total, body = struct.unpack(">I", body[:4])[0], body[4:]
            partial += body
            if len(partial) >= total:
                payloads.append(partial[:total])
                partial, total = b"", None
    return payloads


def tunneled_inners(payloads):
    out = []
    for payload in payloads:
        if len(payload) >= 7 and struct.unpack_from("<H", payload)[0] == 5:
            size = struct.unpack_from("<I", payload, 3)[0]
            out.append(payload[7:7 + size])
    return out


def session():
    a = 50101
    t, r, i, s = harness.tunnel, harness.reliable, harness.inner_op, harness.string
    return [
        ("recv", a, harness.connect()),
        ("recv", a, r(0, 0, harness.login_payload())),
        ("recv", a, r(0, 1, t(6, i(102, b"\x01\x00")))),
        ("recv", a, r(0, 2, t(6, i(141, b"\x01\x00")))),
        ("recv", a, r(0, 3, t(6, i(52)))),
        ("recv", a, r(0, 4, t(6, i(13)))),
        ("recv", a, r(0, 5, t(6, i(10)))),
        ("advance", 60.0),
        ("recv", a, r(0, 6, t(6, i(109, s("ClientZoneTime.log"), s("Zone time: 3.3"))))),
    ]


def run(env, character=None, today=datetime.date(2026, 11, 6)):
    scenario = harness.Scenario("sanctuary", env, "FabledRealms", (-128.0, -128.0), character or harness.CHARACTER_FULL)
    with mock.patch.object(sf.datetime, "date", wraps=datetime.date) as fake_date:
        fake_date.today.return_value = today
        steps, outputs = harness.run_conversation(scenario, session)
    return [tunneled_inners(reassemble(out)) for out in outputs]


def main():
    out = run({"FR_SANCTUARY": "1"})
    login, lobby, queues, time_sync, ready, finished, later, log = out[1], out[2], out[3], out[4], out[5], out[6], out[7], out[8]

    # Build the expected packets with the same inputs the gateway used.
    with harness.Scenario("expected", {"FR_SANCTUARY": "1"}, "FabledRealms", (-128.0, -128.0), harness.CHARACTER_FULL):
        look = gp.appearance(gp.CHARACTER)
        want_login = sf.login_packets(gp.zone_details(), gp.send_self_to_client(gp.CHARACTER))
        want_ready = sf.client_is_ready_packets(1, gp.CHARACTER, *look)
        x, _y, z, _w = gp.configured_spawn()
        want_finished = sf.finished_loading_packets(1, x, z, gp.PROFILE_ID, datetime.date(2026, 11, 6))

    check([n for n, _ in want_login] == LOGIN, "login packet names")
    check([n for n, _ in want_ready] == READY, "ClientIsReady packet names")
    check(login == [p for _, p in want_login], "login: the gateway sent %d packets, expected %d in Sanctuary's order"
          % (len(login), len(want_login)))
    check(ready == [p for _, p in want_ready], "ClientIsReady: sent %d packets, expected %d in Sanctuary's order"
          % (len(ready), len(want_ready)))
    check(finished == [p for _, p in want_finished], "FinishedLoading: sent %d packets, expected %d"
          % (len(finished), len(want_finished)))
    check(len(lobby) == 1 and lobby[0] == sf.lobby_definitions_reply(), "lobby request answered with Sanctuary's reply")
    check(queues == [], "no reply to 141/1")
    check(len(time_sync) == 1 and time_sync[0][:2] == b"\x34\x00" and len(time_sync[0]) == 15, "GameTimeSync reply")
    check(later == [] and log == [], "nothing after FinishedLoading: no AddPc, no fallback")

    # Layouts, read independently from Sanctuary's serializers.
    for name, inner in want_login + want_ready + want_finished + [("LobbyGameDefinitions", sf.lobby_definitions_reply())]:
        if name in ("ZoneDetails", "SendSelfToClient"):
            continue      # the PS3 layouts, checked by tools/gen_ps3_gateway_vectors.py and the C# tests
        try:
            desc = parse(name, inner)
        except (AssertionError, ValueError, struct.error) as exc:
            check(False, "%s does not parse: %s" % (name, exc))
            continue
        key = name.split(" ")[0]
        if key in FIXED:
            check(inner.hex() == FIXED[key], "%s bytes %s, expected %s" % (name, inner.hex(), FIXED[key]))
        print("ok  %-36s %4d bytes  opcode %3d%s  %s" % (name, len(inner), struct.unpack_from("<H", inner)[0],
                                                       sf.opcode_note(inner) and " (out of range)", desc))

    # Seasonal areas: Halloween on 6 November, not on 1 December.
    nov = [n for n, _ in sf.finished_loading_packets(1, x, z, 1, datetime.date(2026, 11, 6)) if n.startswith("SetClientArea")]
    dec = [n for n, _ in sf.finished_loading_packets(1, x, z, 1, datetime.date(2026, 12, 1)) if n.startswith("SetClientArea")]
    check(len(nov) == 14 and len(dec) == 1, "client areas: %d in November, %d in December" % (len(nov), len(dec)))
    check(sf.nearest_area(-1414.636, 351.567)[0] == 5, "the Sanctuary POI lies in the Sanctuary area")
    check(sf.nearest_area(51.0, 363.0)[0] == 8, "next to Snowhill the nearest area is Snowhill")

    # Game clock as Sanctuary computes it.
    check(sf.game_time_sync(1221361670 + 10) == struct.pack("<HqiB", 52, 80, 8, 0), "game clock")

    # FR_SKIP drops by name, and every SetClientArea by its first word.
    skipped = run({"FR_SANCTUARY": "1", "FR_SKIP": "AdventurersJournalInfo, setclientarea"})
    check(len(skipped[5]) == len(READY) - 1 and sf.adventurers_journal() not in skipped[5], "FR_SKIP drops the journal")
    check(all(p[:2] != b"\x59\x00" for p in skipped[6]), "FR_SKIP drops the client areas")

    # Without the switch the old flow is unchanged (it still answers 141/1).
    old = run({})
    check(len(old[3]) == 1 and old[3][0][:2] == b"\x8d\x00", "without FR_SANCTUARY the 141/1 reply is still sent")

    # The creator's clothes go on a second after FinishedLoading: one EquippedItemsChange (PlayerUpdate sub-opcode 7);
    # FR_EQUIP=5 sends one EquipItem (ClientUpdate sub-opcode 5) per piece instead.
    def piece(model, texture, tint, slot):
        return (harness.string(model) + harness.string(texture) + harness.string("dyetint")
                + struct.pack("<iii", tint, 0, slot))
    pieces = [piece("freestyle_<race>_m_chest_rolledslvshirt.adr", "freestyle-stitchup-L1", 234, 3),
              piece("freestyle_<race>_<gender>_legs_bootcutpants.adr", "freestyle-smallstitch-L1", 262, 4),
              piece("freestyle_<race>_m_feet_shoeswsocks.adr", "freestyle-suede-L1", 234, 5)]
    want_each = [struct.pack("<HHi", 38, 5, n + 1) + p + struct.pack("<ii", 1, 0) for n, p in enumerate(pieces)]
    want_all = struct.pack("<HHQi", 35, 7, 1, 3) + b"".join(pieces) + struct.pack("<i", 0)
    dressed = run({"FR_SANCTUARY": "1"}, harness.CHARACTER_DRESSED)
    check(dressed[7] == [want_all], "dressed character: one EquippedItemsChange after FinishedLoading")
    check(dressed[6] == finished, "dressed character: FinishedLoading packets unchanged")
    check(run({"FR_SANCTUARY": "1", "FR_EQUIP": "5"}, harness.CHARACTER_DRESSED)[7] == want_each,
          "FR_EQUIP=5 sends one EquipItem per piece")
    check(run({"FR_SANCTUARY": "1", "FR_EQUIP": "0"}, harness.CHARACTER_DRESSED)[7] == [], "FR_EQUIP=0 sends no clothes")

    print()
    if FAILURES:
        print("%d check(s) failed" % len(FAILURES))
        return 1
    print("all Sanctuary flow checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
