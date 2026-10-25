#!/usr/bin/env python3
"""Generate the C# reference vectors for src/ps3/Sanctuary.Ps3.Tests from the Python reference implementation.

The C# port (src/ps3/Sanctuary.Ps3) is checked against bootstrap/assetlib.py: this script builds small synthetic installs
(pack files, manifests, terrain headers; no game data), runs the Python readers on them and writes what they answered into
src/ps3/Sanctuary.Ps3.Tests/ReferenceVectors.g.cs. The tests then run the C# code on the same bytes and compare.

    python tools/gen_ps3_vectors.py            # rewrite the generated file
    python tools/gen_ps3_vectors.py --check    # exit 1 if the committed file is out of date (used by CI)

Standard library only. The output contains no compressed bytes, so it does not depend on the zlib version.
"""
import argparse
import base64
import math
import os
import shutil
import struct
import sys
import tempfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "bootstrap"))

import assetlib  # noqa: E402

OUT = os.path.join(ROOT, "src", "ps3", "Sanctuary.Ps3.Tests", "ReferenceVectors.g.cs")
CHUNK = 0x2000


# ---------------------------------------------------------------- C# literals

def cs_str(text):
    out = ['"']
    for ch in text:
        code = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif 32 <= code < 127:
            out.append(ch)
        else:
            out.append("\\u%04x" % code)
    out.append('"')
    return "".join(out)


def cs_opt_str(text):
    return "null" if text is None else cs_str(text)


def cs_bool(value):
    return "true" if value else "false"


def cs_double(value):
    text = repr(float(value))
    if "." not in text and "e" not in text and "E" not in text:
        text += ".0"
    return text


def cs_bytes(data):
    """Convert.FromBase64String over concatenated lines, so the generated file stays readable."""
    encoded = base64.b64encode(data).decode("ascii")
    if len(encoded) <= 100:
        return "Convert.FromBase64String(%s)" % cs_str(encoded)
    lines = [encoded[i:i + 100] for i in range(0, len(encoded), 100)]
    body = " +\n            ".join(cs_str(line) for line in lines)
    return "Convert.FromBase64String(\n            %s)" % body


def array(type_name, items, indent="    "):
    if not items:
        return "[]"
    inner = (",\n%s    " % indent).join(items)
    return "[\n%s    %s,\n%s]" % (indent, inner, indent)


# ---------------------------------------------------------------- synthetic installs

def gzne(version=3, a=256, b=16, cell=2.0, min_x=-32, min_z=-32, ext_x=96, ext_z=96, big=True, magic=b"GZNE", size=0x40):
    e = ">" if big else "<"
    data = bytearray(size)
    data[0:4] = magic
    if size >= 0x28:
        struct.pack_into(e + "iii", data, 4, version, a, b)
        struct.pack_into(e + "f", data, 0x10, cell)
        struct.pack_into(e + "iiii", data, 0x18, min_x, min_z, ext_x, ext_z)
    return bytes(data)


def build_pack(chunks, nul_terminated=True, crc_overrides=None):
    """chunks: list of lists of (name, data). Index chunks first (0x2000 bytes each), then the asset data."""
    crc_overrides = crc_overrides or {}
    data_start = len(chunks) * CHUNK
    payload = bytearray()
    placed = []
    for chunk in chunks:
        entries = []
        for name, data in chunk:
            offset = data_start + len(payload)
            payload += data
            crc = crc_overrides.get(name, zlib.crc32(data) & 0xFFFFFFFF)
            entries.append((name, offset, len(data), crc))
        placed.append(entries)
    out = bytearray()
    for index, entries in enumerate(placed):
        nxt = (index + 1) * CHUNK if index + 1 < len(placed) else 0
        body = struct.pack(">II", nxt, len(entries))
        for number, (name, offset, length, crc) in enumerate(entries):
            raw = name.encode("ascii")
            if nul_terminated and number % 2 == 0:
                raw += b"\0"
            body += struct.pack(">I", len(raw)) + raw + struct.pack(">III", offset, length, crc)
        assert len(body) <= CHUNK, "index chunk overflow"
        out += body.ljust(CHUNK, b"\0")
    return bytes(out + payload)


