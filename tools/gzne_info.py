#!/usr/bin/env python3
"""Print the header of a zone's terrain file (<zone>.gzne) straight from the install's packs.

The field order is the one the client's own parser reads (FUN_007df87c, see docs/re/zone-loading.md); the names say
what the client does with each field. The values are what the server needs to place the player inside the terrain:
the cell grid the client checks is `min cell .. min cell + extent` (GetCell, FUN_007ded28), and the client turns a
world position into a cell with floor(position / cell size) (IsLoaded, fn_007cfdd4).

Usage (PowerShell):
    python tools\\gzne_info.py "C:\\...\\NPUA30048\\USRDIR" Sanctuary_Hub
    python tools\\gzne_info.py "C:\\...\\USRDIR" FabledRealms -o gzne_info.txt
"""
import argparse
import math
import os
import struct
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bootstrap"))
import assetlib  # noqa: E402

# (file offset, client field offset, role). Roles are what the client does with the value.
FIELDS = [
    (0x04, None, "version (2 adds one more value at the end)"),
    (0x08, 0x438, "A"),
    (0x0C, 0x43C, "B; cells per tile edge = A / B"),
    (0x10, 0x444, "cell size in world units (float; IsLoaded divides positions by it)"),
    (0x14, 0x448, "unknown (copied, not used by the loaded check)"),
    (0x18, 0x44C, "min cell X (int)"),
    (0x1C, 0x450, "min cell Z (int)"),
    (0x20, 0x454, "extent in cells along X (int)"),
    (0x24, 0x458, "extent in cells along Z (int)"),
    (0x28, None, "unknown (read into a local, unused)"),
    (0x2C, None, "count of the entry records that follow"),
]


def describe(data):
    out = []
    if data[:4] != b"GZNE":
        out.append("not a GZNE file: starts with %r" % data[:8])
        return out
    # The client copies these bytes verbatim into native (big endian) integers; show both readings and pick the
    # one that gives a plausible version number.
    vals = {}
    for endian in (">", "<"):
        vals[endian] = [struct.unpack_from(endian + "i", data, off)[0] for off, _c, _r in FIELDS]
    endian = ">" if 0 < vals[">"][0] < 256 else "<"
    out.append("byte order of the numbers: %s endian (PS3 native is big)" % ("big" if endian == ">" else "little"))
    v = vals[endian]
    for (off, coff, role), val in zip(FIELDS, v):
        fl = struct.unpack_from(endian + "f", data, off)[0]
        shown = "%d" % val
        if off in (0x10, 0x14):
            shown += "   (as float: %g)" % fl
        out.append("  +0x%02x  %-12s %-34s %s" % (off, ("client+0x%x" % coff) if coff else "", shown, role))
    a, b = v[1], v[2]
    cs = struct.unpack_from(endian + "f", data, 0x10)[0]
    if b:
        out.append("cells per tile edge (A / B): %d" % (a // b))
    if math.isfinite(cs) and cs > 0:
        minx, minz, ex, ez = v[5], v[6], v[7], v[8]
        out.append("world X range: %.1f .. %.1f" % (minx * cs, (minx + ex) * cs))
        out.append("world Z range: %.1f .. %.1f" % (minz * cs, (minz + ez) * cs))
        out.append("(GetCell rounds a cell down to a multiple of the tile edge (A / B, an unsigned divide) and compares "
                   "that with min .. min + extent; outside the range counts as 'loaded' for the overlay, inside it "
                   "the cells around the camera must be streamed in first)")
    out.append("file size: %d bytes" % len(data))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("usrdir")
    ap.add_argument("zone", help="zone name without extension")
    ap.add_argument("-o", "--out", default="gzne_info.txt")
    args = ap.parse_args()
    found = assetlib.read_asset(args.usrdir, args.zone + ".gzne")
    if found is None:
        text = "%s.gzne is not in any pack under %s\n" % (args.zone, args.usrdir)
    else:
        data, pack, crc, ok = found
        head = ["%s.gzne in %s, %d bytes, crc 0x%08x (%s)" % (
            args.zone, pack, len(data), crc, "matches the bytes read" if ok else "DOES NOT match the bytes read")]
        text = "\n".join(head + describe(data)) + "\n"
        text += "first 64 bytes: %s\n" % data[:64].hex(" ")
    with open(args.out, "w", encoding="utf-8") as f:
        f.write(text)
    print(text)
    print("Written to", os.path.abspath(args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
