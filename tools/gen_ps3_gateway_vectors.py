#!/usr/bin/env python3
"""Generate the C# gateway reference vectors (src/ps3/Sanctuary.Ps3.Tests/GatewayVectors.g.cs) from bootstrap/gateway_probe.py.

The C# gateway (src/ps3/Sanctuary.Ps3/Gateway) is a port of the UDP game-server stand-in. This script runs the Python code with a fixed
clock, a fixed "random" confirm code and the FR_* switches of a few scenarios and records

  * every packet builder's bytes (ZoneDetails, player data, AddPc, the world packets, ...),
  * how Login payloads and client log packets are read,
  * whole conversations: the datagrams a client sends, the datagrams the server answers with after each one, and what the
    delayed actions (spawn after loading, the world-ready fallback) send when time passes.

The tests run the C# code on the same inputs and compare byte for byte.

    python tools/gen_ps3_gateway_vectors.py            # rewrite the generated file
    python tools/gen_ps3_gateway_vectors.py --check    # exit 1 if the committed file is out of date (used by CI)

Standard library only.
"""
import argparse
import os
import struct
import sys
import tempfile
from unittest import mock

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "bootstrap"))
sys.path.insert(0, os.path.join(ROOT, "tools"))

# Importing the gateway opens its trace file; keep that out of the working directory.
if not os.environ.get("FR_TRACE_DIR"):
    os.environ["FR_TRACE_DIR"] = tempfile.mkdtemp(prefix="fr-gateway-vectors-")

import gateway_probe as gp  # noqa: E402
from gen_ps3_vectors import array, cs_bool, cs_bytes, cs_double  # noqa: E402
from gen_ps3_web_vectors import cs_str  # noqa: E402

OUT = os.path.join(ROOT, "src", "ps3", "Sanctuary.Ps3.Tests", "GatewayVectors.g.cs")
UNIX_TIME = 1700000000
ENCRYPT_CODE = 0x01020304
FR_KEYS = ("FR_ZONE", "FR_ZONE_TYPE", "FR_SPAWN", "FR_SPAWN_Y", "FR_ADDPC", "FR_WELCOME", "FR_EXTRA", "FR_TIME")


def string(text):
    raw = text.encode("utf-8")
    return struct.pack("<I", len(raw)) + raw


class Scenario:
    """Environment, install state and character for one run of the Python code."""

    def __init__(self, label, env=None, install_zone=None, install_spawn=None, character=None):
        self.label = label
        self.env = dict(env or {})
        self.install_zone = install_zone
        self.install_spawn = install_spawn
        self.character = dict(character or {})

    def __enter__(self):
        self._saved = {key: os.environ.pop(key, None) for key in FR_KEYS}
        os.environ.update(self.env)
        self._state = (gp._install_zone, gp._install_spawn, dict(gp.CHARACTER), gp.ADDPC_MODE)
        gp._install_zone = self.install_zone
        gp._install_spawn = (self.install_spawn[0], self.install_spawn[1], None, "") if self.install_spawn else None
        gp.CHARACTER.clear()
        gp.CHARACTER.update(self.character)
        gp.ADDPC_MODE = os.environ.get("FR_ADDPC", "other").strip().lower()
        self._time = mock.patch("time.time", return_value=UNIX_TIME + 0.5)
        self._time.start()
        return self

    def __exit__(self, *exc):
        self._time.stop()
        gp._install_zone, gp._install_spawn, saved_character, gp.ADDPC_MODE = self._state
        gp.CHARACTER.clear()
        gp.CHARACTER.update(saved_character)
        for key in FR_KEYS:
            os.environ.pop(key, None)
        os.environ.update({key: value for key, value in self._saved.items() if value is not None})

    def cs_fields(self):
        install = self.install_spawn
        return "%s, %s, %s, %s, %s, %s" % (
            array("", ["(%s, %s)" % (cs_str(k), cs_str(v)) for k, v in sorted(self.env.items())], "        "),
            "null" if self.install_zone is None else cs_str(self.install_zone),
            cs_bool(install is not None),
            cs_double(install[0] if install else 0.0),
            cs_double(install[1] if install else 0.0),
            array("", ["(%s, %s)" % (cs_str(k), cs_str(v)) for k, v in sorted(self.character.items())], "        "))


