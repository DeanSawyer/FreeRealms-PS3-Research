"""Reference data the client gets when it is ready: item classes and item categories, as Sanctuary sends them.

Sanctuary's BaseZone.OnClientIsReady sends ReferenceData (44) sub-opcode 1 (every item class in Resources/ItemClasses.txt)
and sub-opcode 2 (every item category in ItemCategories.txt and every category group entry in ItemCategoryGroups.txt).
The tables are copied unchanged into data/. The PS3 1.77 handler is 0xd4764 (the client's vtable slot for opcode 44);
layouts read with tools/ppc_trace.py:

* sub-opcode 1 (reader 0xb0688c, into client +2292): u32 count, then per class i32 id (the key) and the six columns of
  ItemClasses.txt (id, name id, icon id, wield type, stat id, profile name id): 28 bytes each, as Sanctuary writes them.
* sub-opcode 2 (reader 0xb066fc, into client +2388): u32 count, then per category i32 id (the key), i32 id, i32 name id,
  i32 icon id, i32 unknown, u8 unknown 2 (21 bytes: Sanctuary's last field, Display, is not in this build); then u32
  count and per group entry i32 group id, i32 group id, i32 category id (12 bytes), every group's entries in turn.
"""
import os
import struct

DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")


def _rows(name):
    with open(os.path.join(DATA, name), encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                yield [int(v) for v in line.split("^")[:-1] if v != ""] if line.endswith("^") else [int(v) for v in line.split("^")]


def item_classes():
    return [row[:6] for row in _rows("ItemClasses.txt")]


def item_categories():
    return [row[:6] for row in _rows("ItemCategories.txt")]


def item_category_groups():
    """[(group id, category id)] grouped by group id in first-seen order, as Sanctuary's dictionary keeps them."""
    groups = {}
    for group_id, category_id in (row[:2] for row in _rows("ItemCategoryGroups.txt")):
        groups.setdefault(group_id, []).append(category_id)
    return [(g, c) for g, categories in groups.items() for c in categories]


def item_class_definitions():
    rows = item_classes()
    return struct.pack("<HHi", 44, 1, len(rows)) + b"".join(struct.pack("<7i", r[0], *r) for r in rows)


def item_category_definitions():
    cats = item_categories()
    groups = item_category_groups()
    out = struct.pack("<HHi", 44, 2, len(cats))
    out += b"".join(struct.pack("<5iB", c[0], c[0], c[1], c[2], c[3], 1 if c[4] else 0) for c in cats)
    out += struct.pack("<i", len(groups)) + b"".join(struct.pack("<3i", g, g, c) for g, c in groups)
    return out
