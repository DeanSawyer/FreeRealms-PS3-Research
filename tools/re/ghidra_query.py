#!/usr/bin/env python3
"""Send commands to a running FrServer.java (see tools/re/ghidra/FrServer.java) and print the answer.

    python3 tools/re/ghidra_query.py QUEUE_DIR "decomp 0x7cfdd4" "callers 0x8981c4"

Each argument is one command line. Commands: decomp, list, callers, callees, xrefs, bytes, cstr, find, vtable.
"""
import os
import sys
import time
import uuid


def query(queue, lines, timeout=600):
    name = uuid.uuid4().hex[:12]
    cmd = os.path.join(queue, name + ".cmd")
    out = os.path.join(queue, name + ".out")
    tmp = cmd + ".w"
    with open(tmp, "w") as f:
        f.write("\n".join(lines) + "\n")
    os.rename(tmp, cmd)  # the server only picks up *.cmd, so it never sees a half-written file
    end = time.time() + timeout
    while time.time() < end:
        if os.path.exists(out):
            with open(out, encoding="utf-8", errors="replace") as f:
                text = f.read()
            os.remove(out)
            return text
        time.sleep(0.2)
    raise TimeoutError("no answer from the Ghidra server in %d s" % timeout)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    sys.stdout.write(query(sys.argv[1], sys.argv[2:]))
