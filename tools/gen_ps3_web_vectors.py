#!/usr/bin/env python3
"""Generate the C# web-front reference vectors (src/ps3/Sanctuary.Ps3.Tests/WebVectors.g.cs) from bootstrap/fr_ps3_bootstrap.py.

The C# web front (src/ps3/Sanctuary.Ps3/Web) is a port of the HTTP and DNS side of the Python bootstrap. This script runs the
Python code on a fixed set of inputs (ps3ws replies for several option combinations, the AppConfig, DNS queries and answers,
form and query-string parsing, percent-decoding) and writes what it answered; the tests run the C# code on the same inputs and
compare byte for byte.

    python tools/gen_ps3_web_vectors.py            # rewrite the generated file
    python tools/gen_ps3_web_vectors.py --check    # exit 1 if the committed file is out of date (used by CI)

Standard library only.
"""
import argparse
import os
import struct
import sys
import tempfile
from urllib.parse import parse_qsl, unquote

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "bootstrap"))
sys.path.insert(0, os.path.join(ROOT, "tools"))

# Importing the bootstrap opens its trace file; keep that out of the working directory.
if not os.environ.get("FR_TRACE_DIR"):
    os.environ["FR_TRACE_DIR"] = tempfile.mkdtemp(prefix="fr-web-vectors-")

import fr_ps3_bootstrap as boot  # noqa: E402
from gen_ps3_vectors import array, cs_bool, cs_bytes  # noqa: E402

OUT = os.path.join(ROOT, "src", "ps3", "Sanctuary.Ps3.Tests", "WebVectors.g.cs")
ANSWER_IP = bytes([10, 1, 2, 3])


def cs_str(text):
    """A C# string literal; characters outside ASCII are escaped (astral ones as \\UXXXXXXXX)."""
    out = ['"']
    for ch in text:
        code = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif 32 <= code < 127:
            out.append(ch)
        elif code > 0xFFFF:
            out.append("\\U%08x" % code)
        else:
            out.append("\\u%04x" % code)
    out.append('"')
    return "".join(out)


KIND_ORDER = ["server_list", "account_info", "check_name", "character_create", "character_login",
              "character_delete", "friends_list", "generic"]


def reply_vectors():
    cases = [
        ("defaults", dict()),
        ("elements", dict(style="elements")),
        ("account status 1", dict(account_status=1)),
        ("account status 3 elements", dict(style="elements", account_status=3)),
        ("existing character", dict(existing_character=True)),
        ("existing character elements", dict(style="elements", existing_character=True)),
        ("zone field", dict(zone=True)),
        ("other gateway", dict(gateway="192.168.1.20:20261")),
        ("everything", dict(style="elements", account_status=1, existing_character=True, zone=True,
                            gateway="10.0.0.5:1")),
    ]
    items = []
    for label, options in cases:
        replies = boot.build_replies(**options)
        assert set(replies) == set(KIND_ORDER), sorted(replies)
        style = options.get("style", "attributes")
        items.append("new ReplyVector(%s, %s, %d, %s, %s, %s, %s)" % (
            cs_str(label), cs_str(style), options.get("account_status", 2),
            cs_bool(options.get("existing_character", False)), cs_bool(options.get("zone", False)),
            cs_str(options.get("gateway", "127.0.0.1:20260")),
            array("", ["(%s, %s)" % (cs_str(kind), cs_bytes(replies[kind])) for kind in KIND_ORDER], "        ")))
    return items


def query(name, qtype=1, qclass=1, ident=b"\x12\x34", flags=b"\x01\x00", count=1, extra=b""):
    labels = b"".join(bytes([len(part)]) + part for part in name) + b"\x00"
    return ident + flags + struct.pack("!HHHH", count, 0, 0, 0) + labels + struct.pack("!HH", qtype, qclass) + extra


def dns_vectors():
    ps3 = [b"ps3", b"freerealms", b"com"]
    cases = [
        ("A query", query(ps3)),
        ("AAAA query", query(ps3, qtype=28)),
        ("HTTPS query", query(ps3, qtype=65)),
        ("upper case labels", query([b"PS3", b"FreeRealms", b"COM"])),
        ("class CH", query(ps3, qclass=3)),
        ("trailing data", query(ps3, extra=b"\x00\x00\x29\x10\x00")),
        ("other host", query([b"example", b"org"])),
        ("two questions", query(ps3, count=2)),
        ("no question", query(ps3, count=0)),
        ("truncated name", query(ps3)[:20]),
        ("missing type", query(ps3)[:-3]),
        ("label too long", query([b"x" * 64, b"com"])),
        ("label past the end", b"\x12\x34\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00" + b"\x3f" + b"ab" + b"\x00\x01\x00\x01"),
        ("non-ASCII label", query([b"p\xe9s3", b"com"])),
        ("short packet", b"\x12\x34\x01\x00\x00\x01"),
        ("root name", query([])),
    ]
    items = []
    for label, packet in cases:
        try:
            host, qtype, qclass, question = boot.parse_dns_question(packet)
        except ValueError:
            items.append("new DnsVector(%s, %s, false, \"\", 0, 0, [], [])" % (cs_str(label), cs_bytes(packet)))
            continue
        reply, _ = boot.dns_answer(packet, question, qtype, ANSWER_IP)
        items.append("new DnsVector(%s, %s, true, %s, %d, %d, %s, %s)" % (
            cs_str(label), cs_bytes(packet), cs_str(host), qtype, qclass, cs_bytes(question), cs_bytes(reply)))
    return items


