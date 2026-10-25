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


def codec_names():
    return [base + prefix for base in BASE_CODECS for prefix in PREFIXES]


def encode(name, raw):
    for prefix, make_prefix in PREFIXES.items():
        if prefix and name.endswith(prefix):
            base = name[: -len(prefix)]
            if base in BASE_CODECS:
                return make_prefix(raw) + BASE_CODECS[base](raw)
    if name in BASE_CODECS:
        return BASE_CODECS[name](raw)
    raise KeyError(f"unknown codec candidate: {name}")
