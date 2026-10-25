#!/usr/bin/env python3
"""Find the lists inside a packet by trying a count of 1 in every 4-byte field.

Runs a deserializer from game.elf (see ppc_trace.py) on an all-zero buffer, then once per 4-byte field with
that field set to 1. A field that is a list count makes the function read extra bytes (one element); those
extra reads are printed. Fields that are plain integers change nothing and are skipped.

    python3 tools/ppc_explore.py game.elf 0xb1e460 --reader --object 33200 \
        --stub 0xa44aac=alloc --stub 0x11834=alloc --stub 0x1dbe4=alloc
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ppc_trace as pt  # noqa: E402


def run(elf, func, buf, stubs, reader, obj_size, max_steps):
    emu = pt.Emu(elf, buf)
    for s in stubs:
        addr, _, val = s.partition("=")
        emu.stubs[int(addr, 0)] = ("alloc", 0) if val == "alloc" else ("ret", int(val, 0) if val else 0)
    emu.gpr[1] = pt.STACK_TOP
    emu.gpr[2] = emu.entry_toc
    rd = pt.setup_reader(emu, len(buf))
    obj = emu.alloc(obj_size) if obj_size else 0
    if reader:
        emu.gpr[3], emu.gpr[4] = obj, rd
    else:
        emu.gpr[3] = obj
    result = emu.run(func, max_steps)
    return emu, result


def fields(emu):
    return [(o, n, pc, st) for o, n, pc, st in emu.advances]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("elf")
    ap.add_argument("func", type=lambda s: int(s, 0))
    ap.add_argument("--reader", action="store_true")
    ap.add_argument("--object", type=int, default=0)
    ap.add_argument("--stub", action="append", default=[])
    ap.add_argument("--buffer-size", type=int, default=8192)
    ap.add_argument("--max-steps", type=int, default=3_000_000)
    ap.add_argument("--base", help="hex bytes to start from (instead of zeros), e.g. a partly built packet")
    ap.add_argument("--only", action="append", default=[], type=lambda s: int(s, 0),
                    help="only test these field offsets")
    args = ap.parse_args()

    base = bytearray(args.buffer_size)
    if args.base:
        data = bytes.fromhex(args.base)
        base[:len(data)] = data
    emu0, res0 = run(args.elf, args.func, bytes(base), args.stub, args.reader, args.object, args.max_steps)
    b = fields(emu0)
    print(f"# baseline: {res0}; {len(b)} fields, consumed up to {max((o + n for o, n, _, _ in b), default=0):#x}")
    for i, (off, size, pc, st) in enumerate(b):
        if size != 4 or (args.only and off not in args.only):
            continue
        buf = bytearray(base)
        buf[off:off + 4] = (1).to_bytes(4, "little")
        emu, res = run(args.elf, args.func, bytes(buf), args.stub, args.reader, args.object, args.max_steps)
        a = fields(emu)
        ta = [(n, p, s) for _, n, p, s in a]
        tb = [(n, p, s) for _, n, p, s in b]
        k = 0
        while k < len(tb) - (i + 1) and k < len(ta) - (i + 1) and ta[-1 - k] == tb[-1 - k]:
            k += 1
        extra = a[i + 1:len(a) - k]
        if extra and ta[:i + 1] == tb[:i + 1]:
            total = sum(n for _, n, _, _ in extra)
            layout = " ".join(f"{n}" for _, n, _, _ in extra)
            print(f"field {off:#06x} (via {' > '.join(hex(x) for x in st)}): count -> {len(extra)} reads, {total} bytes: [{layout}]")
        elif ta != tb and not extra:
            print(f"field {off:#06x}: changes the run ({res}) but adds no reads")


if __name__ == "__main__":
    main()
