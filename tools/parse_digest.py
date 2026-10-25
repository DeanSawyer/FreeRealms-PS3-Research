#!/usr/bin/env python3
"""Parse the PS3 digest (NPUA30048.soe) into a file table and test the .zs size rule.

Observed (see docs/install/digest-format.md):
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


def _read_len(data, pos):
    """Length prefix: <0x80 one byte; 0x80-0xFE two bytes (15 bits); 0xFF four bytes."""
    b = data[pos]
    if b < 0x80:
        return b, 1
    if b == 0xFF:
        return int.from_bytes(data[pos + 1:pos + 5], "big"), 5
    return ((b & 0x7F) << 8) | data[pos + 1], 2


def _walk_dir(data, pos, end, prefix, out):
    name = ""
    while pos < end:
        tag = data[pos]
        length, n = _read_len(data, pos + 1)
        value, stop = pos + 1 + n, pos + 1 + n + length
        if tag == 1:  # directory name
            name = data[value:stop].rstrip(b"\x00").decode("latin-1")
        elif tag == 2:  # child directory (its own name tag comes first inside)
            _walk_dir(data, value, stop, prefix + (name + "/" if name else ""), out)
        elif tag == 3:  # file record
            fields, fname, q = {}, "", value
            while q < stop:
                t = data[q]
                l, m = _read_len(data, q + 1)
                raw = data[q + 1 + m:q + 1 + m + l]
                q += 1 + m + l
                if t == 1:
                    fname = raw.rstrip(b"\x00").decode("latin-1")
                else:
                    fields[t] = int.from_bytes(raw, "big")
            if {2, 3, 4} <= fields.keys():
                out.append({
                    "path": prefix + (name + "/" if name else "") + fname,
                    "name": fname,
                    "compressed_length": fields[2],
                    "size": fields[3],
                    "crc32": fields[4],
                    "timestamp": fields.get(8),
                    "payload_length": fields[2] - TRAILER,
                })
        # tag 0xFE (child counts) and unknown tags are skipped
        pos = stop


def parse_tree(body):
    """Full directory-aware parse. Returns file entries that include their 'path'."""
    pos = 0
    while pos < len(body) and body[pos] != 2:  # header fields precede the root directory
        length, n = _read_len(body, pos + 1)
        pos += 1 + n + length
    length, n = _read_len(body, pos + 1)
    out = []
    _walk_dir(body, pos + 1 + n, pos + 1 + n + length, "", out)
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("digest", type=Path)
    ap.add_argument("--csv", type=Path, help="write the file table to this CSV")
    ap.add_argument("--find", help="print entries whose name contains this text")
    args = ap.parse_args()

    body, trailer = split_digest(args.digest.read_bytes())
    entries = parse_tree(body)
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
            if args.find.lower() in e["path"].lower():
                print(f"{e['path']}: size={e['size']} compressed_length={e['compressed_length']} "
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
