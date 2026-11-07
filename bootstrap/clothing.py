"""The clothes the character creator picked, as the attachments the client draws.

The creator sends item definition ids and tints (ItemChest=28216, ItemChestTint=234 and so on). The table in
data/clothing_items.tsv.gz (tools/gen_clothing_items.py, from Sanctuary's Resources/ClientItemDefinitions.json) gives
each id's model, texture alias, tint alias and slot. Sanctuary builds the same attachment in Player.GetAttachment: the
item's tint, or the definition's icon tint when the item's tint is 0, and no composite effect.

equip_item_change() is PlayerUpdate (35) sub-opcode 6, EquipItemChange: one piece. The PS3 1.77 handler (0x112c24)
reads u16 35, u16 6, u64 guid, i32 item id, the attachment (model, texture alias and tint alias strings, then tint,
composite effect and slot ints), then profile id, item class and wield type, and only acts when that is the whole
packet. It finds the character by guid and attaches the piece (0x11daec), stores the profile id and sets the wield type.

equipped_items_change() is sub-opcode 7, EquippedItemsChange: the whole outfit at once (handler 0x112afc, attachment
list reader 0xb03e90). It is what Sanctuary sends when you change jobs, and the client plays the job change effect
(4005, at 0x11cc74) before it puts the clothes on, so it is only used with FR_EQUIP=7.
"""
import gzip
import os

import character_mappings

TABLE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "clothing_items.tsv.gz")

# Creator field and equipment slot (Sanctuary's EquipmentSlotDefinitions.txt), in slot order.
CREATOR_SLOTS = (("ItemHead", 1), ("ItemHands", 2), ("ItemChest", 3), ("ItemLegs", 4), ("ItemFeet", 5),
                 ("ItemShoulders", 6))

_items = None


def items():
    """{id: (slot, model, texture alias, tint alias, default tint)}, read once."""
    global _items
    if _items is None:
        table = {}
        with gzip.open(TABLE, "rt", encoding="utf-8") as f:
            for line in f:
                if line.startswith("#") or not line.strip():
                    continue
                item_id, slot, model, texture, tint_alias, tint = line.rstrip("\n").split("\t")
                table[int(item_id)] = (int(slot), model, texture, tint_alias, int(tint))
        _items = table
    return _items


def attachments(character):
    """(model, texture alias, tint alias, tint, composite effect, slot) for each piece the creator picked that the
    table has, in slot order. Pieces the table does not have (and 0, nothing picked) are left out."""
    out = []
    for key, _slot in CREATOR_SLOTS:
        item_id = character_mappings.number(character, key, 0)
        found = items().get(item_id) if item_id > 0 else None
        if not found:
            continue
        slot, model, texture, tint_alias, default_tint = found
        tint = character_mappings.number(character, key + "Tint", 0)
        out.append((model, texture, tint_alias, tint or default_tint, 0, slot))
    return out


def _string(text):
    data = text.encode("utf-8")
    return len(data).to_bytes(4, "little") + data


def equip_item_change(guid, item_id, piece, profile_id, item_class=1, wield_type=0):
    """PlayerUpdate sub-opcode 6: put one piece on character `guid` (Sanctuary's PlayerUpdatePacketEquipItemChange:
    guid, item id, attachment, profile id, item class, wield type). Every piece in the table is item class 1."""
    model, texture, tint_alias, tint, composite, slot = piece
    out = bytearray()
    out += (35).to_bytes(2, "little") + (6).to_bytes(2, "little") + int(guid).to_bytes(8, "little")
    out += int(item_id).to_bytes(4, "little", signed=True)
    out += _string(model) + _string(texture) + _string(tint_alias)
    for value in (tint, composite, slot, profile_id, item_class, wield_type):
        out += int(value).to_bytes(4, "little", signed=True)
    return bytes(out)


def equipped_items_change(guid, pieces, wield_type=0):
    """PlayerUpdate sub-opcode 7: put these attachments on character `guid` (Sanctuary's
    PlayerUpdatePacketEquippedItemsChange: guid, attachments, then an int Sanctuary leaves at 0)."""
    out = bytearray()
    out += (35).to_bytes(2, "little") + (7).to_bytes(2, "little") + int(guid).to_bytes(8, "little")
    out += len(pieces).to_bytes(4, "little")
    for model, texture, tint_alias, tint, composite, slot in pieces:
        out += _string(model) + _string(texture) + _string(tint_alias)
        for value in (tint, composite, slot):
            out += int(value).to_bytes(4, "little", signed=True)
    out += int(wield_type).to_bytes(4, "little", signed=True)
    return bytes(out)
