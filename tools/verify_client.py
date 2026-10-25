#!/usr/bin/env python3
"""Compare a game folder against the digest: which files match what the client expects?

For every file listed in NPUA30048.soe this checks the copy under USRDIR:
  ok          size and CRC32 match the digest
  ok-crlf     matches after converting LF to CRLF (text files rewritten by git/zip tools)
  missing     no such file
  size        different size (a different build of the file)
  crc         same size, different CRC32 (modified or corrupt)

Usage:
    python tools/verify_client.py NPUA30048.soe "C:\\path\\to\\NPUA30048" [--csv report.csv]
Reads only; nothing in the game folder is modified.
"""

import argparse
import csv
import sys
import zlib
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import parse_digest  # noqa: E402

CHUNK = 4 * 1024 * 1024
TEXT_FIX_LIMIT = 4 * 1024 * 1024


def crc32_of(path):
    crc = 0
    with path.open("rb") as handle:
        while block := handle.read(CHUNK):
            crc = zlib.crc32(block, crc)
    return crc


def check(entry, usrdir):
    local = usrdir / entry["path"]
    if not local.is_file():
        return "missing", None
    size = local.stat().st_size
    if size == entry["size"]:
        crc = crc32_of(local)
        return ("ok" if crc == entry["crc32"] else "crc"), size
    if size <= TEXT_FIX_LIMIT and entry["size"] <= TEXT_FIX_LIMIT:
        data = local.read_bytes()
        fixed = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        if len(fixed) == entry["size"] and zlib.crc32(fixed) == entry["crc32"]:
            return "ok-crlf", size
    return "size", size


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("digest", type=Path)
    ap.add_argument("client", type=Path, help="NPUA30048 folder (the one containing USRDIR)")
    ap.add_argument("--csv", type=Path, help="write the full report here")
    ap.add_argument("--show", type=int, default=25, help="how many problem files to print")
    args = ap.parse_args()

    usrdir = args.client / "USRDIR" if (args.client / "USRDIR").is_dir() else args.client
    body, _ = parse_digest.split_digest(args.digest.read_bytes())
    entries = parse_digest.parse_tree(body)
    print(f"{len(entries)} files in digest; checking {usrdir}")

    rows = []
    for i, entry in enumerate(entries, 1):
        status, local_size = check(entry, usrdir)
        rows.append({"status": status, "path": entry["path"], "digest_size": entry["size"],
                     "local_size": local_size, "digest_crc32": f"0x{entry['crc32']:08X}",
                     "download_bytes": entry["compressed_length"]})
        if i % 100 == 0:
            print(f"  ...{i}/{len(entries)}", flush=True)

    counts = Counter(r["status"] for r in rows)
    print("\nresult:", ", ".join(f"{k}={counts.get(k, 0)}" for k in
                                  ("ok", "ok-crlf", "missing", "size", "crc")))
    bad = [r for r in rows if r["status"] in ("missing", "size", "crc")]
    need = sum(r["download_bytes"] for r in bad)
    print(f"files the client would have to download: {len(bad)} "
          f"({need / 1e6:.1f} MB compressed per the digest)")
    for r in bad[: args.show]:
        print(f"  {r['status']:<8} {r['path']}  digest={r['digest_size']} local={r['local_size']}")
    if len(bad) > args.show:
        print(f"  ... {len(bad) - args.show} more (use --csv for the full list)")
    if args.csv:
        with args.csv.open("w", newline="", encoding="utf-8") as out:
            writer = csv.DictWriter(out, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)
        print(f"wrote {args.csv}")
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
