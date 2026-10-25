#!/usr/bin/env python3
"""List printable strings in a binary (for example a decrypted game.elf) that match a pattern,
plus the strings stored right next to each match.

Related strings (a URL, the XML tag names it is parsed with, the messages shown on failure) are
usually stored close together, so the neighbours show what a client expects. Read-only.

Usage:
    python tools/elf_strings.py game.elf "serverList|ps3ws" [--context 600] [--min-len 3] [--max-lines 150]
"""

import argparse
import re
import sys
from pathlib import Path


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file", type=Path)
    ap.add_argument("pattern", help="regular expression (case-insensitive) matched against each string")
    ap.add_argument("--context", type=int, default=600, help="bytes before/after a match to include")
    ap.add_argument("--min-len", type=int, default=3)
    ap.add_argument("--max-lines", type=int, default=150, help="stop after printing this many lines")
    args = ap.parse_args()

    data = args.file.read_bytes()
    runs = [(m.start(), m.group().decode("ascii"))
            for m in re.finditer(rb"[\x20-\x7e\r\n\t]{%d,}" % args.min_len, data)]
    rx = re.compile(args.pattern, re.I)
    printed, shown_until = 0, -1
    for index, (offset, text) in enumerate(runs):
        if not rx.search(text) or offset <= shown_until:
            continue
        print(f"--- match at 0x{offset:x} ---")
        lo = index
        while lo > 0 and offset - runs[lo - 1][0] <= args.context:
            lo -= 1
        hi = index
        while hi + 1 < len(runs) and runs[hi + 1][0] - offset <= args.context:
            hi += 1
        for off, s in runs[lo:hi + 1]:
            flag = ">>" if off == offset else "  "
            print(f"{flag} 0x{off:08x}  {s.strip()[:200]!r}")
            printed += 1
            if printed >= args.max_lines:
                print("... output limit reached; raise --max-lines or narrow the pattern")
                return 0
        shown_until = runs[hi][0]
    if printed == 0:
        print("no matching strings")
    return 0


if __name__ == "__main__":
    sys.exit(main())
