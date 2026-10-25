// Keep the analysed game.elf open and answer queries, so each question takes a second instead of a 30 s program load.
//
// Start (read-only, nothing is saved back to the project):
//   analyzeHeadless <projdir> FR -process game.elf -noanalysis -readOnly -scriptPath tools/re/ghidra \
//       -postScript FrServer.java <queue dir>
// Ask: write a text file <queue dir>/NAME.cmd (one command per line); read the answer in <queue dir>/NAME.out.
// Stop: create <queue dir>/stop.
//
// Commands (addresses are hex, with or without 0x):
//   decomp ADDR            decompile the function containing ADDR
//   list ADDR [COUNT]      disassemble COUNT instructions (default 40) from ADDR
//   callers ADDR           functions that call/reference the function at ADDR, with call sites
//   callees ADDR           functions called from the function containing ADDR
//   xrefs ADDR             every reference to ADDR (data or code)
//   bytes ADDR [COUNT]     hex dump
//   cstr ADDR              the NUL-terminated string at ADDR
//   find TEXT              addresses of TEXT in the image (ASCII), with references to each
//   vtable ADDR [N]       N 32-bit slots starting at ADDR, resolved through the descriptors to code addresses
//
// @category FreeRealms
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.*;

public class FrServer extends GhidraScript {
    DecompInterface ifc;
    Memory mem;

    Address a(String s) throws Exception {
        s = s.trim().toLowerCase();
        if (s.startsWith("0x")) s = s.substring(2);
        return toAddr(Long.parseLong(s, 16));
    }

    String fn(Address addr) {
        Function f = getFunctionContaining(addr);
        if (f == null) return "";
        return f.getName() + "+0x" + Long.toHexString(addr.subtract(f.getEntryPoint()));
    }

