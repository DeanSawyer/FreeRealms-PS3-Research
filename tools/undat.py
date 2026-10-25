# Derived from RPCS3 (rpcs3/Crypto/unedat.cpp and lz.cpp, GPL-2.0-or-later; LZ code by Hykem).
# Decrypts the client's own SDAT files (for example ui/scriptsbase.bin.sdat) so the Lua scripts can be read.
"""Decrypt an SDAT/EDAT file (NPD version 2/3, SDAT flag), following RPCS3's unedat.cpp and lz.cpp (GPL-2.0+).
Usage: python3 undat.py in.sdat out.bin"""
import struct, sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

SDAT_KEY = bytes.fromhex("0D655EF8E674A98AB8505CFA7D012933")
EDAT_KEY_0 = bytes.fromhex("BE959CA8308DEFA2E5E180C63712A9AE")
EDAT_KEY_1 = bytes.fromhex("4CA9C14B01C95309969BEC68AA0BC081")

def ecb_enc(key, data):
    e = Cipher(algorithms.AES(key), modes.ECB()).encryptor(); return e.update(data) + e.finalize()
def cbc_dec(key, iv, data):
    d = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor(); return d.update(data) + d.finalize()

M = 0xFFFFFFFF
class St:
    def __init__(s, inb):
        s.inb = inb; s.pos = 0; s.range = M
        s.code = (inb[1] << 24) | (inb[2] << 16) | (inb[3] << 8) | inb[4]
        s.tmp = bytearray(0xCC8); s.tmp[:0xCA8] = b"\x80" * 0xCA8

def drange(st):
    if not (st.range >> 24):
        st.range = (st.range << 8) & M
        b = st.inb[st.pos + 5] if st.pos + 5 < len(st.inb) else 0
        st.code = ((st.code << 8) + b) & M
        st.pos += 1

def dbit(st, ia, ci):
    drange(st)
    c = st.tmp[ci]
    val = (st.range >> 8) * c
    st.tmp[ci] = (c - (c >> 3)) & 0xFF
    if ia is not None: ia[0] <<= 1
    if st.code < val:
        st.range = val; st.tmp[ci] = (st.tmp[ci] + 31) & 0xFF
        if ia is not None: ia[0] += 1
        return 1
    st.code -= val; st.range -= val
    return 0

def dnumber(st, ptr, index):
    i = [1]
    if index >= 3:
        dbit(st, i, ptr + 0x18)
        if index >= 4:
            dbit(st, i, ptr + 0x18)
            if index >= 5:
                drange(st)
                while index >= 5:
                    i[0] <<= 1; st.range >>= 1
                    if st.code < st.range: i[0] += 1
                    else: st.code -= st.range
                    index -= 1
    flag = dbit(st, i, ptr)
    if index >= 1:
        dbit(st, i, ptr + 8)
        if index >= 2: dbit(st, i, ptr + 0x10)
    return i[0], flag

def dword(st, ptr, index):
    i = [1]; index //= 8
    if index >= 3:
        dbit(st, i, ptr + 4)
        if index >= 4:
            dbit(st, i, ptr + 4)
            if index >= 5:
                drange(st)
                while index >= 5:
                    i[0] <<= 1; st.range >>= 1
                    if st.code < st.range: i[0] += 1
                    else: st.code -= st.range
                    index -= 1
    flag = dbit(st, i, ptr)
    if index >= 1:
        dbit(st, i, ptr + 1)
        if index >= 2: dbit(st, i, ptr + 2)
    return i[0], flag

