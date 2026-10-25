#!/usr/bin/env python3
"""Check every row of docs/re/addresses.csv against the analysed game.elf.

For each row the Ghidra server (tools/re/ghidra/FrServer.java) decompiles or disassembles the address and the
`expect` text must appear in the answer. This keeps the documentation honest: a wrong address or a changed
conclusion fails here instead of sitting in a document.

    python3 tools/re/verify_addresses.py QUEUE_DIR [docs/re/addresses.csv]
"""
import csv
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ghidra_query import query  # noqa: E402


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    queue = sys.argv[1]
    path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "..", "docs", "re",
                                                              "addresses.csv")
    rows = list(csv.DictReader(open(path, newline="", encoding="utf-8")))
    failed = 0
    for row in rows:
        cmd = ("decomp " if row["query"] == "decomp" else "list ") + row["address"]
        if row["query"] == "list":
            cmd += " 60"
        text = query(queue, [cmd], timeout=300)
        ok = row["expect"] in text
        failed += not ok
        print("%-4s %-9s %-42s %s" % ("ok" if ok else "FAIL", row["address"], row["name"], "" if ok else
                                      "expected %r" % row["expect"]))
    print("\n%d of %d rows verified" % (len(rows) - failed, len(rows)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
