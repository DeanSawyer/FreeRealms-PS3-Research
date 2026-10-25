#!/usr/bin/env python3
"""Compare candidate encodings of a known asset against the manifest length.

Needs no emulator run. The manifest says terraincalendardefinitions.txt
(403 bytes, CRC32 0x912D54DE) is delivered as 373 bytes. A candidate whose
output length equals that value is worth testing first; one that is far
smaller (e.g. LZMA at ~251 bytes) is unlikely to be the real format.

Usage:
    python tools/codec_sizes.py path/to/terraincalendardefinitions.txt [--expected 373]
"""

import argparse
import sys
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "bootstrap"))
import codec_candidates  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source", type=Path, help="decoded asset (your own copy)")
    ap.add_argument("--expected", type=int, default=373,
                    help="manifest-indicated compressed length (default 373)")
    ap.add_argument("--crc", type=lambda v: int(v, 0), default=0x912D54DE,
                    help="expected CRC32 of the decoded asset")
    args = ap.parse_args()

    raw = args.source.read_bytes()
    crc = zlib.crc32(raw)
    print(f"source: {len(raw)} bytes, CRC32 0x{crc:08X} "
          f"({'matches' if crc == args.crc else 'DOES NOT match'} expected 0x{args.crc:08X})")
    print(f"manifest length: {args.expected} bytes\n")

    rows = []
    for name in codec_candidates.codec_names():
        out = codec_candidates.encode(name, raw)
        rows.append((abs(len(out) - args.expected), name, len(out)))
    rows.sort()
    print(f"{'candidate':<32}{'bytes':>7}{'delta':>8}")
    for dist, name, size in rows:
        mark = "  <== exact" if dist == 0 else ""
        print(f"{name:<32}{size:>7}{size - args.expected:>+8}{mark}")
    if not rows or rows[0][0] != 0:
        print("\nNo candidate reproduces the manifest length. The real codec is "
              "probably something else (custom LZ/Huffman, block framing, or "
              "different input bytes). Use tools/analyze_digest.py next.")


if __name__ == "__main__":
    main()
