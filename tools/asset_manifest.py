#!/usr/bin/env python3
"""Read and query assets_ps3_manifest.bin, the client's list of every asset it may load from the packs.

The format and hash live in bootstrap/assetlib.py; everything was read out of game.elf (docs/re/assets.md):

* The manifest is an array of 16-byte records, sorted by the first field (binary search at 0x7ff838 / 0x803120).
      u64  name hash          CRC-64 of the UPPER-CASED asset name (function 0x1fac0, table at 0xdb1f10)
      u32  crc32              compared with the pack's own crc by the "is the local copy current" check (0x801020)
      u32  size | flag<<31    size of the asset; the top bit means "stored compressed (.z)" for downloads
* The CRC-64 is the reflected form of polynomial 0x95AC9329AC4BC9B5, initial value and final xor all ones
  (the 256-entry table in the executable is exactly this polynomial's table).
* The records are byte-swapped by the client when it runs on the PS3 (0x8035c0), so the file may be either byte order;
  this script detects which one by checking that the hashes come out sorted.

Usage:
    python tools/asset_manifest.py info   assets_ps3_manifest.bin
    python tools/asset_manifest.py check  assets_ps3_manifest.bin FabledRealms.cob sky_wilds.xml
    python tools/asset_manifest.py scan   assets_ps3_manifest.bin --elf game.elf --suffix .cob
    python tools/asset_manifest.py verify assets_ps3_manifest.bin pack_index_all.txt   # from tools/list_packs.py
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bootstrap"))
from assetlib import Manifest, name_hash  # noqa: E402


def cmd_info(m, args):
    print("records:        %d" % m.count)
    print("byte order:     %s (hashes sorted: %.4f)" % ("big" if m.endian == ">" else "little", m.sorted_fraction))
    comp = sum(1 for h, (c, s, z) in m.by_hash.items() if z)
    total = sum(s for h, (c, s, z) in m.by_hash.items())
    print("compressed:     %d records flagged" % comp)
    print("sum of sizes:   %d bytes" % total)
    dup = m.count - len(m.by_hash)
    print("duplicate keys: %d" % dup)


def cmd_check(m, args):
    for name in args.names:
        rec = m.find(name)
        h = name_hash(name)
        if rec:
            print("FOUND    %-48s hash=%016x crc32=%08x size=%d%s" % (name, h, rec[0], rec[1], " (z)" if rec[2] else ""))
        else:
            print("missing  %-48s hash=%016x" % (name, h))


def strings_from_elf(path, lo=3, hi=48):
    data = open(path, "rb").read()
    out = set()
    for mt in re.finditer(rb"[A-Za-z0-9_\-\. ]{%d,%d}" % (lo, hi), data):
        out.add(mt.group().decode("ascii"))
    return out


def cmd_scan(m, args):
    words = set()
    if args.elf:
        words |= strings_from_elf(args.elf)
    if args.wordlist:
        with open(args.wordlist, encoding="utf-8", errors="replace") as f:
            words |= {ln.split("\t")[0].strip() for ln in f if ln.strip()}
    suffixes = args.suffix or [""]
    hits = []
    for w in sorted(words):
        for s in suffixes:
            name = w + s
            rec = m.find(name)
            if rec:
                hits.append((name, rec))
    for name, rec in hits:
        print("%-56s crc32=%08x size=%d%s" % (name, rec[0], rec[1], " (z)" if rec[2] else ""))
    print("\n%d candidate words x %d suffix(es) tested, %d found" % (len(words), len(suffixes), len(hits)))


def cmd_verify(m, args):
    ok = bad = 0
    missing = []
    with open(args.names, encoding="utf-8", errors="replace") as f:
        for ln in f:
            name = ln.split("\t")[0].strip()
            if not name:
                continue
            if m.find(name):
                ok += 1
            else:
                bad += 1
                if len(missing) < 20:
                    missing.append(name)
    total = ok + bad
    print("pack names found in the manifest: %d of %d (%.2f%%)" % (ok, total, 100.0 * ok / max(1, total)))
    for n in missing:
        print("  not found:", n)
    if total and ok / total < 0.9:
        print("Less than 90%% matched: the hash or the name form used here does not match the client's.")
        return 1
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("info")
    p.add_argument("manifest")
    p.set_defaults(fn=cmd_info)
    p = sub.add_parser("check")
    p.add_argument("manifest")
    p.add_argument("names", nargs="+")
    p.set_defaults(fn=cmd_check)
    p = sub.add_parser("scan")
    p.add_argument("manifest")
    p.add_argument("--elf", help="test every string found in game.elf")
    p.add_argument("--wordlist", help="test every line of this file")
    p.add_argument("--suffix", action="append", help="append this to each word (repeatable), e.g. .cob")
    p.set_defaults(fn=cmd_scan)
    p = sub.add_parser("verify")
    p.add_argument("manifest")
    p.add_argument("names", help="a file whose first tab-separated column is an asset name (list_packs.py *_all.txt)")
    p.set_defaults(fn=cmd_verify)
    args = ap.parse_args()
    try:
        m = Manifest(args.manifest)
    except ValueError as err:
        raise SystemExit(str(err))
    return args.fn(m, args) or 0


if __name__ == "__main__":
    sys.exit(main())