CHARACTER_FULL = {"Model": "2", "Head": "3", "SkinTone": "0", "HairColor": "7", "EyeColor": "9",
                  "FirstName": "Zoë", "LastName": "Dean"}
CHARACTER_JUNK = {"Model": " 4 ", "Head": "abc", "SkinTone": "-2", "HairColor": "", "EyeColor": "x9", "FirstName": "",
                  "LastName": "O'Neil"}

PACKET_SCENARIOS = [
    Scenario("defaults"),
    Scenario("install zone and spawn", install_zone="FabledRealms", install_spawn=(-128.0, -128.0)),
    Scenario("install spawn with FR_SPAWN_Y", {"FR_SPAWN_Y": "123.5"}, "FabledRealms", (64.25, 300.75)),
    Scenario("bad FR_SPAWN_Y", {"FR_SPAWN_Y": "high"}, "FabledRealms", (64.25, 300.75)),
    Scenario("FR_SPAWN wins", {"FR_SPAWN": "10.5; 20 ,30.25"}, "FabledRealms", (1.0, 2.0)),
    Scenario("bad FR_SPAWN", {"FR_SPAWN": "1,2"}, "FabledRealms", (1.0, 2.0)),
    Scenario("FR_ZONE and type", {"FR_ZONE": " Other_Zone ", "FR_ZONE_TYPE": "1"}, "FabledRealms", (1.0, 2.0)),
    Scenario("bad zone type", {"FR_ZONE_TYPE": "x"}),
    Scenario("extra packets", {"FR_EXTRA": "1"}, "FabledRealms", (-128.0, -128.0)),
    Scenario("no welcome, no time", {"FR_WELCOME": "0", "FR_TIME": "0"}),
    Scenario("extra without time", {"FR_EXTRA": "1", "FR_TIME": "0"}),
    Scenario("full character", {}, "FabledRealms", (-128.0, -128.0), CHARACTER_FULL),
    Scenario("odd character values", {}, "FabledRealms", (-128.0, -128.0), CHARACTER_JUNK),
]

LOGIN_PAYLOADS = [
    ("valid", struct.pack("<H", 1) + string("0") + struct.pack("<Q", 1) + string("1.77.389.188.2194.374262") + string("PSN_NAME_14ch")),
    ("big guid", struct.pack("<H", 1) + string("ticket") + struct.pack("<Q", 0xFFFFFFFFFFFFFFFF) + string("v") + string("")),
    ("latin-1 text", struct.pack("<H", 1) + struct.pack("<I", 3) + b"\xe9\xe8\xff" + struct.pack("<Q", 5) + string("v") + string("u")),
    ("truncated before guid", struct.pack("<H", 1) + string("0") + b"\x01\x02\x03"),
    ("truncated unknown", struct.pack("<H", 1) + string("0") + struct.pack("<Q", 1) + string("v")),
    ("string too long", struct.pack("<H", 1) + struct.pack("<I", 4097) + b"x" * 4097),
    ("string past the end", struct.pack("<H", 1) + struct.pack("<I", 50) + b"short"),
    ("only the opcode", struct.pack("<H", 1)),
    ("max length string", struct.pack("<H", 1) + struct.pack("<I", 4096) + b"t" * 4096 + struct.pack("<Q", 2) + string("v") + string("u")),
]

