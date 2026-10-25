#!/usr/bin/env python3
"""Which terrain tile files does the client need around the player, and does the install have them?

After <zone>.gzne is parsed, the client asks for one file per terrain tile (read from game.elf):

    <zone>_<x>_<z>.gcnk      fn_007e71bc  ("%s_%d_%d.gcnk"), always
    <zone>_<x>_<z>.gck2      fn_007e71bc  ("%s_%d_%d.gck2"), only when the tile has its extra-data flag set

x and z are the tile's origin cell (a multiple of the tile edge A / B from the header). The loading overlay drops
only when every tile within a radius of the camera has arrived: 96.0 world units to hide the overlay (constant at
0xde9d08, used at 0xef650), 32.0 to show it again (0xde9d0c, used at 0xf04f8). A tile that is in neither a pack nor
the manifest cannot be loaded; one that is only in the manifest has to be downloaded. See docs/re/zone-loading.md.

Usage (PowerShell):
    python tools\\tile_report.py "C:\\...\\NPUA30048\\USRDIR"
    python tools\\tile_report.py "C:\\...\\USRDIR" --zone FabledRealms --x -1904.883 --z 412.6024 -o tile_report.txt

Only pack indexes, the manifest and the .gzne header are read; the only file written is the report.
"""
import argparse
import collections
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bootstrap"))
import assetlib  # noqa: E402

RADIUS_HIDE = 96.0     # 0xde9d08
RADIUS_SHOW = 32.0     # 0xde9d0c


def status(name, by_name, manifest):
    """('pack'|'manifest only'|'MISSING', detail) for one asset name."""
    ent = by_name.get(name.lower())
    rec = manifest.find(name) if manifest else None
    if ent:
        same = rec is not None and rec[0] == ent[4]
        return "in pack", "pack crc %08x, manifest %s" % (ent[4], "equal" if same else
                                                         ("crc %08x DIFFERENT" % rec[0] if rec else "missing"))
    if rec:
        return "manifest only", "size %d%s (the client would have to download it)" % (rec[1], " (z)" if rec[2] else "")
    return "MISSING", "in neither the packs nor the manifest"


