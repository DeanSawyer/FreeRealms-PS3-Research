#!/usr/bin/env python3
"""Exploratory analysis of the PS3 digest (NPUA30048.soe).

The digest format is not documented, so this makes no format assumptions. It
reports structure clues: entropy, magic bytes, whether the whole file or a
tail decompresses, and every place a known asset's name, sizes and CRC appear
(with hex context) so the record layout can be read off by eye.

Usage:
    python tools/analyze_digest.py USRDIR/Digests/NPUA30048.soe
Defaults look for terraincalendardefinitions.txt (403 bytes, 373 compressed,
CRC32 0x912D54DE).
"""

import argparse
import bz2
import lzma
import math
import struct
import zlib
from collections import Counter
from pathlib import Path


def entropy(buf):
    if not buf:
        return 0.0
    n = len(buf)
    return -sum(c / n * math.log2(c / n) for c in Counter(buf).values())


def hexdump(buf, base=0, width=16):
    lines = []
    for i in range(0, len(buf), width):
        chunk = buf[i:i + width]
        hexa = " ".join(f"{b:02x}" for b in chunk)
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        lines.append(f"  {base + i:08x}  {hexa:<{width * 3}} {text}")
    return "\n".join(lines)


def find_all(data, needle):
    pos, hits = 0, []
    while (pos := data.find(needle, pos)) != -1:
        hits.append(pos)
        pos += 1
    return hits


def int_needles(value):
    needles = {
        f"u32le {value}": struct.pack("<I", value),
        f"u32be {value}": struct.pack(">I", value),
    }
    if value < 65536:
        needles[f"u16le {value}"] = struct.pack("<H", value)
        needles[f"u16be {value}"] = struct.pack(">H", value)
    return needles


def try_decompress(data):
    out = []
    for offset in (0, 4, 8, 12, 16, 32):
        chunk = data[offset:]
        for name, fn in (
            ("zlib", lambda b: zlib.decompress(b)),
            ("raw-deflate", lambda b: zlib.decompress(b, -15)),
            ("gzip", lambda b: zlib.decompress(b, 31)),
            ("bz2", lambda b: bz2.decompress(b)),
            ("lzma-alone", lambda b: lzma.decompress(b, format=lzma.FORMAT_ALONE)),
            ("xz", lambda b: lzma.decompress(b, format=lzma.FORMAT_XZ)),
        ):
            try:
                result = fn(chunk)
            except Exception:
                continue
            out.append((offset, name, len(result)))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("digest", type=Path)
    ap.add_argument("--name", default="terraincalendardefinitions",
                    help="asset name fragment to search for")
    ap.add_argument("--sizes", type=int, nargs="*", default=[403, 373],
                    help="integers to locate (uncompressed, compressed lengths)")
    ap.add_argument("--crc", type=lambda v: int(v, 0), default=0x912D54DE)
    ap.add_argument("--context", type=int, default=48, help="bytes around each hit")
    args = ap.parse_args()

    data = args.digest.read_bytes()
    print(f"file: {args.digest}  ({len(data)} bytes, "
          f"CRC32 0x{zlib.crc32(data):08X}, entropy {entropy(data):.3f} bits/byte)")
    print("\nfirst 96 bytes:")
    print(hexdump(data[:96]))

    print("\nentropy by 1 KiB block (low = structured/text, ~8 = compressed/encrypted):")
    print("  " + " ".join(f"{entropy(data[i:i + 1024]):.1f}"
                          for i in range(0, len(data), 1024)))

    print("\nwhole-file / offset decompression attempts:")
    attempts = try_decompress(data)
    for offset, name, size in attempts:
        print(f"  offset {offset}: {name} -> {size} bytes")
    if not attempts:
        print("  none succeeded (digest is not a single standard-compressed stream at "
              "these offsets)")

    ascii_hits = find_all(data.lower(), args.name.lower().encode())
    wide_hits = find_all(data.lower(), args.name.lower().encode("utf-16-le"))
    print(f"\nname '{args.name}': {len(ascii_hits)} ASCII hit(s), "
          f"{len(wide_hits)} UTF-16LE hit(s)")
    if not (ascii_hits or wide_hits):
        print("  Name not stored in clear text. Possible reasons: hashed names, a "
              "string table with prefix compression, or an encrypted/compressed "
              "digest body.")
    for label, hits in (("ascii", ascii_hits), ("utf16le", wide_hits)):
        for pos in hits[:5]:
            lo = max(0, pos - args.context)
            print(f"\n  [{label}] offset 0x{pos:x}")
            print(hexdump(data[lo:pos + args.context + len(args.name)], lo))

    targets = {}
    for value in args.sizes:
        targets.update(int_needles(value))
    targets.update(int_needles(args.crc))
    print("\ninteger / CRC hits (only hits near the name or repeated hits matter; "
          "small values collide often):")
    for label, needle in targets.items():
        hits = find_all(data, needle)
        if not hits:
            continue
        shown = ", ".join(f"0x{h:x}" for h in hits[:8])
        more = f" (+{len(hits) - 8} more)" if len(hits) > 8 else ""
        print(f"  {label:<14} {len(hits):>4} hit(s): {shown}{more}")
        if len(needle) == 4 and len(hits) <= 3:
            for pos in hits:
                lo = max(0, pos - args.context)
                print(hexdump(data[lo:pos + args.context], lo))

    # Cheap record-length guess: distances between successive path-like strings.
    marks = find_all(data, b".txt")
    if len(marks) > 4:
        gaps = Counter(b - a for a, b in zip(marks, marks[1:]))
        print("\nmost common gaps between '.txt' occurrences (fixed-size records show "
              "one dominant gap):")
        for gap, count in gaps.most_common(5):
            print(f"  {gap:>5} bytes x{count}")


if __name__ == "__main__":
    main()
