#!/usr/bin/env python3
"""Parse the PS3 digest (NPUA30048.soe) into a file table and test the .zs size rule.

Observed (see docs/digest-format.md):
  * the .soe is a zlib stream followed by a 129-byte trailer
  * the decompressed body is a tag/length/value tree; file records carry
    tag 1 name (NUL-terminated), tag 2 compressed length, tag 3 size,
    tag 4 CRC32 and tag 8 a timestamp
  * for every file, compressed length - 129 is the payload length

Usage:
    python tools/parse_digest.py NPUA30048.soe [--csv out.csv] [--find NAME]
"""

import argparse
import csv
import re
import sys
import zlib
from pathlib import Path

TRAILER = 129
RECORD = re.compile(rb"\x01([\x02-\x7f])([\x20-\x7e]{1,100})\x00")


def split_digest(blob):
    d = zlib.decompressobj()
    body = d.decompress(blob)
    if not d.eof:
        raise ValueError("digest zlib stream is incomplete")
    return body, d.unused_data


def parse_entries(body):
    entries = []
    for m in RECORD.finditer(body):
        name = m.group(2)
        if m.group(1)[0] != len(name) + 1:
            continue
        pos, fields = m.end(), {}
        while pos + 2 <= len(body) and body[pos] in (2, 3, 4, 8) and body[pos] not in fields:
            length = body[pos + 1]
            if length > 8:
                break
            fields[body[pos]] = int.from_bytes(body[pos + 2:pos + 2 + length], "big")
            pos += 2 + length
        if {2, 3, 4} <= fields.keys():
            entries.append({
                "name": name.decode("ascii"),
                "compressed_length": fields[2],
                "size": fields[3],
                "crc32": fields[4],
                "timestamp": fields.get(8),
                "payload_length": fields[2] - TRAILER,
            })
    return entries


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("digest", type=Path)
    ap.add_argument("--csv", type=Path, help="write the file table to this CSV")
    ap.add_argument("--find", help="print entries whose name contains this text")
    args = ap.parse_args()

    body, trailer = split_digest(args.digest.read_bytes())
    entries = parse_entries(body)
    print(f"digest body: {len(body)} bytes, trailer: {len(trailer)} bytes "
          f"(first byte 0x{trailer[0]:02x})" if trailer else f"digest body: {len(body)} bytes, no trailer")
    print(f"file entries parsed: {len(entries)}")

    stored = sum(1 for e in entries if e["payload_length"] == e["size"])
    smaller = sum(1 for e in entries if 0 <= e["payload_length"] < e["size"])
    odd = [e for e in entries if e["payload_length"] < 0 or e["payload_length"] > e["size"]]
    print(f"payload == size (stored raw): {stored}; payload < size (compressed): {smaller}; "
          f"violating 'payload <= size': {len(odd)}")
    for e in odd[:10]:
        print("  violates rule:", e)

    if args.find:
        for e in entries:
            if args.find.lower() in e["name"].lower():
                print(f"{e['name']}: size={e['size']} compressed_length={e['compressed_length']} "
                      f"payload={e['payload_length']} crc32=0x{e['crc32']:08X}")
    if args.csv:
        with args.csv.open("w", newline="", encoding="utf-8") as out:
            w = csv.DictWriter(out, fieldnames=list(entries[0]))
            w.writeheader()
            w.writerows(entries)
        print(f"wrote {args.csv}")
    return 0 if not odd else 1


if __name__ == "__main__":
    sys.exit(main())
