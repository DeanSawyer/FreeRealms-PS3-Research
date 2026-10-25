#!/usr/bin/env python3
"""Restore Free Realms locale files (locale/<lang>_data.dat + .dir) that lost their CRLF line endings.

The locale data is plain text: one record per line, `id<TAB>type<TAB>text`, each line ended by CRLF, and the
`.dir` index lists every record as `id<TAB>offset<TAB>length<TAB>flag` (offset/length refer to the original,
CRLF version of the `.dat`; the length excludes the line's CRLF). Its header records `Count` and the
`MD5Checksum` of the original `.dat`. Tools that turn CRLF into LF (git text normalisation, some zip tools)
change both files. A blind LF-to-CRLF conversion fails for the `.dat` because a few records contain a bare LF
inside their text; the index says exactly where each record ends, so the original can be rebuilt and checked
against the recorded MD5.

Usage:
    python tools/restore_locale.py "C:\\path\\to\\NPUA30048\\USRDIR\\locale" [more folders ...] [--dry-run]

For each folder every `*_data.dir`/`*_data.dat` pair is checked. Files that need fixing are replaced; the
previous version is kept next to it as `<name>.lf-copy`. Nothing is written unless the result verifies.
"""

import argparse
import hashlib
import sys
import zlib
from pathlib import Path


def parse_dir(text):
    header, rows = {}, []
    for line in text.replace("\r\n", "\n").split("\n"):
        if not line:
            continue
        if line.startswith("## "):
            key, _, value = line[3:].partition(":")
            header[key.strip()] = value.strip()
            continue
        fields = line.split("\t")
        rows.append((int(fields[0]), int(fields[1]), int(fields[2])))
    return header, rows


def restore_dat(data, rows):
    """Return the CRLF version of ``data`` or raise ValueError if the index does not fit."""
    out = bytearray(data[:rows[0][1]])  # byte-order mark before the first record
    pos = rows[0][1]
    for index, (cid, offset, length) in enumerate(rows):
        if offset != len(out):
            raise ValueError(f"record {index} (id {cid}): index offset {offset}, rebuilt offset {len(out)}")
        end = pos + length
        if data[end:end + 1] != b"\n":
            raise ValueError(f"record {index} (id {cid}): no line end where the index says it ends")
        out += data[pos:end] + b"\r\n"
        pos = end + 1
    if pos != len(data):
        raise ValueError(f"{len(data) - pos} bytes left over after the last record")
    return bytes(out)


def process(folder, dry_run):
    changed = 0
    for dir_path in sorted(folder.glob("*_data.dir")):
        dat_path = dir_path.with_suffix(".dat")
        if not dat_path.is_file():
            print(f"  {dir_path.name}: no matching .dat, skipped")
            continue
        raw_dir = dir_path.read_bytes()
        if b"\r" not in raw_dir:
            fixed_dir = raw_dir.replace(b"\n", b"\r\n")
            print(f"  {dir_path.name}: LF line endings -> CRLF")
        else:
            fixed_dir, raw_dir = raw_dir, None
        header, rows = parse_dir(fixed_dir.decode("utf-8"))
        if len(rows) != int(header.get("Count", -1)):
            print(f"  {dir_path.name}: row count {len(rows)} != Count {header.get('Count')}, skipped")
            continue
        data = dat_path.read_bytes()
        want_md5 = header.get("MD5Checksum", "").upper()
        if hashlib.md5(data).hexdigest().upper() == want_md5:
            print(f"  {dat_path.name}: already the original (MD5 matches)")
            new_dat = None
        else:
            try:
                new_dat = restore_dat(data, rows)
            except ValueError as exc:
                print(f"  {dat_path.name}: cannot restore ({exc})")
                continue
            got = hashlib.md5(new_dat).hexdigest().upper()
            if got != want_md5:
                print(f"  {dat_path.name}: rebuilt file has MD5 {got}, index says {want_md5}; not written")
                continue
            print(f"  {dat_path.name}: rebuilt {len(data)} -> {len(new_dat)} bytes, "
                  f"MD5 {got} matches the index, CRC32 0x{zlib.crc32(new_dat):08X}")
        for path, new, old in ((dir_path, fixed_dir if raw_dir is not None else None, raw_dir),
                               (dat_path, new_dat, data)):
            if new is None or dry_run:
                continue
            backup = path.with_name(path.name + ".lf-copy")
            if not backup.exists():
                backup.write_bytes(old)
            path.write_bytes(new)
            changed += 1
            print(f"  wrote {path} (previous version kept as {backup.name})")
    return changed


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="+", type=Path)
    ap.add_argument("--dry-run", action="store_true", help="check and report only; write nothing")
    args = ap.parse_args()
    total = 0
    for folder in args.folders:
        print(folder)
        if not folder.is_dir():
            print("  not a folder")
            continue
        total += process(folder, args.dry_run)
    print(f"done; {total} file(s) written" + (" (dry run)" if args.dry_run else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