INNER_SAMPLES = [
    (88, struct.pack("<H", 88) + string("en_US")),
    (88, struct.pack("<H", 88) + string("it's")),
    (88, struct.pack("<H", 88) + string('say "hi"\n')),
    (88, struct.pack("<H", 88) + string("both ' and \"")),
    (88, struct.pack("<H", 88) + b"\xff\xff\xff\xff" + b"abc"),
    (88, struct.pack("<H", 88) + struct.pack("<I", 4) + b"\xff\xfe\x41\x42"),
    (88, struct.pack("<H", 88) + b"\x01\x00"),
    (169, struct.pack("<Hi", 169, -18000)),
    (169, struct.pack("<H", 169)),
    (109, struct.pack("<H", 109) + string("ClientZoneTime.log") + string("Zone time: 12.5\ttab")),
    (109, struct.pack("<H", 109) + string("name only")),
    (109, struct.pack("<H", 109) + struct.pack("<I", 4000) + b"abc"),
    (109, struct.pack("<H", 109) + string("a") + struct.pack("<I", 100) + b"short"),
    (10, struct.pack("<H", 10)),
    (13, struct.pack("<H", 13) + b"\x01"),
    (999, struct.pack("<H", 999) + b"zz"),
    (-1, b""),
]


def packet_vectors():
    items = []
    for sc in PACKET_SCENARIOS:
        with sc:
            zone = gp.configured_zone()
            zone_type = gp.configured_zone_type()
            spawn = gp.configured_spawn()
            character = sc.character
            self_name = character.get("FirstName") or "Test"
            fields = (
                cs_str(sc.label), sc.cs_fields(), cs_str(zone), zone_type, ", ".join(cs_double(v) for v in spawn),
                cs_bytes(gp.zone_details()), cs_bytes(gp.player_blob(character)), cs_bytes(gp.send_self_to_client(character)),
                cs_bytes(gp.add_pc(character)),
                cs_bytes(gp.add_pc(character, guid=1, first_name=self_name, last_name=character.get("LastName") or "", offset=(0, 0, 0))),
                array("", [cs_bytes(p) for p in gp.world_packets()], "        "),
                array("", [cs_bytes(p) for p in gp.zone_done_packets()], "        "),
                cs_bytes(gp.game_time_sync()))
        items.append("new PacketScenario(%s, %s, %s, %d, %s, %s, %s, %s, %s, %s, %s, %s, %s)" % (
            fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7], fields[8], fields[9],
            fields[10], fields[11], fields[12]))
    return items


def fixed_packets():
    return [
        "new FixedPackets(%s, %s, %s, %s, %s, %s, %s)" % (
            cs_bytes(gp.tunneled(b"\x01\x02\x03")), cs_bytes(gp.tunneled(b"\x01\x02\x03", reliable=False)),
            cs_bytes(gp.tunneled(b"")), cs_bytes(gp.lobby_definitions_reply()), cs_bytes(gp.list_queues_reply()),
            cs_bytes(gp.profile(1)), cs_bytes(gp.zone_details("Z", 3, 1, 2, "sky", 1, 9))),
    ]


def login_vectors():
    items = []
    for label, payload in LOGIN_PAYLOADS:
        parsed = gp.parse_login(payload)
        if parsed is None:
            items.append("new LoginVector(%s, %s, false, \"\", 0UL, \"\", \"\")" % (cs_str(label), cs_bytes(payload)))
        else:
            items.append("new LoginVector(%s, %s, true, %s, %dUL, %s, %s)" % (
                cs_str(label), cs_bytes(payload), cs_str(parsed[0]), parsed[1], cs_str(parsed[2]), cs_str(parsed[3])))
    return items


def describe_vectors():
    return ["new DescribeVector(%d, %s, %s)" % (op, cs_bytes(inner), cs_str(gp.describe_inner(op, inner)))
            for op, inner in INNER_SAMPLES]


# ------------------------------------------------------------------------------------------ conversations

class FakeSock:
    def __init__(self):
        self.sent = []

    def sendto(self, data, addr):
        self.sent.append((addr[1], bytes(data)))


class FakeTimer:
    pending = []
    clock = 0.0
    counter = 0

    def __init__(self, interval, function, args=None, kwargs=None):
        self.due = FakeTimer.clock + interval
        self.function, self.args = function, tuple(args or ())
        self.daemon = False
        FakeTimer.counter += 1
        self.order = FakeTimer.counter

    def start(self):
        FakeTimer.pending.append(self)


