# tools/re — Ghidra workspace for `game.elf`

| File | Purpose |
|---|---|
| `setup_ghidra.sh` | `setup_ghidra.sh <game.elf> <work dir>`: downloads Ghidra 11.3.2 (needs Java 21), imports the decrypted executable as `PowerPC:BE:64:A2ALT-32addr`, creates functions from the descriptor table, runs a trimmed auto-analysis, prints the server command |
| `ghidra/FrSetup.java`, `ghidra/FrAnalysisOptions.java` | Headless import + analysis options. `FrSetup` also rewrites the 469 Cell-only `lvlx`/`lvrx` instructions to `lvx` in the Ghidra copy (Ghidra cannot decode them and would cut every function that contains one) |
| `ghidra/FrServer.java` | Keeps the analysed program open and answers commands from a queue directory |
| `ghidra_query.py` | Client for that queue: `python3 tools/re/ghidra_query.py QUEUE "decomp 0x7cfdd4" "callers 0x8981c4"` |
| `verify_addresses.py` | Checks every row of `docs/re/addresses.csv` against the analysis |

Commands: `decomp ADDR`, `list ADDR [N]`, `callers`, `callees`, `xrefs`, `bytes`, `cstr`, `find TEXT`, `vtable ADDR N`.

Addresses are virtual addresses (file offset + 0x10000). The ELF itself is the user's own file and is not part of this
repository.
