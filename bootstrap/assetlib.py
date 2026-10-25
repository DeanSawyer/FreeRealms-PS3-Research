"""Readers for the PS3 client's asset storage: the pack indexes, the asset manifest, and the zone files.

Everything here was read out of game.elf (build 1.77); the addresses are the functions that define each rule
(docs/re/assets.md and docs/re/zone-loading.md explain them). Standard library only, read-only.

Pack files (assets_ps3_NNN.pack)
    The index is a linked list of chunks. The client reads 0x2000 bytes at a time from file offset 0 (0xa28f8c),
    parses one chunk (0xda46c4) and continues at the chunk's next-chunk offset until that is 0.
    chunk := u32 next | u32 count | count x { u32 name_length | name | u32 offset | u32 length | u32 crc }
    All fields are big endian (0xda4368 reads through a stream that is big endian on every host: the stw/lbz
    endian test at 0xda3020). Names are matched case-insensitively.

Manifest (assets_ps3_manifest.bin)
    16-byte records sorted by their first field (binary search 0x7ff838):
        u64 CRC-64 of the upper-cased name | u32 crc32 | u32 size, top bit = stored compressed
    The CRC-64 is the reflected form of polynomial 0x95AC9329AC4BC9B5 with an all-ones start and final xor
    (function 0x1fac0 with the 256-entry table at 0xdb1f10). The client byte-swaps the records when it runs on the
    PS3 (0x8035c0), so this reader detects the byte order from the sort order.

Zone files
    ZoneDetails' zone name becomes three asset requests (see docs/re/zone-loading.md): <zone>.gzne (terrain header),
    <zone>.cob and <zone>.odd (object placement). An unknown name makes the request fail at once with
    "asset not in manifest" and the world never finishes loading.
"""
import glob
import os
import struct
import zlib
from collections import defaultdict

# ---------------------------------------------------------------- CRC-64 name hash (manifest key)

POLY = 0x95AC9329AC4BC9B5
MASK64 = 0xFFFFFFFFFFFFFFFF


def _crc_table():
    table = []
    for i in range(256):
        c = i
        for _ in range(8):
            c = (c >> 1) ^ POLY if c & 1 else c >> 1
        table.append(c)
    return table


CRC_TABLE = _crc_table()


def name_hash(name):
    """CRC-64 of the upper-cased name, exactly as the client computes it (only a-z are folded)."""
    data = bytes(b - 32 if 97 <= b <= 122 else b for b in name.encode("latin-1"))
    c = MASK64
    for b in data:
        c = (c >> 8) ^ CRC_TABLE[(b ^ c) & 0xFF]
    return c ^ MASK64


# ---------------------------------------------------------------- pack indexes

CHUNK = 0x2000            # index chunk size the client reads (0xa28f8c)
MAX_NAME = 512


def _parse_chunk(block, filesize):
    """One index chunk -> (next offset, [(name, offset, length, crc)]), or None if it is not a valid chunk."""
    if len(block) < 8:
        return None
    nxt, count = struct.unpack_from(">II", block, 0)
    pos, entries = 8, []
    for _ in range(count):
        if pos + 4 > len(block):
            return None
        (nlen,) = struct.unpack_from(">I", block, pos)
        if nlen == 0 or nlen > MAX_NAME or pos + 4 + nlen + 12 > len(block):
            return None
        raw = block[pos + 4:pos + 4 + nlen]
        if raw.endswith(b"\0"):
            raw = raw[:-1]
        if not raw or any(b < 32 or b > 126 for b in raw):
            return None
        off, length, crc = struct.unpack_from(">III", block, pos + 4 + nlen)
        if off > filesize or off + length > filesize + 16:
            return None
        entries.append((raw.decode("ascii"), off, length, crc))
        pos += 4 + nlen + 12
    return nxt, entries


