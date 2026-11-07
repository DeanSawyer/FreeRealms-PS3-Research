"""The clothes the character creator picked, as the attachments the client draws.

The creator sends item definition ids and tints (ItemChest=28216, ItemChestTint=234 and so on). The table in
data/clothing_items.tsv.gz (tools/gen_clothing_items.py, from Sanctuary's Resources/ClientItemDefinitions.json) gives
each id's model, texture alias, tint alias and slot. Sanctuary builds the same attachment in Player.GetAttachment: the
item's tint, or the definition's icon tint when the item's tint is 0, and no composite effect.

equip_item() is ClientUpdate (38) sub-opcode 5, EquipItem: one piece of your own character's outfit, what Sanctuary sends
your client when you equip something (InventoryPacketEquipByGuidHandler). The PS3 1.77 handler (0xfa600) reads u16 38,
u16 5, i32 item id, the attachment (model, texture alias and tint alias strings, then tint, composite effect and slot
ints), profile id and one unused int (the PC packet's trailing Equip bool is not there), and only acts when that is the
whole packet. Then 0xfec54 calls 0x5aab0 (player, slot, attachment, profile id) and redraws when the profile is the
active one. In RPCS3 nothing went on with it, so it is only sent with FR_EQUIP=5. PlayerUpdate sub-opcode 6 (EquipItemChange) is the same news for other players; sent about yourself it
changed nothing in RPCS3.

equipped_items_change() is sub-opcode 7, EquippedItemsChange: the whole outfit at once (handler 0x112afc, attachment
list reader 0xb03e90). It is what Sanctuary sends when you change jobs, and the client plays the job change effect
(4005, at 0x11cc74) before it puts the clothes on. It is the only one seen to dress the character, so it is the default.
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


def equip_item(item_id, piece, profile_id):
    """ClientUpdate sub-opcode 5: put one piece on your character in profile `profile_id` (Sanctuary's
    ClientUpdatePacketEquipItem without the Equip bool: item id, attachment, profile id, an unused int)."""
    model, texture, tint_alias, tint, composite, slot = piece
    out = bytearray()
    out += (38).to_bytes(2, "little") + (5).to_bytes(2, "little") + int(item_id).to_bytes(4, "little", signed=True)
    out += _string(model) + _string(texture) + _string(tint_alias)
    for value in (tint, composite, slot, profile_id, 0):
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
