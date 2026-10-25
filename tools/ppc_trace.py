#!/usr/bin/env python3
"""Run a function from the decrypted PS3 client (game.elf) in a small PowerPC emulator and
record every read it makes from an input buffer.

Purpose: the client prints no debug output, so the layout of a network packet can only be found
by reading its deserializer. Reading 1,000-line PowerPC functions by hand is slow; running them on
a buffer and watching which bytes they read, in which order and how wide, shows the layout directly.

Only the subset of PowerPC the client's deserializers use is implemented. Calls to the allocator
and other unknown helpers can be stubbed (--stub ADDR[=VALUE]). This is a research aid, not a
general emulator; unsupported instructions stop the run with a message.

Example (the SendSelfToClient player blob; args are r3 and r4 after the object/reader are set up):
    python3 tools/ppc_trace.py game.elf 0xb1e460 --reader --object 33200
"""

import argparse
import struct
import sys

M32 = 0xFFFFFFFF
M64 = 0xFFFFFFFFFFFFFFFF
PAGE = 0x1000
SENTINEL = 0xDEAD0000
BUF_BASE = 0x20000000
READER_ADDR = 0x7FE00000
STACK_TOP = 0x7FD00000
HEAP_BASE = 0x30000000
TOC_DEFAULT = 0xF1F758


def sext(v, bits):
    v &= (1 << bits) - 1
    return v - (1 << bits) if v >> (bits - 1) else v


def mask64(mb, me):
    """PowerPC MASK(mb, me) over 64 bits, bit 0 being the most significant."""
    if mb <= me:
        return ((M64 >> mb) & (M64 << (63 - me))) & M64
    return ((M64 >> mb) | (M64 << (63 - me))) & M64


def rotl64(v, n):
    n &= 63
    return ((v << n) | (v >> (64 - n))) & M64 if n else v


def rotl32(v, n):
    n &= 31
    v &= M32
    return ((v << n) | (v >> (32 - n))) & M32 if n else v


class Stop(Exception):
    pass


class Memory:
    def __init__(self):
        self.pages = {}
        self.unmapped_reads = 0
        self.null_reads = []   # (address) of reads below 0x1000: what the real console reports as "reading location 0x0"

    def page(self, addr, create=True):
        key = addr >> 12
        pg = self.pages.get(key)
        if pg is None and create:
            pg = self.pages[key] = bytearray(PAGE)
        return pg

    def load(self, addr, data):
        for i in range(0, len(data), PAGE):
            pass
        pos = 0
        while pos < len(data):
            pg = self.page(addr + pos)
            off = (addr + pos) & 0xFFF
            n = min(PAGE - off, len(data) - pos)
            pg[off:off + n] = data[pos:pos + n]
            pos += n

    def read(self, addr, n):
        out = bytearray()
        while n:
            pg = self.page(addr, create=False)
            off = addr & 0xFFF
            take = min(PAGE - off, n)
            if pg is None:
                self.unmapped_reads += 1
                out += bytes(take)
            else:
                out += pg[off:off + take]
            addr += take
            n -= take
        return bytes(out)

    def write(self, addr, data):
        self.load(addr, data)


