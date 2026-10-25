#!/usr/bin/env python3
"""List the asset names stored in the client's assets_ps3_NNN.pack files (names only, no content).

The client finds zone terrain, models and textures by name inside these packs. The index layout is documented in
bootstrap/assetlib.py (read from game.elf). If a pack does not parse, the first 512 bytes of the first failing
pack are written as hex so the real layout can be read from it. Packs are only read, never modified, and no asset
content is written.

Usage:
    python tools/list_packs.py <USRDIR folder or a single .pack> [output.txt]
    python tools/list_packs.py <USRDIR> out.txt --find fabled
"""
import argparse
import collections
import glob
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bootstrap"))
import assetlib  # noqa: E402

INTERESTING = re.compile(r"\.gzne|\.cob$|\.odd$|terrain|zone|fabled|wilds|crossroads|tile|ground|world|geometry|"
                         r"\.rtt|\.tre|town|station", re.I)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("path", help="folder containing assets_ps3_NNN.pack (the USRDIR) or one .pack file")
    ap.add_argument("out", nargs="?", default="pack_index.txt", help="summary file to write")
    ap.add_argument("--find", help="also list every name containing this text (case-insensitive)")
    args = ap.parse_args()

    if os.path.isdir(args.path):
        packs = sorted(glob.glob(os.path.join(args.path, "*.pack")))
        if not packs:
            packs = sorted(glob.glob(os.path.join(args.path, "**", "*.pack"), recursive=True))
    else:
        packs = [args.path]
    if not packs:
        print("No .pack files found in", args.path)
        return 1

    all_entries, per_pack, failed = [], [], []
    for pk in packs:
        entries = assetlib.read_pack_index(pk)
        if entries is None:
            failed.append(pk)
            continue
        per_pack.append((os.path.basename(pk), len(entries)))
        all_entries.extend((os.path.basename(pk), e) for e in entries)

    if failed:
        head_path = os.path.splitext(args.out)[0] + "_head_hex.txt"
        with open(failed[0], "rb") as f:
            head = f.read(512)
        with open(head_path, "w") as o:
            o.write("file: %s\nsize: %d\nunreadable packs: %d of %d\n" % (
                os.path.basename(failed[0]), os.path.getsize(failed[0]), len(failed), len(packs)))
            for i in range(0, len(head), 16):
                chunk = head[i:i + 16]
                o.write("%04x  %s  %s\n" % (i, chunk.hex(" "), "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)))
        print("%d of %d packs do not use the layout this script knows (first: %s)." % (
            len(failed), len(packs), os.path.basename(failed[0])))
        print("Wrote its first 512 bytes to", head_path, "- please attach that file.")
        if not per_pack:
            return 2

    exts = collections.Counter(os.path.splitext(e[0])[1].lower() or "(none)" for _, e in all_entries)
    base = os.path.splitext(args.out)[0]
    with open(base + "_all.txt", "w", encoding="utf-8") as f:
        for pk, (name, off, length, crc) in all_entries:
            f.write("%s\t%s\t%d\n" % (name, pk, length))

    interesting = sorted({e[0] for _, e in all_entries if INTERESTING.search(e[0])})
    with open(args.out, "w", encoding="utf-8") as f:
        f.write("packs: %d   assets: %d\n\n" % (len(per_pack), len(all_entries)))
        for n, c in per_pack:
            f.write("%-24s %d assets\n" % (n, c))
        f.write("\nassets by extension:\n")
        for ext, c in exts.most_common(40):
            f.write("  %-12s %d\n" % (ext, c))
        f.write("\nnames that look like zone/terrain/world assets (%d):\n" % len(interesting))
        for n in interesting[:1500]:
            f.write("  " + n + "\n")
        if args.find:
            needle = args.find.lower()
            hits = sorted({e[0] for _, e in all_entries if needle in e[0].lower()})
            f.write("\nnames containing %r (%d):\n" % (args.find, len(hits)))
            for n in hits[:1500]:
                f.write("  " + n + "\n")
    print("Read %d assets from %d packs." % (len(all_entries), len(per_pack)))
    print("Summary:", args.out)
    print("Every name:", base + "_all.txt")
    return 0


if __name__ == "__main__":
    sys.exit(main())