def make_probe():
    probe = gp.GatewayProbe.__new__(gp.GatewayProbe)
    probe.sock = FakeSock()
    probe.host, probe.port = "127.0.0.1", 20260
    probe.sessions, probe.max_raw, probe.client_ready = {}, {}, set()
    probe.count, probe.send_seq, probe.pending = 0, {}, {}
    probe.login_reply = True
    return probe


def connect(version=3, code=0x11223344, max_raw=512, name=b"CGAPI_490"):
    return b"\x00\x01" + struct.pack(">iii", version, code, max_raw) + name + b"\x00"


def reliable(channel, seq, payload):
    return struct.pack(">BBH", 0, 9 + channel, seq) + payload


def login_payload(ticket="0", guid=1):
    return (struct.pack("<H", 1) + string(ticket) + struct.pack("<Q", guid) + string("1.77.389.188.2194.374262")
            + string("PSN_NAME_14ch"))


def tunnel(outer, inner):
    return struct.pack("<HBI", outer, 1, len(inner)) + inner


def inner_op(op, *rest):
    return struct.pack("<H", op) + b"".join(rest)


def group(*parts):
    out = b"\x00\x19"
    for part in parts:
        out += (bytes([len(part)]) if len(part) < 255 else b"\xff" + struct.pack(">H", len(part))) + part
    return out


def multi(*parts):
    out = b"\x00\x03"
    for part in parts:
        out += (bytes([len(part)]) if len(part) < 255 else b"\xff" + struct.pack(">H", len(part))) + part
    return out


def full_session():
    a, b, c = 50001, 50002, 50003
    steps = [
        ("recv", a, connect()),
        ("recv", a, reliable(0, 0, login_payload())),
        ("recv", a, struct.pack(">BBH", 0, 0x11, 0)),
        ("recv", a, struct.pack(">BBH", 0, 0x15, 3)),
        ("recv", a, reliable(0, 1, tunnel(5, inner_op(88, string("en_US"))))),
        ("recv", a, reliable(0, 2, tunnel(6, inner_op(169, struct.pack("<i", -18000))))),
        ("recv", a, reliable(0, 3, tunnel(6, inner_op(102, b"\x01\x00")))),
        ("recv", a, reliable(0, 4, tunnel(6, inner_op(141, b"\x01\x00")))),
        ("recv", a, reliable(0, 5, tunnel(5, inner_op(102, b"\x01\x00")))),
        ("recv", a, reliable(0, 6, tunnel(6, inner_op(52)))),
        ("recv", a, reliable(0, 7, tunnel(6, inner_op(109, string("ClientZoneTime.log"), string("Zone time: 12.5"))))),
        ("recv", a, reliable(0, 8, tunnel(6, inner_op(13)))),
        ("recv", a, reliable(0, 9, tunnel(6, inner_op(10)))),
        ("advance", 0.5),
        ("advance", 0.6),
        ("recv", a, multi(reliable(0, 10, tunnel(6, inner_op(52))), reliable(1, 0, tunnel(6, inner_op(77, b"xyz"))))),
        ("recv", a, reliable(0, 11, group(tunnel(6, inner_op(13)), tunnel(6, inner_op(52))))),
        ("recv", a, reliable(0, 12, group(tunnel(6, inner_op(7, b"j" * 290))))),
        ("recv", a, reliable(0, 13, multi(multi(multi(multi(tunnel(6, inner_op(13)))))))),
        ("recv", a, struct.pack(">BBH", 0, 13, 14) + b"fragment data"),
        ("recv", a, reliable(1, 0, tunnel(6, inner_op(52)))),
        ("recv", a, reliable(3, 5, b"\x00\x00")),
        ("recv", a, b"\x00\x06"),
        ("recv", a, b"\x00\x1c"),
        ("recv", a, b"\x00\x30abc"),
        ("recv", a, b"\xff\xff\xff"),
        ("recv", a, b"\x00"),
        ("recv", a, b"\x00\x09\x00"),
        ("recv", a, b"\x00\x01\x00\x00"),
        ("recv", a, reliable(0, 14, b"\x01\x00\x05")),
        ("recv", a, reliable(0, 15, b"\x09")),
        ("recv", a, reliable(0, 16, struct.pack("<H", 4242) + b"abc")),
        ("recv", a, reliable(0, 17, tunnel(6, b"\x01"))),
        ("recv", a, b"\x00\x05\x00\x00\x00\x00\x00\x06"),
        ("recv", a, reliable(0, 18, tunnel(6, inner_op(10)))),
        ("advance", 2.0),
        ("recv", b, connect(version=2, code=0x55667788, max_raw=300)),
        ("recv", b, reliable(0, 0, login_payload("ticket-b", 77))),
        ("advance", 19.0),
        ("advance", 2.0),
        ("recv", c, connect()),
        ("recv", c, reliable(0, 0, login_payload("ticket-c", 78))),
        ("recv", c, reliable(0, 1, tunnel(6, inner_op(13)))),
        ("advance", 25.0),
        ("recv", a, connect(code=0x01010101)),
        ("recv", a, reliable(0, 19, tunnel(6, inner_op(10)))),
        ("advance", 1.0),
        ("recv", a, b"\x00\x05\x00\x00\x00\x00\x00\x02"),
        ("recv", a, b"\x00\x05\x00"),
    ]
    return steps