class Emu:
    def __init__(self, elf_path, buffer, trace_calls=True):
        self.mem = Memory()
        self.gpr = [0] * 32
        self.fpr = [0.0] * 32
        self.cr = 0
        self.lr = 0
        self.ctr = 0
        self.ca = 0
        self.pc = 0
        self.stubs = {}
        self.heap = HEAP_BASE
        self.buf_len = len(buffer)
        self.events = []       # (kind, ...) in execution order
        self.shadow = []
        self.steps = 0
        self.trace_calls = trace_calls
        self.reserve = None
        self.unsupported = None
        self.history = []
        self.text_writes = 0
        self.advances = []
        self.calls = []
        self.null_calls = 0
        self.load_elf(elf_path)
        self.mem.write(BUF_BASE, bytes(buffer))
        self.buf_writes = 0

    # --- loading ---------------------------------------------------------------------
    def load_elf(self, path):
        d = open(path, "rb").read()
        phoff = struct.unpack(">Q", d[0x20:0x28])[0]
        entsize, num = struct.unpack(">HH", d[0x36:0x3A])
        for i in range(num):
            o = phoff + i * entsize
            t, fl, off, va, pa, fsz, msz, al = struct.unpack(">IIQQQQQQ", d[o:o + 56])
            if t == 1 and fsz:
                self.mem.load(va, d[off:off + fsz])
        self.entry_toc = struct.unpack(">I", d[struct.unpack(">Q", d[0x18:0x20])[0] - 0x10000 + 4:][:4])[0]
        self.text = (0x10230, 0x10230 + 0xD9D0D4)

    def alloc(self, n):
        n = (n + 15) & ~15
        a = self.heap
        self.heap += max(n, 16) + 16
        return a

    # --- memory helpers --------------------------------------------------------------
    def lread(self, ea, n):
        ea &= M32
        if ea < 0x1000:
            self.mem.null_reads.append((self.pc, ea, n))
        if BUF_BASE <= ea < BUF_BASE + self.buf_len:
            self.events.append(("read", self.pc, ea - BUF_BASE, n, tuple(self.shadow[-4:])))
        data = self.mem.read(ea, n)
        return int.from_bytes(data, "big")

    def lwrite(self, ea, n, v):
        ea &= M32
        if self.text[0] <= ea < self.text[1]:     # never let emulated code overwrite itself
            self.text_writes += 1
            return
        if n == 4:
            new = v & M32
            if BUF_BASE <= new <= BUF_BASE + self.buf_len:
                old = int.from_bytes(self.mem.read(ea, 4), "big")
                if BUF_BASE <= old <= BUF_BASE + self.buf_len and 0 < new - old <= 0x10000:
                    self.advances.append((old - BUF_BASE, new - old, self.pc, tuple(self.shadow[-4:])))
        self.mem.write(ea, (v & ((1 << (8 * n)) - 1)).to_bytes(n, "big"))

    def u32(self, addr):
        return struct.unpack(">I", self.mem.read(addr, 4))[0]

    # --- condition register ---------------------------------------------------------
    def set_cr_field(self, f, lt, gt, eq, so=0):
        shift = 28 - 4 * f
        v = (lt << 3) | (gt << 2) | (eq << 1) | so
        self.cr = (self.cr & ~(0xF << shift)) & M32 | (v << shift)

    def cr_bit(self, bi):
        return (self.cr >> (31 - bi)) & 1

    def record_cr0(self, val):
        s = sext(val, 64)
        self.set_cr_field(0, int(s < 0), int(s > 0), int(s == 0))

    def branch_cond(self, bo, bi):
        ctr_ok = True
        if not (bo & 4):
            self.ctr = (self.ctr - 1) & M64
            ctr_ok = ((self.ctr != 0) ^ bool(bo & 2))
        cond_ok = True
        if not (bo & 16):
            cond_ok = (self.cr_bit(bi) == ((bo >> 3) & 1))
        return ctr_ok and cond_ok

    # --- execution -------------------------------------------------------------------
    def run(self, entry, max_steps=3_000_000):
        self.pc = entry
        self.lr = SENTINEL
        while self.steps < max_steps:
            if self.pc == SENTINEL:
                return "returned"
            if self.pc in self.stubs:
                self.do_stub(self.pc)
                continue
            if not (self.text[0] <= self.pc < self.text[1]):
                self.null_calls += 1
                if self.null_calls > 200:
                    return "too many null calls"
                self.gpr[3] = 0
                self.pc = self.lr & M32
                if self.shadow:
                    self.shadow.pop()
                continue
            self.steps += 1
            self.history.append(self.pc)
            if len(self.history) > 400:
                self.history.pop(0)
            try:
                self.step()
            except Stop as exc:
                return f"stopped: {exc}"
        return "step limit"

    def do_stub(self, addr):
        kind, val = self.stubs[addr]
        if kind == "alloc":
            self.gpr[3] = self.alloc(self.gpr[3] & M32)
        elif kind == "ret":
            self.gpr[3] = val & M64
        self.pc = self.lr & M32
        if self.shadow:
            self.shadow.pop()

    def step(self):
        pc = self.pc
        w = self.u32(pc)
        op = w >> 26
        g = self.gpr
        rd = (w >> 21) & 31
        ra = (w >> 16) & 31
        rb = (w >> 11) & 31
        rc = w & 1
        si = sext(w, 16)
        ui = w & 0xFFFF
        nxt = (pc + 4) & M32

        def ea_d(zero_ra=True):
            base = 0 if (ra == 0 and zero_ra) else g[ra]
            return (base + si) & M64

        if op == 14:    # addi
            g[rd] = ((0 if ra == 0 else g[ra]) + si) & M64
        elif op == 15:  # addis
            g[rd] = ((0 if ra == 0 else g[ra]) + (si << 16)) & M64
        elif op == 12 or op == 13:   # addic, addic.
            a = g[ra]
            r = a + (si & M64)
            self.ca = int(r > M64)
            g[rd] = r & M64
            if op == 13:
                self.record_cr0(g[rd])
        elif op == 8:   # subfic
            r = (si & M64) + ((~g[ra]) & M64) + 1
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif op == 7:   # mulli
            g[rd] = (sext(g[ra], 64) * si) & M64
        elif op == 24:  # ori
            g[ra] = g[rd] | ui
        elif op == 25:  # oris
            g[ra] = g[rd] | (ui << 16)
        elif op == 26:  # xori
            g[ra] = g[rd] ^ ui
        elif op == 27:  # xoris
            g[ra] = g[rd] ^ (ui << 16)
        elif op == 28:  # andi.
            g[ra] = g[rd] & ui
            self.record_cr0(g[ra])
        elif op == 29:  # andis.
            g[ra] = g[rd] & (ui << 16)
            self.record_cr0(g[ra])
        elif op == 11:  # cmpi
            f = (w >> 23) & 7
            a = sext(g[ra], 64) if (w >> 21) & 1 else sext(g[ra], 32)
            self.set_cr_field(f, int(a < si), int(a > si), int(a == si))
        elif op == 10:  # cmpli
            f = (w >> 23) & 7
            a = g[ra] if (w >> 21) & 1 else g[ra] & M32
            self.set_cr_field(f, int(a < ui), int(a > ui), int(a == ui))
        elif op == 32:  # lwz
            g[rd] = self.lread(ea_d(), 4)
        elif op == 33:  # lwzu
            ea = ea_d(False)
            g[rd] = self.lread(ea, 4)
            g[ra] = ea
        elif op == 34:  # lbz
            g[rd] = self.lread(ea_d(), 1)
        elif op == 35:  # lbzu
            ea = ea_d(False)
            g[rd] = self.lread(ea, 1)
            g[ra] = ea
        elif op == 40:  # lhz
            g[rd] = self.lread(ea_d(), 2)
        elif op == 41:  # lhzu
            ea = ea_d(False)
            g[rd] = self.lread(ea, 2)
            g[ra] = ea
        elif op == 42:  # lha
            g[rd] = sext(self.lread(ea_d(), 2), 16) & M64
        elif op == 36:  # stw
            self.lwrite(ea_d(), 4, g[rd])
        elif op == 37:  # stwu
            ea = ea_d(False)
            self.lwrite(ea, 4, g[rd])
            g[ra] = ea
        elif op == 38:  # stb
            self.lwrite(ea_d(), 1, g[rd])
        elif op == 39:  # stbu
            ea = ea_d(False)
            self.lwrite(ea, 1, g[rd])
            g[ra] = ea
        elif op == 44:  # sth
            self.lwrite(ea_d(), 2, g[rd])
        elif op == 45:  # sthu
            ea = ea_d(False)
            self.lwrite(ea, 2, g[rd])
            g[ra] = ea
        elif op == 46:  # lmw
            ea = ea_d()
            for r in range(rd, 32):
                g[r] = self.lread(ea, 4)
                ea += 4
        elif op == 47:  # stmw
            ea = ea_d()
            for r in range(rd, 32):
                self.lwrite(ea, 4, g[r])
                ea += 4
        elif op == 48:  # lfs
            self.fpr[rd] = struct.unpack(">f", self.lread(ea_d(), 4).to_bytes(4, "big"))[0]
        elif op == 49:  # lfsu
            ea = ea_d(False)
            self.fpr[rd] = struct.unpack(">f", self.lread(ea, 4).to_bytes(4, "big"))[0]
            g[ra] = ea
        elif op == 50:  # lfd
            self.fpr[rd] = struct.unpack(">d", self.lread(ea_d(), 8).to_bytes(8, "big"))[0]
        elif op == 51:  # lfdu
            ea = ea_d(False)
            self.fpr[rd] = struct.unpack(">d", self.lread(ea, 8).to_bytes(8, "big"))[0]
            g[ra] = ea
        elif op == 52:  # stfs
            self.lwrite(ea_d(), 4, int.from_bytes(struct.pack(">f", self.safe_f32(self.fpr[rd])), "big"))
        elif op == 53:  # stfsu
            ea = ea_d(False)
            self.lwrite(ea, 4, int.from_bytes(struct.pack(">f", self.safe_f32(self.fpr[rd])), "big"))
            g[ra] = ea
        elif op == 54:  # stfd
            self.lwrite(ea_d(), 8, int.from_bytes(struct.pack(">d", self.fpr[rd]), "big"))
        elif op == 55:  # stfdu
            ea = ea_d(False)
            self.lwrite(ea, 8, int.from_bytes(struct.pack(">d", self.fpr[rd]), "big"))
            g[ra] = ea
        elif op == 58:  # ld, ldu, lwa
            ds = sext(w & 0xFFFC, 16)
            base = 0 if ra == 0 else g[ra]
            ea = (base + ds) & M64
            sub = w & 3
            if sub == 0:
                g[rd] = self.lread(ea, 8)
            elif sub == 1:
                g[rd] = self.lread(ea, 8)
                g[ra] = ea
            else:
                g[rd] = sext(self.lread(ea, 4), 32) & M64
        elif op == 62:  # std, stdu
            ds = sext(w & 0xFFFC, 16)
            base = 0 if ra == 0 else g[ra]
            ea = (base + ds) & M64
            self.lwrite(ea, 8, g[rd])
            if w & 3 == 1:
                g[ra] = ea
        elif op == 18:  # b, bl, ba, bla
            li = sext(w & 0x03FFFFFC, 26)
            target = li if (w & 2) else (pc + li)
            target &= M32
            if w & 1:
                self.lr = nxt
                self.shadow.append(target)
                self.calls.append((pc, target, self.gpr[3] & M32, self.gpr[4] & M32))
            nxt = target
        elif op == 16:  # bc
            bo, bi = rd, ra
            bd = sext(w & 0xFFFC, 16)
            if w & 1:
                self.lr = nxt
            if self.branch_cond(bo, bi):
                nxt = (bd if (w & 2) else pc + bd) & M32
        elif op == 19:
            xo = (w >> 1) & 0x3FF
            if xo == 16:    # bclr
                tgt = self.lr & ~3 & M32
                if self.branch_cond(rd, ra):
                    if self.shadow:
                        self.shadow.pop()
                    if w & 1:
                        self.lr = nxt
                    nxt = tgt
            elif xo == 528:  # bcctr
                tgt = self.ctr & ~3 & M32
                if self.branch_cond(rd | 4, ra):   # bcctr never decrements
                    if w & 1:
                        self.lr = nxt
                        self.shadow.append(tgt)
                    nxt = tgt
            elif xo in (150,):   # isync
                pass
            elif xo in (193, 449, 289, 33, 129, 225, 257, 417):   # cr logical
                a = self.cr_bit(ra)
                b = self.cr_bit(rb)
                res = {193: a ^ b, 449: a | b, 289: int(a == b), 33: int(not (a | b)),
                       129: a & (1 - b), 225: int(not (a & b)), 257: a & b, 417: a | (1 - b)}[xo]
                sh = 31 - rd
                self.cr = (self.cr & ~(1 << sh)) | (res << sh)
            else:
                raise Stop(f"unsupported op19 xo={xo} at {pc:#x}")
        elif op == 20 or op == 21 or op == 23:   # rlwimi, rlwinm, rlwnm
            sh = rb if op != 23 else (g[rb] & 31)
            mb = ((w >> 6) & 31) + 32
            me = ((w >> 1) & 31) + 32
            r32 = rotl32(g[rd], sh)
            r = r32 | (r32 << 32)
            m = mask64(mb, me)
            if op == 20:
                g[ra] = (r & m) | (g[ra] & ~m & M64)
            else:
                g[ra] = r & m
            if rc:
                self.record_cr0(g[ra])
        elif op == 30:
            sub = (w >> 2) & 7
            sh = rb | (((w >> 1) & 1) << 5)
            mbf = ((w >> 6) & 31) | (((w >> 5) & 1) << 5)
            if sub == 0:     # rldicl
                g[ra] = rotl64(g[rd], sh) & mask64(mbf, 63)
            elif sub == 1:   # rldicr
                g[ra] = rotl64(g[rd], sh) & mask64(0, mbf)
            elif sub == 2:   # rldic
                g[ra] = rotl64(g[rd], sh) & mask64(mbf, 63 - sh)
            elif sub == 3:   # rldimi
                m = mask64(mbf, 63 - sh)
                g[ra] = (rotl64(g[rd], sh) & m) | (g[ra] & ~m & M64)
            else:            # rldcl / rldcr
                n = g[rb] & 63
                if (w >> 1) & 0xF == 8:
                    g[ra] = rotl64(g[rd], n) & mask64(mbf, 63)
                else:
                    g[ra] = rotl64(g[rd], n) & mask64(0, mbf)
            if rc:
                self.record_cr0(g[ra])
        elif op == 31:
            self.op31(w, rd, ra, rb, rc)
        elif op == 17:  # sc
            g[3] = 0
        elif op == 63 or op == 59:
            self.fp_ops(w, op, rd, ra, rb)
        elif op == 3 or op == 2:  # twi / tdi
            raise Stop(f"trap at {pc:#x}")
        else:
            raise Stop(f"unsupported opcode {op} at {pc:#x} (word {w:#010x})")
        self.pc = nxt

    @staticmethod
    def safe_f32(x):
        try:
            struct.pack(">f", x)
            return x
        except OverflowError:
            return float("inf") if x > 0 else float("-inf")

    def fp_ops(self, w, op, rd, ra, rb):
        f = self.fpr
        xo = (w >> 1) & 0x3FF
        xo5 = (w >> 1) & 0x1F
        frc = (w >> 6) & 31
        if op in (59, 63) and xo5 == 18:      # fdiv(s)
            f[rd] = f[ra] / f[rb] if f[rb] else 0.0
        elif xo5 == 20:
            f[rd] = f[ra] - f[rb]
        elif xo5 == 21:
            f[rd] = f[ra] + f[rb]
        elif xo5 == 25:
            f[rd] = f[ra] * f[frc]
        elif xo5 == 29:
            f[rd] = f[ra] * f[frc] + f[rb]
        elif xo5 == 28:
            f[rd] = f[ra] * f[frc] - f[rb]
        elif xo5 == 31:
            f[rd] = -(f[ra] * f[frc] + f[rb])
        elif xo5 == 30:
            f[rd] = -(f[ra] * f[frc] - f[rb])
        elif xo5 == 23:
            f[rd] = f[frc] if f[ra] >= 0 else f[rb]
        elif op == 63 and xo in (72,):          # fmr
            f[rd] = f[rb]
        elif op == 63 and xo in (40,):          # fneg
            f[rd] = -f[rb]
        elif op == 63 and xo in (264,):         # fabs
            f[rd] = abs(f[rb])
        elif op == 63 and xo in (12,):          # frsp
            f[rd] = f[rb]
        elif op == 63 and xo in (15, 14):       # fctiwz / fctiw
            f[rd] = float(int(f[rb])) if abs(f[rb]) < 2 ** 31 else 0.0
        elif op == 63 and xo in (0, 32):        # fcmpu / fcmpo
            a, b = f[ra], f[rb]
            self.set_cr_field(rd >> 2, int(a < b), int(a > b), int(a == b))
        elif op == 63 and xo in (583, 711, 70, 38, 134):   # mffs, mtfsf, mtfsb0/1, mtfsfi
            pass
        elif op == 63 and xo in (814, 815, 846):  # fctid, fctidz, fcfid
            f[rd] = float(int(f[rb])) if xo != 846 else float(sext(int(f[rb]) & M64, 64))
        else:
            raise Stop(f"unsupported fp op {op} xo={xo} at {self.pc:#x}")

    def op31(self, w, rd, ra, rb, rc):
        g = self.gpr
        xo = (w >> 1) & 0x3FF
        pc = self.pc
        # XO-form arithmetic (9-bit extended opcode, OE ignored)
        x9 = xo & 0x1FF
        if x9 == 266:   # add
            g[rd] = (g[ra] + g[rb]) & M64
        elif x9 == 40:  # subf
            g[rd] = (g[rb] - g[ra]) & M64
        elif x9 == 104:  # neg
            g[rd] = (-g[ra]) & M64
        elif x9 == 235:  # mullw
            g[rd] = (sext(g[ra], 32) * sext(g[rb], 32)) & M64
        elif x9 == 233:  # mulld
            g[rd] = (sext(g[ra], 64) * sext(g[rb], 64)) & M64
        elif x9 == 75:   # mulhw
            g[rd] = ((sext(g[ra], 32) * sext(g[rb], 32)) >> 32) & M32
        elif x9 == 11:   # mulhwu
            g[rd] = (((g[ra] & M32) * (g[rb] & M32)) >> 32) & M32
        elif x9 == 491:  # divw
            a, b = sext(g[ra], 32), sext(g[rb], 32)
            g[rd] = (int(a / b) & M32) if b else 0
        elif x9 == 459:  # divwu
            a, b = g[ra] & M32, g[rb] & M32
            g[rd] = (a // b) if b else 0
        elif x9 == 489:  # divd
            a, b = sext(g[ra], 64), sext(g[rb], 64)
            g[rd] = (int(a / b) & M64) if b else 0
        elif x9 == 457:  # divdu
            a, b = g[ra], g[rb]
            g[rd] = (a // b) if b else 0
        elif x9 == 10:   # addc
            r = g[ra] + g[rb]
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif x9 == 138:  # adde
            r = g[ra] + g[rb] + self.ca
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif x9 == 202:  # addze
            r = g[ra] + self.ca
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif x9 == 234:  # addme
            r = g[ra] + M64 + self.ca
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif x9 == 8:    # subfc
            r = (~g[ra] & M64) + g[rb] + 1
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif x9 == 136:  # subfe
            r = (~g[ra] & M64) + g[rb] + self.ca
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif x9 == 200:  # subfze
            r = (~g[ra] & M64) + self.ca
            self.ca = int(r > M64)
            g[rd] = r & M64
        elif xo == 0 or xo == 32:   # cmp, cmpl
            f = (w >> 23) & 7
            L = (w >> 21) & 1
            if xo == 0:
                a = sext(g[ra], 64) if L else sext(g[ra], 32)
                b = sext(g[rb], 64) if L else sext(g[rb], 32)
            else:
                a = g[ra] if L else g[ra] & M32
                b = g[rb] if L else g[rb] & M32
            self.set_cr_field(f, int(a < b), int(a > b), int(a == b))
            return
        elif xo == 28:   # and
            g[ra] = g[rd] & g[rb]
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 444:  # or
            g[ra] = g[rd] | g[rb]
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 316:  # xor
            g[ra] = g[rd] ^ g[rb]
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 124:  # nor
            g[ra] = ~(g[rd] | g[rb]) & M64
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 60:   # andc
            g[ra] = g[rd] & ~g[rb] & M64
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 412:  # orc
            g[ra] = (g[rd] | ~g[rb]) & M64
            return
        elif xo == 284:  # eqv
            g[ra] = ~(g[rd] ^ g[rb]) & M64
            return
        elif xo == 476:  # nand
            g[ra] = ~(g[rd] & g[rb]) & M64
            return
        elif xo == 24:   # slw
            n = g[rb] & 0x3F
            g[ra] = (g[rd] << n) & M32 if n < 32 else 0
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 536:  # srw
            n = g[rb] & 0x3F
            g[ra] = (g[rd] & M32) >> n if n < 32 else 0
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 792 or xo == 824:   # sraw, srawi
            n = (g[rb] & 0x3F) if xo == 792 else rb
            v = sext(g[rd], 32)
            if n > 31:
                g[ra] = (-1 if v < 0 else 0) & M64
                self.ca = int(v < 0)
            else:
                g[ra] = (v >> n) & M64
                self.ca = int(v < 0 and (v & ((1 << n) - 1)) != 0)
            if rc:
                self.record_cr0(g[ra])
            return
        elif xo == 27:   # sld
            n = g[rb] & 0x7F
            g[ra] = (g[rd] << n) & M64 if n < 64 else 0
            return
        elif xo == 539:  # srd
            n = g[rb] & 0x7F
            g[ra] = g[rd] >> n if n < 64 else 0
            return
        elif xo == 794 or xo == 826 or xo == 827:   # srad, sradi
            n = (g[rb] & 0x7F) if xo == 794 else (rb | (((w >> 1) & 1) << 5))
            v = sext(g[rd], 64)
            g[ra] = (v >> min(n, 63)) & M64
            self.ca = int(v < 0 and (v & ((1 << min(n, 63)) - 1)) != 0)
            return
        elif xo == 954:  # extsb
            g[ra] = sext(g[rd], 8) & M64
        elif xo == 922:  # extsh
            g[ra] = sext(g[rd], 16) & M64
        elif xo == 986:  # extsw
            g[ra] = sext(g[rd], 32) & M64
        elif xo == 26:   # cntlzw
            v = g[rd] & M32
            g[ra] = 32 - v.bit_length()
        elif xo == 58:   # cntlzd
            g[ra] = 64 - g[rd].bit_length()
        elif xo in (23, 55, 87, 119, 279, 311, 343, 375, 21, 53, 20, 84, 534, 790, 535, 599):  # loads
            base = 0 if ra == 0 else g[ra]
            if xo in (55, 119, 311, 53, 375):
                base = g[ra]
            ea = (base + g[rb]) & M64
            if xo in (23, 55):
                g[rd] = self.lread(ea, 4)
            elif xo in (87, 119):
                g[rd] = self.lread(ea, 1)
            elif xo in (279, 311):
                g[rd] = self.lread(ea, 2)
            elif xo in (343, 375):
                g[rd] = sext(self.lread(ea, 2), 16) & M64
            elif xo in (21, 53):
                g[rd] = self.lread(ea, 8)
            elif xo == 20:   # lwarx
                g[rd] = self.lread(ea, 4)
                self.reserve = ea
            elif xo == 84:   # ldarx
                g[rd] = self.lread(ea, 8)
                self.reserve = ea
            elif xo == 534:  # lwbrx
                g[rd] = int.from_bytes(self.lread(ea, 4).to_bytes(4, "big"), "little")
            elif xo == 790:  # lhbrx
                g[rd] = int.from_bytes(self.lread(ea, 2).to_bytes(2, "big"), "little")
            elif xo == 535:  # lfsx
                self.fpr[rd] = struct.unpack(">f", self.lread(ea, 4).to_bytes(4, "big"))[0]
            elif xo == 599:  # lfdx
                self.fpr[rd] = struct.unpack(">d", self.lread(ea, 8).to_bytes(8, "big"))[0]
            if xo in (55, 119, 311, 375, 53):
                g[ra] = ea
            return
        elif xo in (151, 183, 215, 247, 407, 439, 149, 181, 662, 918, 663, 727, 150, 214):  # stores
            base = 0 if ra == 0 else g[ra]
            if xo in (183, 247, 439, 181):
                base = g[ra]
            ea = (base + g[rb]) & M64
            if xo in (151, 183):
                self.lwrite(ea, 4, g[rd])
            elif xo in (215, 247):
                self.lwrite(ea, 1, g[rd])
            elif xo in (407, 439):
                self.lwrite(ea, 2, g[rd])
            elif xo in (149, 181):
                self.lwrite(ea, 8, g[rd])
            elif xo == 662:  # stwbrx
                self.lwrite(ea, 4, int.from_bytes((g[rd] & M32).to_bytes(4, "big"), "little"))
            elif xo == 918:  # sthbrx
                self.lwrite(ea, 2, int.from_bytes((g[rd] & 0xFFFF).to_bytes(2, "big"), "little"))
            elif xo == 663:
                self.lwrite(ea, 4, int.from_bytes(struct.pack(">f", self.safe_f32(self.fpr[rd])), "big"))
            elif xo == 727:
                self.lwrite(ea, 8, int.from_bytes(struct.pack(">d", self.fpr[rd]), "big"))
            elif xo == 150:  # stwcx.
                self.lwrite(ea, 4, g[rd])
                self.set_cr_field(0, 0, 0, 1)
                return
            elif xo == 214:  # stdcx.
                self.lwrite(ea, 8, g[rd])
                self.set_cr_field(0, 0, 0, 1)
                return
            if xo in (183, 247, 439, 181):
                g[ra] = ea
            return
        elif xo == 339:  # mfspr
            spr = ((w >> 16) & 31) | (((w >> 11) & 31) << 5)
            g[rd] = {8: self.lr, 9: self.ctr, 1: self.ca << 29}.get(spr, 0)
            return
        elif xo == 467:  # mtspr
            spr = ((w >> 16) & 31) | (((w >> 11) & 31) << 5)
            if spr == 8:
                self.lr = g[rd]
            elif spr == 9:
                self.ctr = g[rd]
            return
        elif xo == 19:   # mfcr
            g[rd] = self.cr
            return
        elif xo == 144:  # mtcrf / mtocrf
            fxm = (w >> 12) & 0xFF
            val = g[rd] & M32
            for i in range(8):
                if fxm & (0x80 >> i):
                    sh = 28 - 4 * i
                    self.cr = (self.cr & ~(0xF << sh)) | (val & (0xF << sh))
            return
        elif xo == 512:  # mcrxr
            return
        elif xo == 371:  # mftb
            g[rd] = self.steps
            return
        elif xo in (598, 854, 86, 982, 278, 246, 54, 470, 566):   # sync, eieio, dcbf, icbi, dcbt, dcbtst, dcbst, dcbi, tlbsync
            return
        elif xo == 1014:  # dcbz
            ea = ((0 if ra == 0 else g[ra]) + g[rb]) & M64
            ea &= ~127
            self.mem.write(ea, bytes(128))
            return
        elif xo == 4 or xo == 68:    # tw / td
            return
        else:
            raise Stop(f"unsupported op31 xo={xo} at {pc:#x} (word {w:#010x})")
        if rc:
            self.record_cr0(g[rd] if x9 in (266, 40, 104, 235, 233, 491, 459, 489, 457, 10, 138, 202, 234, 8, 136, 200, 75, 11) else g[ra])


def setup_reader(emu, length):
    """Reader object as built by the client: base, length, cur, end, ok flag."""
    b = BUF_BASE
    emu.mem.write(READER_ADDR, struct.pack(">IIIIBB", b, length, b, b + length, 1, 0))
    return READER_ADDR


def format_events(emu, min_depth=0):
    """Group consecutive reads by call path so a layout can be read off."""
    out = []
    last = None
    for ev in emu.events:
        _, pc, off, n, stack = ev
        key = stack[-2:] if stack else ()
        out.append((off, n, pc, stack))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("elf")
    ap.add_argument("func", type=lambda s: int(s, 0))
    ap.add_argument("--buffer-size", type=int, default=0x4000)
    ap.add_argument("--set", action="append", default=[], metavar="OFF=HEXBYTES",
                    help="put bytes into the input buffer, e.g. 8=01000000 (little-endian count)")
    ap.add_argument("--reader", action="store_true", help="pass a reader object in r4 (r3 = object)")
    ap.add_argument("--reader-r3", action="store_true", help="pass the reader in r3 instead")
    ap.add_argument("--object", type=int, default=0, help="allocate a zeroed object of N bytes for r3")
    ap.add_argument("--r", action="append", default=[], metavar="N=VALUE", help="set register rN")
    ap.add_argument("--stub", action="append", default=[], metavar="ADDR[=VALUE]",
                    help="return immediately from ADDR with r3=VALUE; ADDR=alloc makes it an allocator")
    ap.add_argument("--max-steps", type=int, default=3_000_000)
    ap.add_argument("--file", help="use this file as the input buffer (overrides --buffer-size)")
    ap.add_argument("--calls", type=int, default=0, help="print the last N call sites")
    ap.add_argument("--raw", action="store_true", help="print every read instead of a compact layout")
    args = ap.parse_args()

    buf = bytearray(open(args.file, "rb").read()) if args.file else bytearray(args.buffer_size)
    for s in args.set:
        off, _, hexs = s.partition("=")
        data = bytes.fromhex(hexs)
        buf[int(off, 0):int(off, 0) + len(data)] = data
    emu = Emu(args.elf, buf)
    for s in args.stub:
        addr, _, val = s.partition("=")
        if val == "alloc":
            emu.stubs[int(addr, 0)] = ("alloc", 0)
        else:
            emu.stubs[int(addr, 0)] = ("ret", int(val, 0) if val else 0)
    emu.gpr[1] = STACK_TOP
    emu.gpr[2] = emu.entry_toc
    reader = setup_reader(emu, len(buf))
    obj = emu.alloc(args.object) if args.object else 0
    if args.reader_r3:
        emu.gpr[3] = reader
    elif args.reader:
        emu.gpr[3], emu.gpr[4] = obj, reader
    else:
        emu.gpr[3] = obj
    for s in args.r:
        n, _, v = s.partition("=")
        emu.gpr[int(n)] = int(v, 0)
    result = emu.run(args.func, args.max_steps)
    if args.calls:
        for src, dst, r3, r4 in emu.calls[-args.calls:]:
            print(f"call {src:#x} -> {dst:#x}  r3={r3:#x} r4={r4:#x}")
    jumps = [(a, b) for a, b in zip(emu.history, emu.history[1:]) if b != a + 4]
    print("# last jumps (from -> to):", " ".join(f"{a:#x}->{b:#x}" for a, b in jumps[-12:]))
    print(f"# r3 at exit = {emu.gpr[3] & M32:#x}; last pcs: " + " ".join(f"{p:#x}" for p in emu.history[-16:]))
    if result.startswith("stopped"):
        print("# last pcs:", " ".join(f"{p:#x}" for p in emu.history[-12:]))
    print(f"# {result} after {emu.steps} instructions, {len(emu.events)} buffer reads, "
          f"{emu.mem.unmapped_reads} unmapped reads, final pc {emu.pc:#x}")
    if args.raw:
        for _, pc, off, n, stack in emu.events:
            print(f"off {off:#06x} size {n}  pc {pc:#x}  via {[hex(s) for s in stack]}")
        return
    # Field view: the client advances a reader's cursor after every primitive it reads, so each
    # advance of a pointer inside the input buffer is exactly one field (offset, width).
    for off, size, pc, stack in emu.advances:
        print(f"{off:#06x} +{size:<4} pc={pc:#x} via {' > '.join(hex(x) for x in stack)}")
    if emu.advances:
        end = max(o + n for o, n, _, _ in emu.advances)
        print(f"# consumed {end} of {len(buf)} bytes")


if __name__ == "__main__":
    sys.exit(main())