def decompress(inb, size):
    out = bytearray(size)
    head = inb[0]
    st = St(inb)
    if head > 0x80:
        return None
    offset = 0; start = 0; prev = 0
    while True:
        ts1 = offset + 0xB68
        if not dbit(st, None, ts1):
            if offset > 0: offset -= 1
            if start == size: return bytes(out[:start])
            sect = ((((((start & 7) << 8) + prev) >> head) & 7) * 0xFF) - 1
            idx = [1]
            while True:
                dbit(st, idx, sect + idx[0])
                if (idx[0] >> 8) != 0: break
            out[start] = idx[0] & 0xFF; start += 1
        else:
            index = -1
            while True:
                ts1 += 8
                flag = dbit(st, None, ts1); index += flag
                if not (flag != 0 and index < 6): break
            bsize = 0x160
            ts2 = index + 0x7F1
            if index >= 0 or flag != 0:
                sect = (index << 5) | ((((start << index) & 3)) << 3) | (offset & 7)
                data_length, flag = dnumber(st, 0xBA8 + sect, index)
                if data_length == 0xFF: return bytes(out[:start])
            else:
                data_length = 1
            if data_length <= 2:
                ts2 += 0xF8; bsize = 0x40
            shift = [1]
            while True:
                diff = (shift[0] << 4) - bsize
                flag = dbit(st, shift, ts2 + (shift[0] << 3))
                if not (diff < 0): break
            if diff > 0 or flag != 0:
                if flag == 0: diff -= 8
                data_offset, flag = dword(st, 0x928 + diff, diff)
            else:
                data_offset = 1
            bs = start - data_offset; be = start + data_length + 1
            if bs < 0 or be > size: return None
            offset = (((be) + 1) & 1) + 6
            while True:
                out[start] = out[bs]; start += 1; bs += 1
                if start >= be: break
        prev = out[start - 1]

def main(src, dst):
    d = open(src, "rb").read()
    magic, ver, lic, typ = struct.unpack(">4sIII", d[:16])
    digest = d[0x40:0x50]; dev_hash = d[0x60:0x70]
    flags, bsz, fsize = struct.unpack(">IIQ", d[0x80:0x90])
    print("magic", magic, "ver", ver, "lic", lic, "flags", hex(flags), "block", bsz, "size", fsize)
    assert magic == b"NPD\0" and flags & 0x01000000, "only SDAT handled"
    dec_key = bytes(a ^ b for a, b in zip(dev_hash, SDAT_KEY))
    nblocks = (fsize + bsz - 1) // bsz
    msz = 0x20 if flags & 1 or flags & 0x20 else 0x10
    out = bytearray(); fails = 0
    for b in range(nblocks):
        mo = 0x100 + b * msz
        md = d[mo:mo + 0x20]
        if flags & 1:
            dec = bytearray(16)
            m = md
            dec[0x0] = m[0xC] ^ m[0x8] ^ m[0x10]; dec[0x1] = m[0xD] ^ m[0x9] ^ m[0x11]
            dec[0x2] = m[0xE] ^ m[0xA] ^ m[0x12]; dec[0x3] = m[0xF] ^ m[0xB] ^ m[0x13]
            dec[0x4] = m[0x4] ^ m[0x8] ^ m[0x14]; dec[0x5] = m[0x5] ^ m[0x9] ^ m[0x15]
            dec[0x6] = m[0x6] ^ m[0xA] ^ m[0x16]; dec[0x7] = m[0x7] ^ m[0xB] ^ m[0x17]
            dec[0x8] = m[0xC] ^ m[0x0] ^ m[0x18]; dec[0x9] = m[0xD] ^ m[0x1] ^ m[0x19]
            dec[0xA] = m[0xE] ^ m[0x2] ^ m[0x1A]; dec[0xB] = m[0xF] ^ m[0x3] ^ m[0x1B]
            dec[0xC] = m[0x4] ^ m[0x0] ^ m[0x1C]; dec[0xD] = m[0x5] ^ m[0x1] ^ m[0x1D]
            dec[0xE] = m[0x6] ^ m[0x2] ^ m[0x1E]; dec[0xF] = m[0x7] ^ m[0x3] ^ m[0x1F]
            offset, length, cend = struct.unpack(">Qii", bytes(dec))
        else:
            offset = 0x100 + b * bsz + nblocks * msz; length = bsz; cend = 0
            if b == nblocks - 1 and fsize % bsz: length = fsize % bsz
        plen = length; length = (plen + 15) & ~15
        enc = d[offset:offset + length]
        bkey = (dev_hash[:12] if ver > 1 else bytes(12)) + struct.pack(">i", b)
        key_result = ecb_enc(dec_key, bkey)
        key_final = key_result; iv = digest if ver > 1 else bytes(16)
        if flags & 0x8:  # encrypted ERK
            key_final = cbc_dec(EDAT_KEY_1 if ver == 4 else EDAT_KEY_0, bytes(16), key_result)
        plain = cbc_dec(key_final, iv, enc)
        if (flags & 1) and cend:
            r = decompress(plain, bsz)
            if r is None: fails += 1; r = b""
            out += r
        else:
            out += plain[:plen]
    print("blocks", nblocks, "decompress failures", fails, "out bytes", len(out))
    open(dst, "wb").write(bytes(out[:fsize]))

main(sys.argv[1], sys.argv[2])
