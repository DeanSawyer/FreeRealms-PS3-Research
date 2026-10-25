"""Read, edit and re-serialize the PS3 digest (NPUA30048.soe).

Layout (see docs/digest-format.md): zlib stream + 129-byte trailer. The decompressed body is
a tag/length/value tree. Length prefix: < 0x80 one byte; 0x80-0xFE two bytes (15 bits);
0xFF four more bytes. Top level: header fields, then tag 2 = root directory. Inside a
directory: tag 1 name, tag 2 child directory, tag 3 file record, tag 0xFE child counts.
Inside a file record: tag 1 name, 2 compressed length, 3 size, 4 CRC32, 8 timestamp, 6 flag.

Round-trips byte-for-byte when nothing is edited (checked by ``serialize(parse(b)) == b``).
Standard library only.
"""

import struct
import zlib

TRAILER_LEN = 129
HEADER_TOTAL_KIB = 21  # header tag 21 = sum of all file sizes // 1024 (verified against the digest)


class Node:
    __slots__ = ("tag", "kind", "value", "children", "width")

    def __init__(self, tag, kind, value=None, children=None, width=1):
        self.tag = tag
        self.kind = kind          # "leaf", "dir", "file"
        self.value = value        # bytes for leaves
        self.children = children  # list of Node for containers
        self.width = width        # length-prefix width as found in the source (1, 2 or 5)


def _read_len(data, pos):
    b = data[pos]
    if b < 0x80:
        return b, 1
    if b == 0xFF:
        return int.from_bytes(data[pos + 1:pos + 5], "big"), 5
    return ((b & 0x7F) << 8) | data[pos + 1], 2


def _write_len(length, min_width=1):
    if length < 0x80 and min_width <= 1:
        return bytes([length])
    if length <= 0x7EFF and min_width <= 2:
        return bytes([0x80 | (length >> 8), length & 0xFF])
    return b"\xff" + struct.pack(">I", length)


def _parse_seq(data, start, end, context):
    nodes, pos = [], start
    while pos < end:
        tag = data[pos]
        length, width = _read_len(data, pos + 1)
        value_start = pos + 1 + width
        value_end = value_start + length
        if value_end > end:
            raise ValueError(f"TLV overruns its parent at offset {pos}")
        if context == "top" and tag == 2:
            nodes.append(Node(tag, "dir", children=_parse_seq(data, value_start, value_end, "dir"),
                              width=width))
        elif context == "dir" and tag == 2:
            nodes.append(Node(tag, "dir", children=_parse_seq(data, value_start, value_end, "dir"),
                              width=width))
        elif context == "dir" and tag == 3:
            nodes.append(Node(tag, "file", children=_parse_seq(data, value_start, value_end, "file"),
                              width=width))
        else:
            nodes.append(Node(tag, "leaf", value=bytes(data[value_start:value_end]), width=width))
        pos = value_end
    return nodes


def parse(body):
    return _parse_seq(body, 0, len(body), "top")


def _serialize_node(node):
    if node.kind == "leaf":
        payload = node.value
    else:
        payload = b"".join(_serialize_node(child) for child in node.children)
    return bytes([node.tag]) + _write_len(len(payload), node.width) + payload


def serialize(nodes):
    return b"".join(_serialize_node(node) for node in nodes)


def _name(node):
    for child in node.children:
        if child.tag == 1 and child.kind == "leaf":
            return child.value.rstrip(b"\x00").decode("latin-1")
    return ""


def iter_files(nodes):
    """Yield (path, file_node) for every file record, in digest order."""
    def walk(directory, prefix):
        name = _name(directory)
        base = prefix + (name + "/" if name else "")
        for child in directory.children:
            if child.kind == "dir":
                yield from walk(child, base)
            elif child.kind == "file":
                yield base + _name(child), child

    for top in nodes:
        if top.kind == "dir":
            yield from walk(top, "")


def get_field(file_node, tag):
    for child in file_node.children:
        if child.tag == tag and child.kind == "leaf":
            return int.from_bytes(child.value, "big")
    return None


def set_field(file_node, tag, value):
    """Set a numeric field, keeping its byte width when the new value still fits."""
    for child in file_node.children:
        if child.tag == tag and child.kind == "leaf":
            width = max(len(child.value), (value.bit_length() + 7) // 8, 1)
            child.value = value.to_bytes(width, "big")
            return
    raise KeyError(f"file record has no tag {tag}")


def set_header_number(nodes, tag, value):
    for node in nodes:
        if node.tag == tag and node.kind == "leaf":
            width = max(len(node.value), (value.bit_length() + 7) // 8, 1)
            node.value = value.to_bytes(width, "big")
            return True
    return False


def split_digest(blob):
    d = zlib.decompressobj()
    body = d.decompress(blob)
    if not d.eof:
        raise ValueError("digest zlib stream is incomplete")
    return body, d.unused_data


def pack_digest(body, trailer):
    return zlib.compress(body, 9) + trailer


def payload_length(data):
    """Payload size the .zs container uses for ``data``: zlib level 9, or raw if that is not smaller."""
    packed = len(zlib.compress(data, 9))
    return packed if packed < len(data) else len(data)


CHUNK = 4 * 1024 * 1024
TEXT_FIX_LIMIT = 64 * 1024 * 1024


def downloadable(nodes):
    """Yield (path, file_node) for records the client downloads (they carry tags 2, 3 and 4)."""
    for path, node in iter_files(nodes):
        if all(get_field(node, tag) is not None for tag in (2, 3, 4)):
            yield path, node


def _crc32_file(path):
    crc = 0
    with path.open("rb") as handle:
        while block := handle.read(CHUNK):
            crc = zlib.crc32(block, crc)
    return crc


def adapt_to_local(body, usrdir):
    """Describe the local build: rewrite size/CRC/length of records whose local file differs.

    A record is left alone when the local file is missing (the client will download it), already
    matches, or matches after LF->CRLF conversion (the server converts those on request).
    Returns (new_body, changes) where each change is a dict.
    """
    tree = parse(body)
    changes = []
    for path, node in downloadable(tree):
        local = usrdir / path
        if not local.is_file():
            continue
        want_size, want_crc = get_field(node, 3), get_field(node, 4)
        size = local.stat().st_size
        crc = _crc32_file(local) if size == want_size else None
        if crc == want_crc:
            continue
        if size != want_size and size <= TEXT_FIX_LIMIT and want_size <= TEXT_FIX_LIMIT:
            fixed = local.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
            if len(fixed) == want_size and zlib.crc32(fixed) == want_crc:
                continue
        data = local.read_bytes()
        new_crc = zlib.crc32(data)
        new_length = payload_length(data) + TRAILER_LEN
        changes.append({"path": path, "old_size": want_size, "new_size": size,
                        "old_crc32": want_crc, "new_crc32": new_crc,
                        "old_length": get_field(node, 2), "new_length": new_length})
        set_field(node, 3, size)
        set_field(node, 4, new_crc)
        set_field(node, 2, new_length)
    if changes:
        total = sum(get_field(node, 3) for _, node in downloadable(tree))
        set_header_number(tree, HEADER_TOTAL_KIB, total // 1024)
    return serialize(tree), changes