    String run1(String line) throws Exception {
        String[] t = line.trim().split("\\s+");
        if (t.length == 0 || t[0].isEmpty()) return "";
        StringBuilder sb = new StringBuilder();
        switch (t[0]) {
            case "decomp": {
                Address ad = a(t[1]);
                Function f = getFunctionContaining(ad);
                if (f == null) {
                    disassemble(ad);
                    f = createFunction(ad, null);
                }
                if (f == null) return "no function at " + t[1] + "\n";
                DecompileResults r = ifc.decompileFunction(f, 90, monitor);
                if (!r.decompileCompleted()) return "decompile failed: " + r.getErrorMessage() + "\n";
                sb.append("// ").append(f.getName()).append(" @ ").append(f.getEntryPoint()).append("\n");
                sb.append(r.getDecompiledFunction().getC());
                break;
            }
            case "list": {
                Address ad = a(t[1]);
                int n = t.length > 2 ? Integer.parseInt(t[2]) : 40;
                for (int i = 0; i < n; i++) {
                    Instruction in = getInstructionAt(ad);
                    if (in == null) {
                        disassemble(ad);
                        in = getInstructionAt(ad);
                        if (in == null) { sb.append(ad).append("  <no instruction>\n"); break; }
                    }
                    sb.append(String.format("%08x  %s\n", ad.getOffset(), in.toString()));
                    ad = in.getNext() != null ? in.getNext().getAddress() : ad.add(4);
                }
                break;
            }
            case "callers": {
                Address ad = a(t[1]);
                ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(ad);
                while (it.hasNext()) {
                    Reference r = it.next();
                    sb.append(String.format("%08x  %s  %s\n", r.getFromAddress().getOffset(), r.getReferenceType(),
                            fn(r.getFromAddress())));
                }
                break;
            }
            case "callees": {
                Function f = getFunctionContaining(a(t[1]));
                if (f == null) return "no function\n";
                for (Function c : f.getCalledFunctions(monitor)) {
                    sb.append(String.format("%08x  %s\n", c.getEntryPoint().getOffset(), c.getName()));
                }
                break;
            }
            case "xrefs": {
                Address ad = a(t[1]);
                ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(ad);
                while (it.hasNext()) {
                    Reference r = it.next();
                    sb.append(String.format("%08x  %s  %s\n", r.getFromAddress().getOffset(), r.getReferenceType(),
                            fn(r.getFromAddress())));
                }
                break;
            }
            case "bytes": {
                Address ad = a(t[1]);
                int n = t.length > 2 ? Integer.parseInt(t[2]) : 64;
                byte[] b = new byte[n];
                mem.getBytes(ad, b);
                for (int i = 0; i < n; i += 16) {
                    sb.append(String.format("%08x ", ad.getOffset() + i));
                    for (int j = i; j < Math.min(n, i + 16); j++) sb.append(String.format(" %02x", b[j] & 0xff));
                    sb.append("\n");
                }
                break;
            }
            case "cstr": {
                Address ad = a(t[1]);
                StringBuilder s = new StringBuilder();
                for (int i = 0; i < 400; i++) {
                    int c = mem.getByte(ad.add(i)) & 0xff;
                    if (c == 0) break;
                    s.append(c >= 32 && c < 127 ? (char) c : '.');
                }
                sb.append(s).append("\n");
                break;
            }
            case "find": {
                String text = line.trim().substring(5);
                byte[] pat = text.getBytes(StandardCharsets.ISO_8859_1);
                Address from = mem.getMinAddress();
                int shown = 0;
                while (from != null && shown < 50) {
                    Address hit = mem.findBytes(from, pat, null, true, monitor);
                    if (hit == null) break;
                    sb.append(String.format("%08x\n", hit.getOffset()));
                    ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(hit);
                    while (it.hasNext()) {
                        Reference r = it.next();
                        sb.append(String.format("    ref from %08x %s\n", r.getFromAddress().getOffset(),
                                fn(r.getFromAddress())));
                    }
                    shown++;
                    from = hit.add(1);
                }
                break;
            }
            case "vtable": {
                Address ad = a(t[1]);
                int n = t.length > 2 ? Integer.parseInt(t[2]) : 32;
                for (int i = 0; i < n; i++) {
                    long p = mem.getInt(ad.add(4L * i)) & 0xffffffffL;
                    String code = "";
                    try {
                        long c = mem.getInt(toAddr(p)) & 0xffffffffL;
                        code = String.format("-> code %08x %s", c, fn(toAddr(c)));
                    } catch (Exception e) {
                        code = "";
                    }
                    sb.append(String.format("slot %2d (+%3d)  %08x %s\n", i, 4 * i, p, code));
                }
                break;
            }
            default:
                return "unknown command: " + t[0] + "\n";
        }
        return sb.toString();
    }

    @Override
    public void run() throws Exception {
        mem = currentProgram.getMemory();
        ifc = new DecompInterface();
        ifc.openProgram(currentProgram);
        File dir = new File(getScriptArgs()[0]);
        dir.mkdirs();
        println("FrServer ready, queue: " + dir);
        new File(dir, "ready").createNewFile();
        while (!new File(dir, "stop").exists()) {
            File[] cmds = dir.listFiles((d, n) -> n.endsWith(".cmd"));
            if (cmds != null) {
                Arrays.sort(cmds);
                for (File c : cmds) {
                    StringBuilder out = new StringBuilder();
                    for (String line : Files.readAllLines(c.toPath())) {
                        out.append("> ").append(line).append("\n");
                        try {
                            out.append(run1(line));
                        } catch (Exception e) {
                            out.append("error: ").append(e).append("\n");
                        }
                    }
                    File o = new File(dir, c.getName().replace(".cmd", ".out"));
                    Files.write(new File(dir, c.getName() + ".tmp").toPath(), out.toString().getBytes(StandardCharsets.UTF_8));
                    new File(dir, c.getName() + ".tmp").renameTo(o);
                    c.delete();
                }
            }
            Thread.sleep(200);
        }
        println("FrServer stopping");
    }
}
