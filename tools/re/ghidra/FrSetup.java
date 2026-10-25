// Free Realms PS3 (NPUA30048 build 1.77) game.elf: prepare the Ghidra program.
//
// game.elf is a decrypted PS3 PPU executable (PowerPC64 big endian, 32-bit pointers). Function pointers are
// 8-byte descriptors {code, toc} in the .opd area at 0xeeb530 (0x2c228 bytes, 22597 entries, one shared TOC).
// This script
//   0. rewrites the Cell-only vector loads lvlx / lvrx (and their lvlxl / lvrxl / stvlx / stvrx forms) to the plain
//      AltiVec lvx / stvx with the same registers. Ghidra's PowerPC language does not know them, so it stops
//      disassembling at the first one and every function that contains one is cut off there (about 470 places
//      in this executable). Only the Ghidra copy changes; each patched address gets an end-of-line comment with
//      the original word. The registers and the control flow are right, the loaded bytes of an unaligned load are not.
//   1. sets r2 (the TOC) to the shared value over the whole code range,
//   2. creates a function for every distinct code address named by the descriptors,
//   3. labels each descriptor "opd_<addr>" so references to function pointers resolve in the listing.
// Run it before auto-analysis (analyzeHeadless -preScript) so the analyzers start from known entries.
//
// @category FreeRealms
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.SourceType;

import java.math.BigInteger;
import java.util.TreeSet;

public class FrSetup extends GhidraScript {
    static final long CODE_LO = 0x10230L, CODE_HI = 0xdad304L;
    static final long OPD = 0xeeb530L, OPD_SIZE = 0x2c228L;
    static final long TOC = 0xf1f758L;

    @Override
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        fixCellVectorOps(mem);
        Register r2 = currentProgram.getRegister("r2");
        Address lo = toAddr(CODE_LO), hi = toAddr(CODE_HI - 1);
        currentProgram.getProgramContext().setValue(r2, lo, hi, BigInteger.valueOf(TOC));

        TreeSet<Long> entries = new TreeSet<>();
        for (long a = OPD; a < OPD + OPD_SIZE; a += 8) {
            Address d = toAddr(a);
            long code = mem.getInt(d) & 0xffffffffL;
            long toc = mem.getInt(d.add(4)) & 0xffffffffL;
            if (toc != TOC || code < CODE_LO || code >= CODE_HI || (code & 3) != 0) {
                continue;
            }
            entries.add(code);
            try {
                currentProgram.getSymbolTable().createLabel(d, String.format("opd_%08x", a), SourceType.IMPORTED);
            } catch (Exception e) {
                // label clashes are harmless
            }
        }
        println("descriptor entries: " + entries.size());
        int made = 0;
        for (long code : entries) {
            Address a = toAddr(code);
            if (getFunctionAt(a) != null) {
                continue;
            }
            disassemble(a);
            Function f = createFunction(a, String.format("fn_%08x", code));
            if (f != null) {
                made++;
            }
        }
        println("functions created: " + made);
    }

    /** Cell BE vector load/store forms (primary opcode 31) and the plain AltiVec form that replaces each. */
    private static int plainForm(int xo) {
        switch (xo) {
            case 519: case 551: return 103;   // lvlx, lvrx   -> lvx
            case 775: case 807: return 359;   // lvlxl, lvrxl -> lvxl
            case 647: case 679: return 231;   // stvlx, stvrx -> stvx
            case 903: case 935: return 487;   // stvlxl, stvrxl -> stvxl
            default: return -1;
        }
    }

    private void fixCellVectorOps(Memory mem) throws Exception {
        int fixed = 0;
        for (long a = CODE_LO; a < CODE_HI; a += 4) {
            Address ad = toAddr(a);
            int w = mem.getInt(ad);
            if ((w >>> 26) != 31 || (w & 1) != 0) {
                continue;
            }
            int plain = plainForm((w >>> 1) & 0x3ff);
            if (plain < 0) {
                continue;
            }
            mem.setInt(ad, (w & ~(0x3ff << 1)) | (plain << 1));
            setEOLComment(ad, String.format("Cell BE vector op, file word %08x (shown as the plain AltiVec form)", w));
            fixed++;
        }
        println("Cell vector loads/stores rewritten: " + fixed);
    }
}