def report(usrdir, zone, probes, radius):
    lines = []
    out = lines.append
    _packs, entries, bad = assetlib.load_packs(usrdir)
    out("packs read: %d, assets: %d, unreadable packs: %d" % (len(_packs), len(entries), len(bad)))
    mpath = os.path.join(usrdir, "assets_ps3_manifest.bin")
    manifest = assetlib.Manifest(mpath) if os.path.exists(mpath) else None
    out("manifest: %s" % ("%d records" % manifest.count if manifest else "NOT FOUND"))
    by_name = {e[1].lower(): e for e in entries}

    found = assetlib.read_asset(usrdir, zone + ".gzne")
    if not found:
        out("%s.gzne is not in the packs: nothing to do here (run zone_report.py)." % zone)
        return lines
    header = assetlib.gzne_header(found[0])
    if not header:
        out("%s.gzne does not start with GZNE (first bytes %s)" % (zone, found[0][:8].hex(" ")))
        return lines
    out("")
    out("%s.gzne: version %d, tile edge %d cells (%d / %d), cell size %g, box x %d..%d, z %d..%d (cells)" % (
        zone, header["version"], header["edge"], header["a"], header["b"], header["cell_size"],
        header["min_x"], header["min_x"] + header["ext_x"], header["min_z"], header["min_z"] + header["ext_z"]))
    cs = header["cell_size"]
    out("  in world units: x %.0f..%.0f, z %.0f..%.0f" % (
        header["min_x"] * cs, (header["min_x"] + header["ext_x"]) * cs,
        header["min_z"] * cs, (header["min_z"] + header["ext_z"]) * cs))

    prefix = zone.lower() + "_"
    tile_re = re.compile(r"^" + re.escape(prefix) + r"(-?\d+)_(-?\d+)\.(gcnk|gck2)$", re.I)
    in_pack = collections.defaultdict(set)
    for e in entries:
        m = tile_re.match(e[1])
        if m:
            in_pack[m.group(3).lower()].add((int(m.group(1)), int(m.group(2))))
    out("")
    out("tile files named %s_<x>_<z>.* inside the packs: .gcnk %d, .gck2 %d" % (
        zone, len(in_pack["gcnk"]), len(in_pack["gck2"])))
    for ext, coords in sorted(in_pack.items()):
        if coords:
            xs, zs = [c[0] for c in coords], [c[1] for c in coords]
            out("  .%s origins: x %d..%d, z %d..%d" % (ext, min(xs), max(xs), min(zs), max(zs)))

    edge = header["edge"] or 1
    if manifest and header["edge"]:
        total = man = loc = 0
        for ox in range(header["min_x"], header["min_x"] + header["ext_x"], edge):
            for oz in range(header["min_z"], header["min_z"] + header["ext_z"], edge):
                total += 1
                name = "%s_%d_%d.gcnk" % (zone, ox, oz)
                man += manifest.find(name) is not None
                loc += name.lower() in by_name
        out("every tile of the box (%d): .gcnk in the manifest %d, in the packs %d" % (total, man, loc))

    if manifest and header["edge"]:
        out("")
        out("map of the terrain box, one character per tile (rows = z from low to high, columns = x from low to high):")
        out("  # stored in the packs   + only in the manifest (download)   . in neither   P = PC spawn tile, O = origin")
        pc_tile = tuple(assetlib.tile_origin(assetlib._floor_cell(v, cs), edge) for v in (-1904.883, 412.6024))
        o_tile = (assetlib.tile_origin(0, edge), assetlib.tile_origin(0, edge))
        for oz in range(header["min_z"], header["min_z"] + header["ext_z"], edge):
            row = ""
            for ox in range(header["min_x"], header["min_x"] + header["ext_x"], edge):
                name = "%s_%d_%d.gcnk" % (zone, ox, oz)
                ch = "#" if name.lower() in by_name else ("+" if manifest.find(name) else ".")
                if (ox, oz) == pc_tile:
                    ch = "P"
                elif (ox, oz) == o_tile:
                    ch = "O"
                row += ch
            out("  z %4d  %s" % (oz, row))
        out("  columns start at x = %d, one per %d cells" % (header["min_x"], edge))

    picked = assetlib.pick_spawn(usrdir, zone)
    if picked:
        out("")
        out("spawn the server now picks: x=%.1f z=%.1f, tile %s - %s" % (picked[0], picked[1], picked[2], picked[3]))
        got = assetlib.read_asset(usrdir, "%s_%d_%d.gcnk" % (zone, picked[2][0], picked[2][1]))
        if got:
            data = got[0]
            out("  that tile: %d bytes, first 64 bytes: %s" % (len(data), data[:64].hex(" ")))

    if picked:
        probes = list(probes) + [("spawn the server now picks", (picked[0], picked[1]))]
    for label, (px, pz) in probes:
        for r in radius:
            out("")
            out("== %s at x=%.3f z=%.3f, radius %g ==" % (label, px, pz, r))
            tiles = assetlib.tiles_near(header, px, pz, r)
            inside = [t for t in tiles if t[2]]
            out("tiles visited: %d, inside the terrain box (must be streamed): %d" % (len(tiles), len(inside)))
            counts = collections.Counter()
            for ox, oz, ins in tiles:
                if not ins:
                    continue
                st, detail = status("%s_%d_%d.gcnk" % (zone, ox, oz), by_name, manifest)
                counts[st] += 1
                extra = status("%s_%d_%d.gck2" % (zone, ox, oz), by_name, manifest)[0]
                out("  tile %6d %6d  .gcnk %-13s %s;  .gck2 %s" % (ox, oz, st, detail, extra))
            out("summary: %s" % (dict(counts) or "no tile of this area is inside the terrain box"))
    return lines


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("usrdir")
    ap.add_argument("--zone", default="FabledRealms")
    ap.add_argument("--x", type=float, default=-1904.883, help="world x of the PC game spawn")
    ap.add_argument("--z", type=float, default=412.6024, help="world z of the PC game spawn")
    ap.add_argument("-o", "--out", default="tile_report.txt")
    args = ap.parse_args()
    probes = [("PC game spawn (the old server default)", (args.x, args.z)), ("world origin", (0.0, 0.0))]
    text = "\n".join(report(args.usrdir, args.zone, probes, [RADIUS_HIDE])) + "\n"
    with open(args.out, "w", encoding="utf-8") as f:
        f.write(text)
    print(text)
    print("Report written to", os.path.abspath(args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