def short_session():
    a = 50010
    return [
        ("recv", a, connect()),
        ("recv", a, reliable(0, 0, login_payload("x", 5))),
        ("recv", a, reliable(0, 1, tunnel(6, inner_op(13)))),
        ("recv", a, reliable(0, 2, tunnel(6, inner_op(10)))),
        ("advance", 1.0),
        ("advance", 30.0),
    ]


CONVERSATIONS = [
    (Scenario("full session, extras, other AddPc", {"FR_EXTRA": "1", "FR_ADDPC": "other"}, "FabledRealms", (-128.0, -128.0),
              CHARACTER_FULL), full_session),
    (Scenario("both AddPc, no time, no welcome", {"FR_ADDPC": "both", "FR_TIME": "0", "FR_WELCOME": "0", "FR_ZONE": "Elsewhere"}, None, None,
              CHARACTER_FULL), short_session),
    (Scenario("self AddPc with an empty name", {"FR_ADDPC": "self", "FR_SPAWN": "1.5,2.5,3.5"}, "FabledRealms", (1.0, 2.0),
              CHARACTER_JUNK), short_session),
    (Scenario("no AddPc", {"FR_ADDPC": "none"}, None, None, {}), short_session),
    (Scenario("empty FR_ADDPC sends nothing either", {"FR_ADDPC": ""}, None, None, {}), short_session),
    (Scenario("unknown FR_ADDPC sends nothing either", {"FR_ADDPC": "Everything"}, None, None, {}), short_session),
]


def run_conversation(scenario, make_steps):
    FakeTimer.pending, FakeTimer.clock = [], 0.0
    outputs = []
    steps = make_steps()
    with scenario, \
            mock.patch("os.urandom", lambda n: ENCRYPT_CODE.to_bytes(4, "big")), \
            mock.patch.object(gp.threading, "Timer", FakeTimer):
        probe = make_probe()
        for step in steps:
            probe.sock.sent.clear()
            if step[0] == "recv":
                probe.handle(step[2], ("127.0.0.1", step[1]))
            else:
                FakeTimer.clock += step[1]
                while True:
                    due = sorted((t for t in FakeTimer.pending if t.due <= FakeTimer.clock), key=lambda t: (t.due, t.order))
                    if not due:
                        break
                    FakeTimer.pending.remove(due[0])
                    due[0].function(*due[0].args)
            outputs.append(list(probe.sock.sent))
    return steps, outputs


