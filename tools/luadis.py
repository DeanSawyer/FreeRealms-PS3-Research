# Dump functions of a Lua 5.1 big-endian chunk (the decrypted scriptsbase.bin.sdat) that use a given constant.
# Usage: python3 -I luadis.py IsLoadingFinished [dis]   (reads s.bin in the current directory)
import struct, sys
d = open('s.bin','rb').read()
pos = 12
OPS = "MOVE LOADK LOADBOOL LOADNIL GETUPVAL GETGLOBAL GETTABLE SETGLOBAL SETUPVAL SETTABLE NEWTABLE SELF ADD SUB MUL DIV MOD POW UNM NOT LEN CONCAT JMP EQ LT LE TEST TESTSET CALL TAILCALL RETURN FORLOOP FORPREP TFORLOOP SETLIST CLOSE CLOSURE VARARG".split()
def i32():
    global pos; v = struct.unpack_from('>i', d, pos)[0]; pos += 4; return v
def u8():
    global pos; v = d[pos]; pos += 1; return v
def string():
    global pos; n = struct.unpack_from('>I', d, pos)[0]; pos += 4
    if n == 0: return None
    s = d[pos:pos+n-1]; pos += n; return s.decode('latin1')
funcs = []
def func(path, parent):
    src = string(); ld = i32(); lld = i32(); nups = u8(); npar = u8(); va = u8(); ms = u8()
    n = i32(); code = list(struct.unpack_from('>%dI' % n, d, pos)); globals()['pos'] += 4*n
    nk = i32(); ks = []
    for _ in range(nk):
        t = u8()
        if t == 0: ks.append(None)
        elif t == 1: ks.append(bool(u8()))
        elif t == 3:
            ks.append(struct.unpack_from('>d', d, pos)[0]); globals()['pos'] += 8
        elif t == 4: ks.append(string())
        else: raise Exception('bad const %d at %d' % (t, pos))
    f = dict(path=path, ld=ld, lld=lld, nups=nups, npar=npar, code=code, ks=ks, parent=parent, kids=[])
    funcs.append(f)
    np_ = i32()
    for k in range(np_):
        f['kids'].append(func(path + [k], f))
    nl = i32(); globals()['pos'] += 4*nl
    nv = i32()
    f['locs'] = []
    for _ in range(nv):
        nm = string(); a = i32(); b = i32(); f['locs'].append((nm, a, b))
    nu = i32(); f['upn'] = [string() for _ in range(nu)]
    return f
top = func([], None)
def rk(f, x):
    if x >= 256:
        k = f['ks'][x-256]; return 'K(%r)' % (k,)
    return 'R%d' % x
def dis(f, out):
    for pc, ins in enumerate(f['code']):
        op = ins & 0x3f; a = (ins >> 6) & 0xff; c = (ins >> 14) & 0x1ff; b = (ins >> 23) & 0x1ff
        bx = (ins >> 14) & 0x3ffff; sbx = bx - 131071
        nm = OPS[op]
        if nm in ('LOADK',): s = 'R%d = %r' % (a, f['ks'][bx])
        elif nm in ('GETGLOBAL',): s = 'R%d = _G.%s' % (a, f['ks'][bx])
        elif nm in ('SETGLOBAL',): s = '_G.%s = R%d' % (f['ks'][bx], a)
        elif nm == 'GETTABLE': s = 'R%d = R%d[%s]' % (a, b, rk(f, c))
        elif nm == 'SETTABLE': s = 'R%d[%s] = %s' % (a, rk(f, b), rk(f, c))
        elif nm == 'SELF': s = 'R%d = R%d[%s]; R%d = R%d' % (a, b, rk(f, c), a+1, b)
        elif nm == 'CALL': s = 'CALL R%d args=%d rets=%d' % (a, b-1, c-1)
        elif nm in ('JMP',): s = 'JMP -> %d' % (pc + 1 + sbx)
        elif nm in ('EQ', 'LT', 'LE'): s = '%s if (%s %s %s) ~= %d' % (nm, rk(f, b), nm, rk(f, c), a)
        elif nm == 'TEST': s = 'TEST R%d c=%d' % (a, c)
        elif nm == 'TESTSET': s = 'TESTSET R%d R%d c=%d' % (a, b, c)
        elif nm == 'CLOSURE': s = 'R%d = CLOSURE proto %d' % (a, bx)
        elif nm == 'LOADBOOL': s = 'R%d = %s skip=%d' % (a, bool(b), c)
        elif nm in ('GETUPVAL', 'SETUPVAL'): s = '%s R%d U%d (%s)' % (nm, a, b, f['upn'][b] if b < len(f['upn']) else '?')
        elif nm in ('MOVE', 'UNM', 'NOT', 'LEN'): s = '%s R%d R%d' % (nm, a, b)
        elif nm in ('ADD', 'SUB', 'MUL', 'DIV', 'MOD', 'POW'): s = 'R%d = %s %s %s' % (a, rk(f, b), nm, rk(f, c))
        elif nm == 'RETURN': s = 'RETURN R%d n=%d' % (a, b-1)
        else: s = '%s a=%d b=%d c=%d' % (nm, a, b, c)
        out.append('  %3d %s' % (pc, s))
if __name__ == '__main__':
    key = sys.argv[1]
    for f in funcs:
        if key in f['ks']:
            print('=== func path', f['path'], 'lines', f['ld'], f['lld'], 'params', f['npar'], 'upvals', f['upn'])
            print('  consts:', [k for k in f['ks'] if isinstance(k, str)][:40])
            if len(sys.argv) > 2:
                o = []; dis(f, o); print('\n'.join(o))