def read_pack_index(path):
    """All (name, offset, length, crc) entries of one pack, or None if the index does not parse."""
    size = os.path.getsize(path)
    out, seen, pos = [], set(), 0
    with open(path, "rb") as f:
        while True:
            if pos in seen or pos + 8 > size:
                return None
            seen.add(pos)
            f.seek(pos)
            parsed = _parse_chunk(f.read(min(CHUNK, size - pos)), size)
            if parsed is None:
                return None
            nxt, entries = parsed
            out.extend(entries)
            if nxt == 0:
                return out
            pos = nxt


def load_packs(usrdir):
    """Index every assets_ps3_NNN.pack in a folder. Returns (pack paths, entries, unreadable pack names), where
    each entry is (pack file name, asset name, offset, length, crc)."""
    packs = sorted(glob.glob(os.path.join(usrdir, "assets_ps3_*.pack")))
    entries, unreadable = [], []
    for pk in packs:
        ents = read_pack_index(pk)
        if ents is None:
            unreadable.append(os.path.basename(pk))
            continue
        entries.extend((os.path.basename(pk),) + e for e in ents)
    return packs, entries, unreadable


def read_asset(usrdir, name):
    """Bytes of one asset by name (case-insensitive) from the packs in `usrdir`.

    Returns (data, pack file name, stored crc, crc matches) or None when no pack lists the name. The crc is the
    entry's own field; `crc matches` says whether zlib's crc32 of the bytes read equals it, which confirms that
    offset and length mean what assetlib assumes."""
    wanted = name.lower()
    for pk in sorted(glob.glob(os.path.join(usrdir, "assets_ps3_*.pack"))):
        ents = read_pack_index(pk)
        for ename, off, length, crc in ents or ():
            if ename.lower() == wanted:
                with open(pk, "rb") as f:
                    f.seek(off)
                    data = f.read(length)
                return data, os.path.basename(pk), crc, (zlib.crc32(data) & 0xFFFFFFFF) == crc
    return None


# ---------------------------------------------------------------- manifest

class Manifest:
    def __init__(self, path):
        raw = open(path, "rb").read()
        if len(raw) % 16:
            raise ValueError("%s: size %d is not a multiple of 16; not a binary manifest" % (path, len(raw)))
        self.count = len(raw) // 16
        best = None
        for endian in (">", "<"):
            recs = list(struct.iter_unpack(endian + "QII", raw))
            ordered = sum(1 for a, b in zip(recs, recs[1:]) if a[0] <= b[0])
            score = ordered / max(1, len(recs) - 1)
            if best is None or score > best[0]:
                best = (score, endian, recs)
        self.sorted_fraction, self.endian, self.records = best
        self.by_hash = {}
        for h, crc, size in self.records:
            self.by_hash.setdefault(h, (crc, size & 0x7FFFFFFF, bool(size & 0x80000000)))

    def find(self, name):
        """(crc32, size, compressed) for an asset name, or None when the client would say 'not in manifest'."""
        return self.by_hash.get(name_hash(name))


# ---------------------------------------------------------------- zones

ZONE_EXTS = (".gzne", ".cob", ".odd")


def zone_catalog(entries):
    """{zone name: set of extensions found} for every asset named <zone>.gzne/.cob/.odd (case kept from the pack)."""
    zones = defaultdict(set)
    for _pack, name, _off, _length, _crc in entries:
        stem, ext = os.path.splitext(name)
        if ext.lower() in ZONE_EXTS:
            zones[stem].add(ext.lower())
    return zones


def complete_zones(zones):
    """Zones that have everything the client requests when it enters them."""
    return sorted(z for z, exts in zones.items() if exts >= set(ZONE_EXTS))


