# Derived from RPCS3 (rpcs3/Crypto/unedat.cpp, GPL-2.0-or-later). Test patch: makes the Lua loading-finished checks
# in the decrypted scriptsbase chunk always answer "done", then re-packs it as an SDAT with stored blocks and valid CMACs.
# Needs luadis.py, the decrypted s.bin (tools/undat.py) and the original sdat. Usage: python3 sdat_patch_loading.py in.sdat out.sdat
"""Patch the decrypted Lua chunk and re-pack it as an SDAT (stored uncompressed blocks, valid per-block CMAC)."""
import struct, sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.cmac import CMAC
SDAT_KEY = bytes.fromhex("0D655EF8E674A98AB8505CFA7D012933")
EDAT_KEY_0 = bytes.fromhex("BE959CA8308DEFA2E5E180C63712A9AE")
def ecb_enc(k, x): e = Cipher(algorithms.AES(k), modes.ECB()).encryptor(); return e.update(x) + e.finalize()
def cbc_dec(k, iv, x): e = Cipher(algorithms.AES(k), modes.CBC(iv)).decryptor(); return e.update(x) + e.finalize()
def cbc_enc(k, iv, x): e = Cipher(algorithms.AES(k), modes.CBC(iv)).encryptor(); return e.update(x) + e.finalize()
def cmac(k, x): c = CMAC(algorithms.AES(k)); c.update(x); return c.finalize()

# locate code arrays in the plain chunk
exec(open('luadis.py').read().split("if __name__")[0].replace("def func(path, parent):\n    src = string(); ld = i32(); lld = i32(); nups = u8(); npar = u8(); va = u8(); ms = u8()\n    n = i32(); code = list(struct.unpack_from('>%dI' % n, d, pos)); globals()['pos'] += 4*n",
 "def func(path, parent):\n    src = string(); ld = i32(); lld = i32(); nups = u8(); npar = u8(); va = u8(); ms = u8()\n    n = i32(); code_off = pos; code = list(struct.unpack_from('>%dI' % n, d, pos)); globals()['pos'] += 4*n").replace("f = dict(path=path,","f = dict(code_off=code_off, path=path,"))
plain = bytearray(open('s.bin','rb').read())
def enc_abc(op, a, b, c): return op | (a << 6) | (c << 14) | (b << 23)
def enc_abx(op, a, bx): return op | (a << 6) | (bx << 14)
LOADK, LOADBOOL, RETURN = 1, 2, 30
def patch(path, first, kidx=None):
    f = [x for x in funcs if x['path'] == path][0]
    a = enc_abx(LOADK, 1, kidx) if first == 'num' else enc_abc(LOADBOOL, 1, 1, 0)
    b = enc_abc(RETURN, 1, 2, 0)
    struct.pack_into('>II', plain, f['code_off'], a, b)
    print('patched', path, 'at', hex(f['code_off']))
f = [x for x in funcs if x['path'] == [13, 12]][0]
patch([13, 12], 'num', f['ks'].index(1.0))
patch([12, 107], 'bool'); patch([12, 21], 'bool')

src = open(sys.argv[1], 'rb').read()
ver = struct.unpack('>I', src[4:8])[0]; digest = src[0x40:0x50]; dev_hash = src[0x60:0x70]
flags, bsz, fsize = struct.unpack('>IIQ', src[0x80:0x90])
assert fsize == len(plain), (fsize, len(plain))
dec_key = bytes(a ^ b for a, b in zip(dev_hash, SDAT_KEY))
nblocks = (fsize + bsz - 1) // bsz
out = bytearray(src[:0x100]); out += bytes(0x20 * nblocks)
meta = []
for b in range(nblocks):
    chunk = bytes(plain[b*bsz:(b+1)*bsz]); plen = len(chunk)
    chunk += bytes((-plen) % 16); length = len(chunk)
    key_result = ecb_enc(dec_key, dev_hash[:12] + struct.pack('>i', b))
    key_final = cbc_dec(EDAT_KEY_0, bytes(16), key_result)
    cipher = cbc_enc(key_final, digest, chunk)
    h = cmac(key_final, cipher)
    offset = len(out); out += cipher
    dec = struct.pack('>Qii', offset, plen, 0)
    m = bytearray(32); m[:16] = h
    m[0x10] = dec[0x0] ^ m[0xC] ^ m[0x8]; m[0x11] = dec[0x1] ^ m[0xD] ^ m[0x9]
    m[0x12] = dec[0x2] ^ m[0xE] ^ m[0xA]; m[0x13] = dec[0x3] ^ m[0xF] ^ m[0xB]
    m[0x14] = dec[0x4] ^ m[0x4] ^ m[0x8]; m[0x15] = dec[0x5] ^ m[0x5] ^ m[0x9]
    m[0x16] = dec[0x6] ^ m[0x6] ^ m[0xA]; m[0x17] = dec[0x7] ^ m[0x7] ^ m[0xB]
    m[0x18] = dec[0x8] ^ m[0xC] ^ m[0x0]; m[0x19] = dec[0x9] ^ m[0xD] ^ m[0x1]
    m[0x1A] = dec[0xA] ^ m[0xE] ^ m[0x2]; m[0x1B] = dec[0xB] ^ m[0xF] ^ m[0x3]
    m[0x1C] = dec[0xC] ^ m[0x4] ^ m[0x0]; m[0x1D] = dec[0xD] ^ m[0x5] ^ m[0x1]
    m[0x1E] = dec[0xE] ^ m[0x6] ^ m[0x2]; m[0x1F] = dec[0xF] ^ m[0x7] ^ m[0x3]
    out[0x100 + b*0x20: 0x120 + b*0x20] = m
open(sys.argv[2], 'wb').write(bytes(out)); print('wrote', sys.argv[2], len(out))