def blob(label):
    return ("data:" + label).encode("ascii")


def tile(zone, x, z, ext=".gcnk"):
    return ("%s_%d_%d%s" % (zone, x, z, ext), blob("tile %d %d" % (x, z)))


def main_install():
    fabled_hdr = gzne(3, 256, 16, 2.0, -32, -32, 96, 96)
    other_hdr = gzne(3, 64, 8, 0.1, 0, 0, 32, 32, big=False)
    sparse_hdr = gzne(3, 256, 16, 2.0, -16, -16, 48, 48)
    outside_hdr = gzne(3, 256, 16, 2.0, 0, 0, 32, 32)
    no_edge_hdr = gzne(3, 256, 0, 2.0, 0, 0, 32, 32)
    bad_magic_hdr = gzne(magic=b"XXXX")

    def zone_files(zone, header):
        return [(zone + ".gzne", header), (zone + ".cob", blob(zone + " cob")), (zone + ".odd", blob(zone + " odd"))]

    box = [(x, z) for x in range(-32, 64, 16) for z in range(-32, 64, 16)]
    holes = {(0, 0), (0, 16), (-16, 0)}
    fabled_tiles = [t for t in box if t not in holes and t != (-16, -16)]
    outside = [(96, 96), (-64, -64)]

    first = fabled_tiles[:14]
    rest = fabled_tiles[14:]

    pack0 = [
        zone_files("FabledRealms", fabled_hdr)
        + [("Partial.gzne", gzne()), ("Partial.cob", blob("partial cob"))]
        + [("readme.txt", blob("first readme")), ("bad.bin", blob("bad crc"))]
        + zone_files("OtherZone", other_hdr)
    ]
    pack1 = [
        [tile("FabledRealms", x, z) for x, z in first] + zone_files("Sparse", sparse_hdr),
        [tile("FabledRealms", x, z) for x, z in rest]
        + [("FABLEDREALMS_-16_-16.GCNK", blob("upper case tile"))]
        + [tile("FabledRealms", 16, 32, ".gck2")]
        + [tile("FabledRealms", x, z) for x, z in outside]
        + [("readme.txt", blob("second readme"))]
        + [tile("Sparse", -16, -16), tile("Sparse", 16, 16)]
        + zone_files("NoEdge", no_edge_hdr) + [tile("NoEdge", 0, 0)]
        + zone_files("Outside", outside_hdr) + [tile("Outside", 96, 96), tile("Outside", -16, 0)]
        + zone_files("BadMagic", bad_magic_hdr) + [tile("BadMagic", 0, 0)],
    ]
    garbage = bytes([0xFF]) * 100
    return [
        ("assets_ps3_000.pack", build_pack(pack0, crc_overrides={"bad.bin": 0xDEADBEEF})),
        ("assets_ps3_001.pack", build_pack(pack1, nul_terminated=False)),
        ("assets_ps3_002.pack", garbage),
        ("notes.txt", b"not a pack"),
    ]


def no_zones_install():
    return [("assets_ps3_000.pack", build_pack([[("a.txt", blob("a")), ("b.cob", blob("b")), ("c.odd", blob("c"))]]))]


def materialise(files):
    folder = tempfile.mkdtemp(prefix="fr-vectors-")
    for name, data in files:
        with open(os.path.join(folder, name), "wb") as handle:
            handle.write(data)
    return folder


# ---------------------------------------------------------------- manifest

def make_manifest(items, big=True):
    recs = [(assetlib.name_hash(n), crc, size | (0x80000000 if comp else 0)) for n, crc, size, comp in items]
    recs.sort(key=lambda r: r[0])
    endian = ">" if big else "<"
    return b"".join(struct.pack(endian + "QII", *r) for r in recs)


