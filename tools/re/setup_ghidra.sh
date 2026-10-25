#!/usr/bin/env bash
# Reproduce the Ghidra project for game.elf.
#
#   tools/re/setup_ghidra.sh <game.elf> <work dir>
#
# Downloads Ghidra 11.3.2 (needs Java 21), imports the decrypted PS3 executable as PowerPC:BE:64:A2ALT-32addr,
# creates a function at every code address named by the descriptor table (FrSetup.java) and runs a trimmed
# auto-analysis (FrAnalysisOptions.java). Nothing from the client is written into the repository.
set -euo pipefail
ELF=$(readlink -f "${1:?game.elf}")
WORK=$(mkdir -p "${2:?work dir}" && readlink -f "$2")
HERE=$(cd "$(dirname "$0")" && pwd)
GHIDRA_VER=11.3.2
GHIDRA_DIR="$WORK/ghidra_${GHIDRA_VER}_PUBLIC"
if [ ! -d "$GHIDRA_DIR" ]; then
  curl -L -o "$WORK/ghidra.zip" \
    "https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_${GHIDRA_VER}_build/ghidra_${GHIDRA_VER}_PUBLIC_20250415.zip"
  unzip -q "$WORK/ghidra.zip" -d "$WORK"
fi
export MAXMEM=${MAXMEM:-5G}
HL="$GHIDRA_DIR/support/analyzeHeadless"
mkdir -p "$WORK/project"
"$HL" "$WORK/project" FR -import "$ELF" -processor "PowerPC:BE:64:A2ALT-32addr" -cspec default \
  -noanalysis -overwrite -scriptPath "$HERE/ghidra" -postScript FrSetup.java
"$HL" "$WORK/project" FR -process game.elf -scriptPath "$HERE/ghidra" -preScript FrAnalysisOptions.java \
  -analysisTimeoutPerFile 5400
echo "Query server:"
echo "  $HL $WORK/project FR -process game.elf -noanalysis -readOnly -scriptPath $HERE/ghidra -postScript FrServer.java $WORK/queue"
echo "  python3 $HERE/ghidra_query.py $WORK/queue 'decomp 0x7cfdd4'"
