"""Candidate encoders for the PS3 patch-asset (.zs) experiments.

Every entry is a *hypothesis*. Nothing here is a verified description of the
original wire format. Used by fr_ps3_bootstrap.py (--codec) and
tools/codec_sizes.py. Standard library only.
"""

import bz2
import gzip
import lzma
import struct
import zlib

DEFAULT_CODEC = "zs-zlib-zero-trailer"  # the container the client accepts (docs/install/digest-format.md); "lzma-alone" is the first, rejected experiment


def _lzma_alone(raw):
    return lzma.compress(raw, format=lzma.FORMAT_ALONE, preset=6)


def _lzma_alone_sized(raw):
    # Same stream, but the 13-byte header carries the real uncompressed size
    # instead of the "unknown" marker (-1) that Python writes.
    data = bytearray(_lzma_alone(raw))
    data[5:13] = struct.pack("<Q", len(raw))
    return bytes(data)


def _lzma_body_only(raw):
    # LZMA body without the 13-byte props/dict/size header.
    return _lzma_alone(raw)[13:]


def _raw_deflate(raw, level=9):
    co = zlib.compressobj(level, zlib.DEFLATED, -15)
    return co.compress(raw) + co.flush()


BASE_CODECS = {
    "lzma-alone": _lzma_alone,
    "lzma-alone-sized": _lzma_alone_sized,
    "lzma-body-only": _lzma_body_only,
    "xz": lambda raw: lzma.compress(raw, format=lzma.FORMAT_XZ),
    "zlib-6": lambda raw: zlib.compress(raw, 6),
    "zlib-9": lambda raw: zlib.compress(raw, 9),
    "zlib-1": lambda raw: zlib.compress(raw, 1),
    "raw-deflate": _raw_deflate,
    "gzip": lambda raw: gzip.compress(raw, 9, mtime=0),
    "bz2": lambda raw: bz2.compress(raw),
    "stored": lambda raw: bytes(raw),
}

# Optional framing in front of the payload (all hypotheses).
PREFIXES = {
    "": lambda raw: b"",
    "+len-le32": lambda raw: struct.pack("<I", len(raw)),
    "+len-be32": lambda raw: struct.pack(">I", len(raw)),
    "+len-crc-le32": lambda raw: struct.pack("<II", len(raw), zlib.crc32(raw)),
    "+len-crc-be32": lambda raw: struct.pack(">II", len(raw), zlib.crc32(raw)),
}


# --- Observed .zs container (see docs/install/digest-format.md) -----------------------
# Derived from the digest, not from a captured .zs file:
#   * every manifest entry's compressed length = payload + 129 bytes
#   * payload is a zlib stream (78 xx ...), or the raw bytes when zlib does not
#     make the file smaller (11 of 741 entries)
#   * the digest itself ends with a 129-byte trailer: 0x01 + 128 random-looking
#     bytes (possibly an RSA-1024 signature; unverified)
# Whether the client validates the trailer is unknown; these candidates test it.
ZS_TRAILER_LEN = 129
_digest_trailer = None


def set_digest_trailer(trailer):
    """Remember the 129-byte tail of the digest's own zlib stream."""
    global _digest_trailer
    _digest_trailer = bytes(trailer) if len(trailer) == ZS_TRAILER_LEN else None


def _stored_blocks(data, final):
    """Deflate stored (uncompressed) blocks covering ``data``; byte-aligned input required."""
    out = bytearray()
    pos = 0
    while True:
        chunk = data[pos:pos + 65535]
        pos += len(chunk)
        last = final and pos >= len(data)
        out += bytes([1 if last else 0]) + struct.pack("<HH", len(chunk), len(chunk) ^ 0xFFFF)
        out += chunk
        if pos >= len(data):
            return bytes(out)