def manifest_vector(label, raw, names):
    folder = tempfile.mkdtemp(prefix="fr-manifest-")
    try:
        path = os.path.join(folder, "m.bin")
        with open(path, "wb") as handle:
            handle.write(raw)
        manifest = assetlib.Manifest(path)
    finally:
        shutil.rmtree(folder, ignore_errors=True)
    lookups = []
    for name in names:
        found = manifest.find(name)
        if found is None:
            lookups.append("(%s, false, 0u, 0u, false)" % cs_str(name))
        else:
            crc, size, compressed = found
            lookups.append("(%s, true, 0x%08xu, %du, %s)" % (cs_str(name), crc, size, cs_bool(compressed)))
    return ("new ManifestVector(%s, %s, %s, %s, %d,\n        %s)" % (
        cs_str(label), cs_bytes(raw), cs_bool(manifest.endian == ">"), cs_double(manifest.sorted_fraction),
        manifest.count, array("", lookups, "        ")))


# ---------------------------------------------------------------- generation

def gzne_vector(label, data):
    header = assetlib.gzne_header(data)
    if header is None:
        return "new GzneVector(%s, %s, false, 0, 0, 0, 0, 0.0, 0, 0, 0, 0, false)" % (cs_str(label), cs_bytes(data))
    return "new GzneVector(%s, %s, true, %d, %d, %d, %d, %s, %d, %d, %d, %d, %s)" % (
        cs_str(label), cs_bytes(data), header["version"], header["a"], header["b"], header["edge"],
        cs_double(header["cell_size"]), header["min_x"], header["min_z"], header["ext_x"], header["ext_z"],
        cs_bool(header["endian"] == ">"))


