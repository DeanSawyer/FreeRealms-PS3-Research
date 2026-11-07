"""The items a new character owns, and the item definitions the client needs for them, as Sanctuary gives them.

Sanctuary's CharacterCreateRequestHandler makes three items from the character creator's chest, legs and feet (ids
1, 2, 3, with the creator's tints) and equips them in the starter job. With UnlockAllProfiles it then gives every other
job its DefaultItems from Resources/Profiles.json: each definition the character does not own yet becomes a new item
(the next id, the definition's icon tint, count 1), items for the other gender are skipped, and a job without
DefaultItems gets the creator's three. The gateway then equips each job's items by the definition's slot (a later item
in the same slot wins). This module does the same from bootstrap/data/item_definitions.tsv.gz
(tools/gen_item_definitions.py) and bootstrap/jobs.py. The creator's head, hands and shoulders, which Sanctuary
ignores, are added too when something was picked there, so everything the player chose is worn.

On the PS3 1.77 client (read with tools/ppc_trace.py; docs/protocol/gateway.md, *Items*):

* item_definition() is PlayerUpdate (35) sub-opcode 33: an i32-length payload holding one definition: the 34 fields of
  item_definitions.tsv.gz in that order (0xac53b8, strings are i32 length + bytes, member discount is one byte), then one i32 (0xa90d10) and two
  empty lists (0xac3c24, 0xac3788). The handler (0x110ab0 -> 0x11e008) needs the player to exist, and puts the
  definition in the client's cache (0x148420, client +3076), where the equipped-clothes routine
  RequestEquippedAttachments (0xeba4c) and the inventory look items up.
* item_record() is one entry of the player data's item list (reader 0xb0f10c, 37 bytes): id, an int, definition, tint,
  id again, count, a byte, an int, an empty list, an int. The client keys the item by the first field (0x59a34).
* A job's equipment is 17 item ids at entry offset 0x43 (slot n at 0x43 + 4 * (n - 1)); 0 is an empty slot.

FR_ITEMS=0 sends none of this (no items, no definitions, empty equipment), as before.
"""
import gzip
import os
import struct

import character_mappings
import jobs

TABLE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "item_definitions.tsv.gz")
FIELD_KINDS = "iiiiiiiiiiibbssiiibbiiiiiiibibsbui"   # i32, bool and u8 (one byte), string; tools/gen_item_definitions.py
SLOT_FIELD = 10
EQUIPMENT_SLOTS = 17
CREATOR_ITEMS = (("ItemChest", "ItemChestTint"), ("ItemLegs", "ItemLegsTint"), ("ItemFeet", "ItemFeetTint"))
CREATOR_EXTRAS = (("ItemHead", "ItemHeadTint"), ("ItemHands", "ItemHandsTint"), ("ItemShoulders", "ItemShouldersTint"))
GENDER_BY_MODEL = {1: 1, 2: 1, 60: 2, 61: 2}   # Sanctuary's Models.txt: human and fairy, male and female

_definitions = None


def definitions():
    """{definition id: tuple of the 34 fields}, read once."""
    global _definitions
    if _definitions is None:
        table = {}
        with gzip.open(TABLE, "rt", encoding="utf-8") as f:
            for line in f:
                if line.startswith("#") or not line.strip():
                    continue
                parts = line.rstrip("\n").split("\t")
                row = tuple(p if kind == "s" else int(p) for p, kind in zip(parts, FIELD_KINDS))
                table[row[0]] = row
        _definitions = table
    return _definitions


def enabled():
    return os.environ.get("FR_ITEMS", "1").strip() != "0"


def _string(text):
    data = text.encode("utf-8")
    return struct.pack("<I", len(data)) + data


def definition_payload(row):
    out = bytearray()
    for value, kind in zip(row, FIELD_KINDS):
        if kind == "s":
            out += _string(value)
        elif kind == "b":
            out += struct.pack("<B", 1 if value else 0)
        elif kind == "u":
            out += struct.pack("<B", value & 0xFF)
        else:
            out += struct.pack("<i", value)
    out += struct.pack("<iii", 0, 0, 0)   # the int after the definition, then two empty lists
    return bytes(out)


def item_definition(definition_id):
    """PlayerUpdate sub-opcode 33 with one definition."""
    payload = definition_payload(definitions()[definition_id])
    return struct.pack("<HHi", 35, 33, len(payload)) + payload


def item_record(item_id, definition_id, tint, count=1):
    """One entry of the player data's item list."""
    return struct.pack("<iiiiii", item_id, 0, definition_id, tint, item_id, count) + b"\x00" + struct.pack("<iii", 0, 0, 0)


def inventory(character, chosen_jobs):
    """(items, equipment): items is [(id, definition, tint)], equipment is {job id: {slot: item id}}."""
    c = character or {}
    table = definitions()
    gender = GENDER_BY_MODEL.get(character_mappings.number(c, "Model", 1), 1)
    items = []
    by_definition = {}

    def add(definition_id, tint):
        items.append((len(items) + 1, definition_id, tint))
        by_definition.setdefault(definition_id, items[-1][0])
        return items[-1][0]

    creator = []
    for key, tint_key in CREATOR_ITEMS + CREATOR_EXTRAS:
        definition_id = character_mappings.number(c, key, 0)
        if definition_id > 0 and definition_id in table:
            creator.append(add(definition_id, character_mappings.number(c, tint_key, 0)))

    def equip(item_ids):
        slots = {}
        for item_id in item_ids:
            slot = table[items[item_id - 1][1]][SLOT_FIELD]
            if 1 <= slot <= EQUIPMENT_SLOTS:
                slots[slot] = item_id
        return slots

    equipment = {}
    for job in chosen_jobs:
        defaults = jobs.JOB_ITEMS.get(job[0], ())
        if job[0] == jobs.STARTER_JOB or not defaults:
            equipment[job[0]] = equip(creator)
            continue
        owned = []
        for definition_id in defaults:
            row = table.get(definition_id)
            if row is None or (row[15] != 0 and row[15] != gender):   # gender usage
                continue
            item_id = by_definition.get(definition_id) or add(definition_id, row[4])   # the icon's tint
            owned.append(item_id)
        equipment[job[0]] = equip(owned)
    return items, equipment


def item_list(items):
    """The item list of the player data: a count, then one record per item."""
    return struct.pack("<i", len(items)) + b"".join(item_record(i, d, t) for i, d, t in items)


def definition_packets(items):
    """One item_definition() per distinct definition the character owns, in item order."""
    seen = []
    for _item_id, definition_id, _tint in items:
        if definition_id not in seen:
            seen.append(definition_id)
    return [item_definition(d) for d in seen]
