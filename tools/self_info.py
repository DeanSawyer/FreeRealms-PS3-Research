#!/usr/bin/env python3
"""Print the header of a PS3 SELF/EBOOT file: key revision, type and NPDRM license info.

Use it to see why RPCS3 reports "Failed to decrypt SELF metadata info": an NPDRM SELF whose
license type is "network" or "local" needs a license file (.rap) for your account; a "free" one
does not. Read-only; prints metadata only (no keys, no decrypted content).

Layout follows RPCS3's SELF.h / the public PS3 developer wiki:
  0x00 SCE header   magic "SCE\\0", header version, key revision, type, meta offset, header size, data size
  0x20 SELF header  header type, offsets of app info / ELF / program headers / ... / control info
  app info          authority id, vendor id, SELF type, version
  control info      type 1 flags, 2 digest, 3 NPDRM (magic "NPD\\0", license type, app type, content id)

Usage:
    python tools/self_info.py game.self [EBOOT.BIN ...]
"""

import struct
import sys
from pathlib import Path

SELF_TYPES = {1: "LV0", 2: "LV1", 3: "LV2", 4: "application", 5: "isolated SPU module",
              6: "secure loader", 7: "secure SPU module"}
LICENSE_TYPES = {1: "network (needs RAP/rif)", 2: "local (needs RAP/rif)", 3: "free (no RAP needed)"}
NPD_APP_TYPES = {0: "module (SPRX)", 1: "executable (EBOOT/SELF)", 2: "other"}


def u32(data, off):
    return struct.unpack_from(">I", data, off)[0]


def u64(data, off):
    return struct.unpack_from(">Q", data, off)[0]


def describe(path):
    data = Path(path).read_bytes()
    print(f"== {path}  ({len(data)} bytes)")
    print("first 32 bytes:", data[:32].hex(" "))
    if data[:4] != b"SCE\x00":
        print("  not a SELF: magic is", data[:4].hex(), "(expected 53 43 45 00)")
        if data[:4] == b"\x7fELF":
            print("  this is a plain ELF (unencrypted)")
        return
    if len(data) < 0xA0:
        print("  too short for a SELF header")
        return
    hver, flags, stype = u32(data, 4), struct.unpack_from(">H", data, 8)[0], struct.unpack_from(">H", data, 10)[0]
    meta_off, hsize, esize = u32(data, 12), u64(data, 16), u64(data, 24)
    print(f"  SCE header: version {hver}, key revision 0x{flags:04x}, type {stype} (1 = SELF), "
          f"metadata offset 0x{meta_off:x}, header size 0x{hsize:x}, data size {esize}")
    (htype, appinfo, elf, phdr, shdr, secinfo, sceve, control, controlsize, _pad) = struct.unpack_from(">10Q", data, 0x20)
    print(f"  SELF header: type {htype}, app info @0x{appinfo:x}, ELF @0x{elf:x}, control info @0x{control:x} "
          f"(size 0x{controlsize:x})")
    if appinfo + 0x20 <= len(data):
        authid, vendor, self_type, version = struct.unpack_from(">QIIQ", data, appinfo)
        print(f"  app info: auth id 0x{authid:016x}, vendor 0x{vendor:08x}, "
              f"SELF type {self_type} ({SELF_TYPES.get(self_type, 'unknown')}), version 0x{version:016x}")
    pos, end = control, min(control + controlsize, len(data))
    while pos + 0x10 <= end:
        ctype, csize = u32(data, pos), u32(data, pos + 4)
        body = pos + 0x10
        if ctype == 3 and body + 0x80 <= len(data):
            magic = data[body:body + 4]
            ver, lic, app = u32(data, body + 4), u32(data, body + 8), u32(data, body + 12)
            content = data[body + 16:body + 16 + 0x30].split(b"\x00")[0].decode("latin-1")
            print(f"  NPDRM control info: magic {magic!r}, version {ver}, "
                  f"license type {lic} = {LICENSE_TYPES.get(lic, 'unknown')}, "
                  f"app type {app} = {NPD_APP_TYPES.get(app, 'unknown')}")
            print(f"    content id: {content}")
        else:
            print(f"  control info type {ctype}, size 0x{csize:x}")
        if csize < 0x10 or csize > end - pos:
            break
        pos += csize
    if control == 0:
        print("  (no control info table)")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    for path in sys.argv[1:]:
        try:
            describe(path)
        except OSError as exc:
            print(f"{path}: {exc}")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