def conversation_vectors():
    items = []
    for scenario, make_steps in CONVERSATIONS:
        steps, outputs = run_conversation(scenario, make_steps)
        rendered = []
        for step, sent in zip(steps, outputs):
            sent_items = array("", ["(%d, %s)" % (port, cs_bytes(data)) for port, data in sent], "            ")
            if step[0] == "recv":
                rendered.append("new ConversationStep(true, %d, %s, 0.0, %s)" % (step[1], cs_bytes(step[2]), sent_items))
            else:
                rendered.append("new ConversationStep(false, 0, [], %s, %s)" % (cs_double(step[1]), sent_items))
        items.append("new ConversationVector(%s, %s, %s)" % (
            cs_str(scenario.label), scenario.cs_fields(), array("", rendered, "        ")))
    return items


def generate():
    out = []
    add = out.append
    add("// <auto-generated>")
    add("// Written by tools/gen_ps3_gateway_vectors.py from bootstrap/gateway_probe.py. Do not edit; run the script instead.")
    add("// </auto-generated>")
    add("#nullable enable")
    add("")
    add("using System;")
    add("")
    add("namespace Sanctuary.Ps3.Tests;")
    add("")
    add("internal sealed record PacketScenario(")
    add("    string Label,")
    add("    (string Name, string Value)[] Env,")
    add("    string? InstallZone,")
    add("    bool HasInstallSpawn,")
    add("    double InstallSpawnX,")
    add("    double InstallSpawnZ,")
    add("    (string Name, string Value)[] Character,")
    add("    string Zone,")
    add("    int ZoneType,")
    add("    double[] Spawn,")
    add("    byte[] ZoneDetails,")
    add("    byte[] PlayerBlob,")
    add("    byte[] SendSelf,")
    add("    byte[] AddPcOther,")
    add("    byte[] AddPcSelf,")
    add("    byte[][] WorldPackets,")
    add("    byte[][] ZoneDone,")
    add("    byte[] TimeSync);")
    add("")
    add("internal sealed record FixedPackets(byte[] Tunneled, byte[] TunneledUnreliable, byte[] TunneledEmpty, byte[] Lobby, byte[] Queues, byte[] Profile, byte[] ZoneDetailsArgs);")
    add("")
    add("internal sealed record LoginVector(string Label, byte[] Payload, bool Valid, string Ticket, ulong Guid, string Version, string Unknown);")
    add("")
    add("internal sealed record DescribeVector(int Opcode, byte[] Inner, string Text);")
    add("")
    add("internal sealed record ConversationStep(bool Receive, int Port, byte[] Data, double Seconds, (int Port, byte[] Data)[] Sent);")
    add("")
    add("internal sealed record ConversationVector(")
    add("    string Label,")
    add("    (string Name, string Value)[] Env,")
    add("    string? InstallZone,")
    add("    bool HasInstallSpawn,")
    add("    double InstallSpawnX,")
    add("    double InstallSpawnZ,")
    add("    (string Name, string Value)[] Character,")
    add("    ConversationStep[] Steps);")
    add("")
    add("internal static class GatewayVectors")
    add("{")
    add("    public const long UnixTime = %d;" % UNIX_TIME)
    add("")
    add("    public const int EncryptCode = 0x%08x;" % ENCRYPT_CODE)
    add("")
    add("    public static readonly PacketScenario[] Packets = %s;" % array("", packet_vectors()))
    add("")
    add("    public static readonly FixedPackets Fixed = %s;" % fixed_packets()[0])
    add("")
    add("    public static readonly LoginVector[] Logins = %s;" % array("", login_vectors()))
    add("")
    add("    public static readonly DescribeVector[] Descriptions = %s;" % array("", describe_vectors()))
    add("")
    add("    public static readonly ConversationVector[] Conversations = %s;" % array("", conversation_vectors()))
    add("}")
    return "\n".join(out) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="fail if the committed file differs from a fresh run")
    args = parser.parse_args()
    text = generate()
    if args.check:
        current = open(OUT, encoding="utf-8", newline="").read() if os.path.exists(OUT) else ""
        if current.replace("\r\n", "\n") != text:
            print("%s is out of date; run python tools/gen_ps3_gateway_vectors.py" % os.path.relpath(OUT, ROOT))
            return 1
        print("gateway vectors are up to date")
        return 0
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print("wrote %s (%d bytes)" % (os.path.relpath(OUT, ROOT), len(text)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