def pad_zlib(raw, target, tail=96):
    """A valid zlib stream for ``raw`` that is exactly ``target`` bytes long, or None.

    Builds: zlib header, a level-9 deflate of all but the last ``tail`` bytes,
    a few stored blocks (each an empty 5-byte block, or real data) to land on
    the exact length, then the Adler-32. Decompresses to ``raw`` with any
    standard inflater. Returns None when ``target`` is too small to reach.
    """
    n0 = max(len(raw) - tail, 0)
    base = zlib.compressobj(9, zlib.DEFLATED, -15)
    prefix = base.compress(raw[:n0])
    header = b"\x78\xda"
    adler = struct.pack(">I", zlib.adler32(raw))
    for d in range(0, min(tail, len(raw) - n0) + 1):
        step = base.copy()
        head = prefix + step.compress(raw[n0:n0 + d]) + step.flush(zlib.Z_SYNC_FLUSH)
        rest = raw[n0 + d:]
        final = _stored_blocks(rest, True)
        total = len(header) + len(head) + len(final) + len(adler)
        gap = target - total
        if gap >= 0 and gap % 5 == 0:
            padding = b"\x00\x00\x00\xff\xff" * (gap // 5)
            return header + head + padding + final + adler
    return None


_LEVELS = [9, 6, 1, 2, 3, 4, 5, 7, 8]


def _stored_zlib(raw):
    return b"\x78\x01" + _stored_blocks(raw, True) + struct.pack(">I", zlib.adler32(raw))


_SIG = b"\x00" * 128

# Files the digest lists with compressed length == size + 129 (payload == size) are stored
# uncompressed. The first trailer byte probably says how the payload is encoded; 0x01 works for
# zlib payloads, but the client rejected raw bytes + 0x01. These are tried in order, one per
# retry (the client re-requests with a growing cb= suffix after a rejected download).
STORED_VARIANTS = [
    ("raw+trailer00", lambda raw: raw + b"\x00" + _SIG),
    ("raw+trailer02", lambda raw: raw + b"\x02" + _SIG),
    ("raw+trailer03", lambda raw: raw + b"\x03" + _SIG),
    ("raw+trailer04", lambda raw: raw + b"\x04" + _SIG),
    ("raw+trailer80", lambda raw: raw + b"\x80" + _SIG),
    ("raw+trailerff", lambda raw: raw + b"\xff" + _SIG),
    ("zlibstored+trailer01", lambda raw: _stored_zlib(raw) + b"\x01" + _SIG),
]


def encode_stored(index, raw):
    label, make = STORED_VARIANTS[index % len(STORED_VARIANTS)]
    return make(raw), label


def _zs_payload(raw, target=None):
    """zlib stream, or the raw bytes when zlib does not make the file smaller.

    ``target`` is the payload length the manifest implies (compressed length
    minus the trailer). When given, prefer whichever zlib level reproduces it;
    failing that, pad a valid zlib stream to exactly that length.
    """
    if target is not None and target == len(raw):
        return bytes(raw)
    default = zlib.compress(raw, 9)
    if target is not None:
        for level in list(_LEVELS):
            candidate = zlib.compress(raw, level)
            if len(candidate) == target:
                if _LEVELS[0] != level:  # try the level that worked last time first
                    _LEVELS.remove(level)
                    _LEVELS.insert(0, level)
                return candidate
        padded = pad_zlib(raw, target)
        if padded is not None:
            return padded
    return default if len(default) < len(raw) else bytes(raw)


def _zs_digest_trailer(raw, target=None):
    if _digest_trailer is None:
        raise RuntimeError("digest trailer not available; start the server with a valid digest")
    return _zs_payload(raw, target) + _digest_trailer


ZS_CODECS = {
    # trailer = 0x01 followed by 128 zero bytes (tests: is the trailer checked at all?)
    "zs-zlib-zero-trailer": lambda raw, target=None: (
        _zs_payload(raw, target) + b"\x01" + b"\x00" * 128),
    # trailer copied from the digest (well-formed-looking bytes, wrong content)
    "zs-zlib-digest-trailer": _zs_digest_trailer,
}


def codec_names():
    return (list(ZS_CODECS)
            + [base + prefix for base in BASE_CODECS for prefix in PREFIXES])


def encode(name, raw, payload_target=None):
    if name in ZS_CODECS:
        return ZS_CODECS[name](raw, payload_target)
    for prefix, make_prefix in PREFIXES.items():
        if prefix and name.endswith(prefix):
            base = name[: -len(prefix)]
            if base in BASE_CODECS:
                return make_prefix(raw) + BASE_CODECS[base](raw)
    if name in BASE_CODECS:
        return BASE_CODECS[name](raw)
    raise KeyError(f"unknown codec candidate: {name}")
