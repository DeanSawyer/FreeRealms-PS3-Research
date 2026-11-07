#!/usr/bin/env python3
"""Write bootstrap/data/clothing_items.tsv.gz: what the client draws for each piece of clothing.

The character creator sends item definition ids (ItemChest=28216 and so on). To dress the character the server
sends the client each piece's model, texture alias, tint alias, tint and slot (ClientUpdate sub-opcode 5,
EquipItem; see docs/re/zone-loading.md). Those come from Sanctuary's Resources/ClientItemDefinitions.json,
which is 33 MB, so this keeps only what is needed: items of class 1 (the adventurer clothes the creator offers) worn in
slots 1 to 6 (head, hands, chest, legs, feet, shoulders).

    python tools/gen_clothing_items.py PATH/TO/Sanctuary/src/Resources/ClientItemDefinitions.json

The file is gzip with a fixed timestamp, so the same input always gives the same bytes. Columns, tab separated:
id, slot, model, texture alias, tint alias, default tint (the icon's tint, used when the creator sent tint 0).

Standard library only.
"""
import argparse
import gzip
import io
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "bootstrap", "data", "clothing_items.tsv.gz")
CLOTHING_CLASS = 1
SLOTS = (1, 2, 3, 4, 5, 6)
HEADER = "# id\tslot\tmodel\ttexture_alias\ttint_alias\tdefault_tint"


def clean(text):
    return str(text or "").replace("\t", " ").replace("\n", " ").replace("\r", " ")


def rows(definitions):
    out = []
    for item in definitions:
        if item.get("Class") != CLOTHING_CLASS or item.get("Slot") not in SLOTS or not item.get("ModelName"):
            continue
        out.append((int(item["Id"]), int(item["Slot"]), clean(item.get("ModelName")), clean(item.get("TextureAlias")),
                    clean(item.get("TintAlias")), int((item.get("Icon") or {}).get("TintId") or 0)))
    out.sort()
    return out


def encode(table):
    text = "\n".join([HEADER] + ["%d\t%d\t%s\t%s\t%s\t%d" % row for row in table]) + "\n"
    buf = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", compresslevel=9, fileobj=buf, mtime=0) as gz:
        gz.write(text.encode("utf-8"))
    return buf.getvalue()


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("definitions", help="Sanctuary's Resources/ClientItemDefinitions.json")
    args = ap.parse_args()
    with open(args.definitions, encoding="utf-8-sig") as f:
        definitions = json.load(f)
    table = rows(definitions)
    data = encode(table)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(data)
    print("wrote %s (%d items, %d bytes)" % (os.path.relpath(OUT, ROOT), len(table), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