def pick_zone(usrdir, preferred="FabledRealms"):
    """Choose the zone name to send in ZoneDetails from the install's own packs.

    Returns (zone, complete zones, note). `zone` is the preferred name when its files exist, otherwise the first
    complete zone; None if the packs cannot be read or hold no zone (the caller keeps its default)."""
    _packs, entries, _bad = load_packs(usrdir)
    if not entries:
        return None, [], "no pack index could be read in %s" % usrdir
    zones = zone_catalog(entries)
    complete = complete_zones(zones)
    lowered = {z.lower(): z for z in complete}
    if preferred.lower() in lowered:
        return lowered[preferred.lower()], complete, "preferred zone exists"
    if complete:
        return complete[0], complete, "preferred zone %r is not in the packs" % preferred
    return None, [], "the packs hold no zone with .gzne, .cob and .odd"


# ---------------------------------------------------------------- terrain header and tiles

def gzne_header(data):
    """Header fields of a <zone>.gzne file (parser 0x7df87c), or None if it is not a GZNE file.

    Returns a dict: version, a, b, edge (= a / b, cells per tile edge, stored at terrain+0x440), cell_size (float,
    terrain+0x444), min_x, min_z, ext_x, ext_z (cells, terrain+0x44c..0x458), endian."""
    if len(data) < 0x30 or data[:4] != b"GZNE":
        return None
    endian = ">" if 0 < struct.unpack_from(">i", data, 4)[0] < 256 else "<"
    ints = [struct.unpack_from(endian + "i", data, off)[0] for off in (4, 8, 12, 0x18, 0x1C, 0x20, 0x24)]
    cell_size = struct.unpack_from(endian + "f", data, 0x10)[0]
    version, a, b, min_x, min_z, ext_x, ext_z = ints
    return {"version": version, "a": a, "b": b, "edge": (a // b) if b else 0, "cell_size": cell_size,
            "min_x": min_x, "min_z": min_z, "ext_x": ext_x, "ext_z": ext_z, "endian": endian}


def _floor_cell(value, cell_size):
    import math
    return int(math.floor(value / cell_size))


def tile_origin(cell, edge):
    """Tile origin of a cell as GetCell (0x7ded28) computes it: an UNSIGNED divide (divwu) by the tile edge, then
    a multiply, as a signed 32-bit number. Exact for power-of-two edges, which is what the game uses."""
    o = ((cell & 0xFFFFFFFF) // edge) * edge
    o &= 0xFFFFFFFF
    return o - (1 << 32) if o & 0x80000000 else o


def tiles_near(header, x, z, radius):
    """Tiles the client must have loaded for IsLoaded(position x, z; radius) (0x7cfdd4).

    The client visits every cell from floor((p - radius) / cell size) to floor((p + radius) / cell size) on both
    axes. A tile outside min .. min + extent counts as loaded at once; one inside needs its file streamed in.
    Returns a sorted list of (origin x, origin z, inside the terrain box). The float-to-cell conversion
    (0x449030) is taken to be a floor; that is not proven for negative values, so callers should treat tiles one
    step away as relevant as well."""
    cs, edge = header["cell_size"], header["edge"]
    x0, x1 = _floor_cell(x - radius, cs), _floor_cell(x + radius, cs)
    z0, z1 = _floor_cell(z - radius, cs), _floor_cell(z + radius, cs)
    out = set()
    for cx in range(x0, x1 + 1):
        for cz in range(z0, z1 + 1):
            ox, oz = tile_origin(cx, edge), tile_origin(cz, edge)
            inside = (header["min_x"] <= ox < header["min_x"] + header["ext_x"]
                      and header["min_z"] <= oz < header["min_z"] + header["ext_z"])
            out.add((ox, oz, inside))
    return sorted(out)


def tile_file_names(zone, ox, oz):
    """Asset names the client requests for one tile (0x7e71bc): '<zone>_<x>_<z>.gcnk' always, and
    '<zone>_<x>_<z>.gck2' when the tile object has its extra-data flag set."""
    return ["%s_%d_%d.gcnk" % (zone, ox, oz), "%s_%d_%d.gck2" % (zone, ox, oz)]