def generate():
    out = []
    add = out.append
    add("// <auto-generated>")
    add("// Written by tools/gen_ps3_vectors.py from bootstrap/assetlib.py. Do not edit; run the script instead.")
    add("// </auto-generated>")
    add("#nullable enable")
    add("")
    add("using System;")
    add("")
    add("namespace Sanctuary.Ps3.Tests;")
    add("")
    add("internal sealed record GzneVector(string Label, byte[] Data, bool Valid, int Version, int A, int B, int Edge, double CellSize, int MinX, int MinZ, int ExtX, int ExtZ, bool BigEndian);")
    add("")
    add("internal sealed record ManifestVector(string Label, byte[] Raw, bool BigEndian, double SortedFraction, int Count, (string Name, bool Found, uint Crc, uint Size, bool Compressed)[] Lookups);")
    add("")
    add("internal sealed record NearVector(double X, double Z, double Radius, (int Ox, int Oz, bool Inside)[] Tiles);")
    add("")
    add("internal sealed record InstallVector(")
    add("    string Label,")
    add("    (string File, byte[] Data)[] Files,")
    add("    string[] Packs,")
    add("    string[] Unreadable,")
    add("    (string Pack, string Name, uint Offset, uint Length, uint Crc)[] Entries,")
    add("    string[] CompleteZones,")
    add("    (string Preferred, string? Zone, string Note)[] Picks,")
    add("    (string Name, string Pack, uint StoredCrc, bool CrcMatches, int Length, uint DataCrc)[] Assets,")
    add("    string[] MissingAssets,")
    add("    (string Zone, bool Found, double X, double Z, int TileX, int TileZ, string Note)[] Spawns);")
    add("")
    add("internal static class ReferenceVectors")
    add("{")

    # name hash
    names = ["a.txt", "A.TXT", "FabledRealms.gzne", "", "art/environments/terraincalendardefinitions.txt",
             "Mixed_Case-09~[]{}@`", "Ünï.bin", "z", "{", "ÿ"]
    add("    public static readonly (string Name, ulong Hash)[] NameHashes = %s;" % array(
        "", ["(%s, 0x%016xUL)" % (cs_str(n), assetlib.name_hash(n)) for n in names]))
    add("")

    # crc32
    samples = [b"", b"a", b"123456789", bytes(range(256)), blob("terraincalendardefinitions")]
    add("    public static readonly (byte[] Data, uint Crc)[] Crc32s = %s;" % array(
        "", ["(%s, 0x%08xu)" % (cs_bytes(s), zlib.crc32(s) & 0xFFFFFFFF) for s in samples]))
    add("")

    # gzne headers
    gz = [
        ("big endian", gzne(3, 256, 16, 2.0, -32, -32, 96, 96)),
        ("little endian, float cell size", gzne(3, 64, 8, 0.1, 0, 0, 32, 32, big=False)),
        ("no edge", gzne(3, 256, 0, 2.0, 0, 0, 32, 32)),
        ("negative a floors", gzne(3, -100, 16, 2.0, 0, 0, 32, 32)),
        ("negative b floors", gzne(3, 100, -16, 2.0, 0, 0, 32, 32)),
        ("version 300 is little endian", gzne(300, 256, 16, 2.0, -32, -32, 96, 96, big=True)),
        ("too short", gzne(size=0x2F)),
        ("wrong magic", gzne(magic=b"GZNF")),
        ("empty", b""),
    ]
    add("    public static readonly GzneVector[] GzneHeaders = %s;" % array("", [gzne_vector(l, d) for l, d in gz]))
    add("")

    # floor cells, tile origins
    cs32 = struct.unpack("<f", struct.pack("<f", 0.1))[0]
    floors = [(0.0, 2.0), (1.99, 2.0), (2.0, 2.0), (-0.01, 2.0), (-2.0, 2.0), (-2.01, 2.0), (95.9, cs32), (-95.9, cs32),
              (1000.5, 1.5), (-1000.5, 1.5)]
    floor_items = ["(%s, %s, %d)" % (cs_double(v), cs_double(c), int(math.floor(v / c))) for v, c in floors]
    add("    public static readonly (double Value, double CellSize, int Cell)[] FloorCells = %s;" % array("", floor_items))
    add("")
    cells = [0, 1, 15, 16, 17, -1, -5, -16, -17, 1000, -1000, 2147483647, -2147483648]
    origins = ["(%d, %d, %d)" % (c, e, assetlib.tile_origin(c, e)) for c in cells for e in (8, 16, 32, 64)]
    add("    public static readonly (int Cell, int Edge, int Origin)[] TileOrigins = %s;" % array("", origins))
    add("")

    # tiles near
    fabled = assetlib.gzne_header(gz[0][1])
    positions = [(0.0, 0.0, 96.0), (-50.5, 70.25, 96.0), (1000.0, 1000.0, 96.0), (0.0, 0.0, 0.0), (47.9, -31.9, 10.0),
                 (-100.0, -100.0, 96.0)]
    near = []
    for x, z, r in positions:
        tiles = ["(%d, %d, %s)" % (ox, oz, cs_bool(inside)) for ox, oz, inside in assetlib.tiles_near(fabled, x, z, r)]
        near.append("new NearVector(%s, %s, %s, %s)" % (cs_double(x), cs_double(z), cs_double(r), array("", tiles, "        ")))
    add("    /// <summary>Tiles for the first header of <see cref=\"GzneHeaders\"/>.</summary>")
    add("    public static readonly NearVector[] TilesNear = %s;" % array("", near))
    add("")
    files = [("FabledRealms", -16, 32), ("Zone", 0, 0), ("a_b", -64, -64)]
    add("    public static readonly (string Zone, int Ox, int Oz, string Gcnk, string Gck2)[] TileFileNames = %s;" % array(
        "", ["(%s, %d, %d, %s, %s)" % ((cs_str(z), ox, oz) + tuple(cs_str(n) for n in assetlib.tile_file_names(z, ox, oz)))
              for z, ox, oz in files]))
    add("")

    # manifests
    items = [("art/a.txt", 0x11111111, 100, False), ("art/b.dds", 0x22222222, 5000, True), ("Zone.gzne", 0x33333333, 70000, True),
             ("zone.cob", 0x44444444, 1, False), ("ZONE.ODD", 0x55555555, 2147483647, True), ("x", 0x66666666, 0, False)]
    dup = items + [("art/a.txt", 0x99999999, 7, True)]
    probe = [n for n, _c, _s, _z in items] + ["not-in-manifest.bin", "ART/A.TXT", "Zone.COB"]
    manifests = [
        manifest_vector("big endian", make_manifest(items, True), probe),
        manifest_vector("little endian", make_manifest(items, False), probe),
        manifest_vector("duplicate name, first record wins", make_manifest(dup, True), probe),
        manifest_vector("one record written little endian ties to big endian", make_manifest(items[:1], False), probe),
        manifest_vector("empty", b"", probe),
    ]
    add("    public static readonly ManifestVector[] Manifests = %s;" % array("", manifests))
    add("")

    # installs
    installs = []
    for label, files_, preferred, assets, missing, zones in [
        ("main", main_install(), ["FabledRealms", "fabledrealms", "Missing", "OtherZone"],
         ["FabledRealms.gzne", "fabledrealms.COB", "readme.txt", "bad.bin", "fabledrealms_-16_-16.gcnk", "OtherZone.odd"],
         ["missing.bin", "FabledRealms"],
         ["FabledRealms", "fabledrealms", "Sparse", "NoEdge", "Outside", "BadMagic", "OtherZone", "Partial", "Missing"]),
        ("no zones", no_zones_install(), ["FabledRealms"], ["a.txt"], [], ["FabledRealms"]),
        ("no packs", [("notes.txt", b"x")], ["FabledRealms"], [], ["a.txt"], ["FabledRealms"]),
    ]:
        folder = materialise(files_)
        try:
            packs, entries, bad = assetlib.load_packs(folder)
            picks = []
            for want in preferred:
                zone, complete, note = assetlib.pick_zone(folder, want)
                picks.append("(%s, %s, %s)" % (cs_str(want), cs_opt_str(zone), cs_str(note.replace(folder, "{usr}"))))
            _zone, complete, _note = assetlib.pick_zone(folder, preferred[0])
            found = []
            for name in assets:
                result = assetlib.read_asset(folder, name)
                data, pack, crc, matches = result
                found.append("(%s, %s, 0x%08xu, %s, %d, 0x%08xu)" % (
                    cs_str(name), cs_str(pack), crc, cs_bool(matches), len(data), zlib.crc32(data) & 0xFFFFFFFF))
            for name in missing:
                assert assetlib.read_asset(folder, name) is None, name
            spawns = []
            for zone in zones:
                spawn = assetlib.pick_spawn(folder, zone)
                if spawn is None:
                    spawns.append("(%s, false, 0.0, 0.0, 0, 0, \"\")" % cs_str(zone))
                else:
                    x, z, best, note = spawn
                    spawns.append("(%s, true, %s, %s, %d, %d, %s)" % (cs_str(zone), cs_double(x), cs_double(z), best[0], best[1], cs_str(note)))
            entry_items = ["(%s, %s, %du, %du, 0x%08xu)" % (cs_str(p), cs_str(n), o, ln, c) for p, n, o, ln, c in entries]
            installs.append("""new InstallVector(
            %s,
            %s,
            %s,
            %s,
            %s,
            %s,
            %s,
            %s,
            %s,
            %s)""" % (
                cs_str(label),
                array("", ["(%s, %s)" % (cs_str(n), cs_bytes(d)) for n, d in files_], "            "),
                array("", [cs_str(os.path.basename(p)) for p in packs], "            "),
                array("", [cs_str(b) for b in bad], "            "),
                array("", entry_items, "            "),
                array("", [cs_str(z) for z in complete], "            "),
                array("", picks, "            "),
                array("", found, "            "),
                array("", [cs_str(m) for m in missing], "            "),
                array("", spawns, "            ")))
        finally:
            shutil.rmtree(folder, ignore_errors=True)
    add("    public static readonly InstallVector[] Installs = %s;" % array("", installs))
    add("}")
    return "\n".join(out) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="fail if the committed file differs from a fresh run")
    args = parser.parse_args()
    text = generate()
    if args.check:
        current = open(OUT, encoding="utf-8", newline="").read() if os.path.exists(OUT) else ""
        if current.replace("\r\n", "\n") != text:
            print("%s is out of date; run python tools/gen_ps3_vectors.py" % os.path.relpath(OUT, ROOT))
            return 1
        print("reference vectors are up to date")
        return 0
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print("wrote %s (%d bytes)" % (os.path.relpath(OUT, ROOT), len(text)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
