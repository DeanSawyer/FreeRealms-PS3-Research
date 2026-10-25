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

DEFAULT_CODEC = "lzma-alone"  # the preserved baseline experiment


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


# --- Observed .zs container (see docs/digest-format.md) -----------------------
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


def _zs_payload(raw):
    packed = zlib.compress(raw, 9)
    return packed if len(packed) < len(raw) else bytes(raw)


def _zs_digest_trailer(raw):
    if _digest_trailer is None:
        raise RuntimeError("digest trailer not available; start the server with a valid digest")
    return _zs_payload(raw) + _digest_trailer


ZS_CODECS = {
    # trailer = 0x01 followed by 128 zero bytes (tests: is the trailer checked at all?)
    "zs-zlib-zero-trailer": lambda raw: _zs_payload(raw) + b"\x01" + b"\x00" * 128,
    # trailer copied from the digest (well-formed-looking bytes, wrong content)
    "zs-zlib-digest-trailer": _zs_digest_trailer,
}


def codec_names():
    return (list(ZS_CODECS)
            + [base + prefix for base in BASE_CODECS for prefix in PREFIXES])


def encode(name, raw):
    if name in ZS_CODECS:
        return ZS_CODECS[name](raw)
    for prefix, make_prefix in PREFIXES.items():
        if prefix and name.endswith(prefix):
            base = name[: -len(prefix)]
            if base in BASE_CODECS:
                return make_prefix(raw) + BASE_CODECS[base](raw)
    if name in BASE_CODECS:
        return BASE_CODECS[name](raw)
    raise KeyError(f"unknown codec candidate: {name}")
