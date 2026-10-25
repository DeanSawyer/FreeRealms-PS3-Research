#!/usr/bin/env python3
"""List the asset names stored in the client's assets_ps3_NNN.pack files (names only, no content).

The client finds zone terrain, models and textures by name inside these packs. To know which zone name
the server must send in ZoneDetails, we need the names the PS3 client really has.

Pack layout assumed here (the layout the SOE Forgelight-era packs use, big endian):

    chunk: u32 next_chunk_offset (0 = last) | u32 asset_count | asset_count x
           { u32 name_length | name bytes | u32 offset | u32 length | u32 crc32 }

If the first pack does not parse with that layout, the script writes the first 512 bytes of the file as hex
so the real layout can be read from it. It never modifies the packs and writes no asset content.

Usage:
    python tools/list_packs.py <USRDIR folder or a single .pack> [output.txt]
    python tools/list_packs.py <USRDIR> out.txt --find fabled
"""
import argparse
import collections
import glob
import os
import re
import struct
import sys

INTERESTING = re.compile(r"terrain|zone|fabled|wilds|crossroads|tile|ground|world|geometry|\.rtt|\.tre|\.zone|town|station",
                         re.I)


def read_index(path):
    """Read only what is needed: chunks are followed by walking the next-chunk offsets with seeks."""
    size = os.path.getsize(path)
    out = []
    with open(path, "rb") as f:
        for endian in (">", "<"):
            out = []
            pos = 0
            seen = set()
            ok = True
            while True:
                if pos in seen or pos + 8 > size:
                    ok = False
                    break
                seen.add(pos)
                f.seek(pos)
                head = f.read(8)
                nxt, count = struct.unpack(endian + "II", head)
                if count > 200000:
                    ok = False
                    break
                # an index chunk is small; read generously and parse
                f.seek(pos)
                block = f.read(min(size - pos, 8 + count * 600))
                parsed = parse_chunks_one(block, endian, size)
                if parsed is None:
                    ok = False
                    break
                out.extend(parsed)
                if nxt == 0:
                    break
                if nxt > size:
                    ok = False
                    break
                pos = nxt
            if ok and out:
                return endian, out
    return None, None


def parse_chunks_one(block, endian, filesize):
    nxt, count = struct.unpack_from(endian + "II", block, 0)
    p = 8
    res = []
    for _ in range(count):
        if p + 4 > len(block):
            return None
        (nlen,) = struct.unpack_from(endian + "I", block, p)
        if nlen == 0 or nlen > 512 or p + 4 + nlen + 12 > len(block):
            return None
        raw = block[p + 4:p + 4 + nlen]
        if any(b < 32 or b > 126 for b in raw):
            return None
        off, length, crc = struct.unpack_from(endian + "III", block, p + 4 + nlen)
        if off > filesize or off + length > filesize + 16:
            return None
        res.append((raw.decode("ascii"), off, length, crc))
        p += 4 + nlen + 12
    return res


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

    all_entries = []
    per_pack = []
    failed = []
    for pk in packs:
        endian, entries = read_index(pk)
        if entries is None:
            failed.append(pk)
            continue
        per_pack.append((os.path.basename(pk), "big" if endian == ">" else "little", len(entries)))
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
        for n, endian, c in per_pack:
            f.write("%-24s %-6s %d assets\n" % (n, endian, c))
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
