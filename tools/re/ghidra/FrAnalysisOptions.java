// Choose the auto-analysis passes for game.elf. The program is ~14 MB of PowerPC code (about 18k functions);
// the default analyzer set takes hours, and most of it adds nothing here. Run as an analyzeHeadless -preScript.
//
// @category FreeRealms
import ghidra.app.script.GhidraScript;

public class FrAnalysisOptions extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] off = {
            "Decompiler Parameter ID",
            "Aggressive Instruction Finder",
            "Embedded Media",
            "Create Address Tables",
            "Demangler GNU",
            "Function ID",
            "Stack",
            "Call Convention ID",
            "Variadic Function Signature Override",
            "ELF Scan for GOT",
            "Non-Returning Functions - Discovered",
            "Shared Return Calls",
        };
        for (String name : off) {
            try {
                setAnalysisOption(currentProgram, name, "false");
            } catch (Exception e) {
                println("note: analyzer not present: " + name);
            }
        }
    }
}