DECODE_INPUTS = [
    "", "abc", "%41%42", "%e2%82%ac", "%E2%82%AC", "%E2%82", "%zz", "%4", "a%2", "%c3%28", "caf%C3%A9",
    "100%", "%%41", "x%41y%42z", "é%A9", "%F0%9F%98%80", "\U0001F600%41", "%ff%fe", "%00", "a+b", "%2B",
    "%C3%A9%C3%A9", "%C3", "%C3%", "%C3%A", "%80abc", "%e9", "Ã%A9",
]

FORM_INPUTS = [
    ("", True), ("", False),
    ("Session=", True), ("Session=", False),
    ("Session=abc&Name=Bob+Smith&Head=2&", True),
    ("a&b=&=c&&d=1&d=2", True), ("a&b=&=c&&d=1&d=2", False),
    ("x=%E2%82%AC", True), ("k=v=w", True), ("%41=%42", True), ("a=1;b=2", True),
    ("Name=%FF", True), ("Name=%e2%82", True), ("a=%zz&b=%4", True),
    ("c=123&l=456&cb=", False), ("c=123&l=456&cb=", True), ("c=12x&l=%31%32&cb=aaa", False),
    ("c=²&l=5", False), ("n=\U0001F600&m=%F0%9F%98%80", True), ("+=+&a+b=c+d", True),
    ("Session=s&Name=Ã©&Head=1&Skin=2&Hair=3", True),
    ("a=1&a=2&a=3", True), ("=&=", True), ("&&&", True), ("a=b%26c&d=e%3Df", True),
]


def form_vectors():
    items = []
    for text, keep in FORM_INPUTS:
        pairs = parse_qsl(text, keep_blank_values=keep)
        items.append("new FormVector(%s, %s, %s)" % (
            cs_str(text), cs_bool(keep),
            array("", ["(%s, %s)" % (cs_str(n), cs_str(v)) for n, v in pairs], "        ")))
    return items


def generate():
    out = []
    add = out.append
    add("// <auto-generated>")
    add("// Written by tools/gen_ps3_web_vectors.py from bootstrap/fr_ps3_bootstrap.py. Do not edit; run the script instead.")
    add("// </auto-generated>")
    add("#nullable enable")
    add("")
    add("using System;")
    add("")
    add("namespace Sanctuary.Ps3.Tests;")
    add("")
    add("internal sealed record ReplyVector(string Label, string Style, int AccountStatus, bool ExistingCharacter, bool Zone, string Gateway, (string Kind, byte[] Body)[] Replies);")
    add("")
    add("internal sealed record DnsVector(string Label, byte[] Packet, bool Valid, string Host, int Type, int Class, byte[] Raw, byte[] Reply);")
    add("")
    add("internal sealed record FormVector(string Input, bool KeepBlank, (string Name, string Value)[] Pairs);")
    add("")
    add("internal static class WebVectors")
    add("{")
    add("    /// <summary>The address the DNS replies carry.</summary>")
    add("    public static readonly byte[] AnswerIp = [%s];" % ", ".join(str(b) for b in ANSWER_IP))
    add("")
    add("    public static readonly (string Ip, byte[] Document)[] AppConfigs = [")
    for ip in ("127.0.0.1", "192.168.1.20"):
        add("        (%s, %s)," % (cs_str(ip), cs_bytes(boot.app_config(ip))))
    add("    ];")
    add("")
    add("    public static readonly ReplyVector[] Replies = %s;" % array("", reply_vectors()))
    add("")
    add("    public static readonly DnsVector[] Dns = %s;" % array("", dns_vectors()))
    add("")
    add("    public static readonly (string Input, string Output)[] Decodes = %s;" % array(
        "", ["(%s, %s)" % (cs_str(text), cs_str(unquote(text))) for text in DECODE_INPUTS]))
    add("")
    add("    public static readonly FormVector[] Forms = %s;" % array("", form_vectors()))
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
            print("%s is out of date; run python tools/gen_ps3_web_vectors.py" % os.path.relpath(OUT, ROOT))
            return 1
        print("web vectors are up to date")
        return 0
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print("wrote %s (%d bytes)" % (os.path.relpath(OUT, ROOT), len(text)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
