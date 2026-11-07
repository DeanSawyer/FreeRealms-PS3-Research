"""The inventory, one for one with Sanctuary's: what a character owns and wears, and the client's inventory requests.

State per session: the items and each job's equipment (bootstrap/items.py, as Sanctuary creates them for a new
character) and the active job. The handlers follow Sanctuary.Gateway's, check for check:

* Inventory (42) sub-opcode 3, EquipByGuid (InventoryPacketEquipByGuidHandler): the item must be owned, have a
  definition of type 1 whose class is 0 or a known item class, and the job must exist. The item goes into the job's
  slot for its definition's slot (replacing what was there). Reply: ClientUpdate EquipItem to you, PlayerUpdate
  EquipItemChange to the others when it is the active job, the toolbar, and the weapon flair.
* sub-opcode 8, EquipByItemRecord: the same, finding the item by definition and tint.
* sub-opcode 2, EquippedRemove: empties a job's slot. Reply: ClientUpdate UnequipSlot, EquipItemChange with an empty
  attachment to the others, the toolbar, the weapon flair.
* Command (26) sub-opcode 13, SetProfile (CommandPacketSetProfileHandler): switches job. Reply: the weapon flair
  cleared, ClientUpdate ActivateProfile (the job's profile entry, its attachments, animation 3001 emo_outfit_all,
  effect 4005 PFX_Job_Swirl), EquippedItemsChange to the others, the toolbar.

The PS3 client builds these requests at 0x2848d0 / 0x21805c (42/3), 0x284858 (42/2), 0x5d494 (42/8) and 0xcff00
(26/13), with Sanctuary's sub-opcode numbers. Their exact byte layout is not read yet, so the fields are taken as
little-endian ints after the u16 opcode and u16 sub-opcode, in Sanctuary's order, and anything missing at the end
falls back (the slot to the definition's, the job to the active one). Every request is logged with its bytes.

Replies, with layouts read from the 1.77 client (docs/protocol/gateway.md, *Inventory*):
ClientUpdate (38) 5 EquipItem (0xfa600), 6 UnequipSlot (0xfa264: slot, job), 21 ActivateProfile (0xf9e88: i32-length
profile entry, attachment list, animation, effect, an unused int); PlayerUpdate (35) 6 EquipItemChange (0x112c24), 7
EquippedItemsChange (0x112afc), 31 SlotCompositeEffectOverride; Ability (36) 5 the toolbar.

Not done yet (Sanctuary has them): 42/6 ItemActionBarAssign (the action bar slot packet's PS3 layout is unknown),
42/10-12 style cards (need the definitions' Param1/Param2, not in the table), the combat jobs' toolbar abilities
(Sanctuary's CombatJobs.json), and saving to a database (this server keeps everything for the session only).
"""
import struct

import clothing
import items
import jobs
import reference_data

WEAPON_SLOT = 7          # Sanctuary Player.WeaponSlot
FLAIR_SHARD_SLOT = 13    # Sanctuary Player.FlairShardSlot
TOOLBAR_SLOTS = 8        # Sanctuary AbilitySet.MaxAbilitySlots
NO_ITEM_CLASS = 0        # Sanctuary ItemDefinitionHelper.NoItemClass
JOB_SWITCH_ANIMATION = 3001   # emo_outfit_all
JOB_SWITCH_EFFECT = 4005      # PFX_Job_Swirl

# Definition fields (bootstrap/items.py FIELD_KINDS order)
F_ICON_TINT, F_CLASS, F_SLOT, F_MODEL, F_TEXTURE, F_TYPE, F_COMPOSITE, F_TINT_ALIAS = 4, 8, 10, 13, 14, 16, 21, 30


def _ints(inner):
    """The little-endian ints after the u16 opcode and u16 sub-opcode."""
    body = inner[4:]
    return list(struct.unpack_from("<%di" % (len(body) // 4), body)) if len(body) >= 4 else []


def _string(text):
    data = text.encode("utf-8")
    return struct.pack("<I", len(data)) + data


def _attachment_bytes(piece):
    model, texture, tint_alias, tint, composite, slot = piece
    return _string(model) + _string(texture) + _string(tint_alias) + struct.pack("<iii", tint, composite, slot)


def unequip_slot(slot, profile_id):
    """ClientUpdate sub-opcode 6, UnequipSlot."""
    return struct.pack("<HHii", 38, 6, slot, profile_id)


def activate_profile(profile_entry, pieces, animation=JOB_SWITCH_ANIMATION, effect=JOB_SWITCH_EFFECT):
    """ClientUpdate sub-opcode 21, ActivateProfile."""
    return (struct.pack("<HHi", 38, 21, len(profile_entry)) + profile_entry
            + struct.pack("<i", len(pieces)) + b"".join(_attachment_bytes(p) for p in pieces)
            + struct.pack("<iii", animation, effect, 0))


def equip_item_change(guid, item_id, piece, profile_id, item_class, wield_type):
    """PlayerUpdate sub-opcode 6, EquipItemChange (what the other players see)."""
    return (struct.pack("<HHQi", 35, 6, guid, item_id) + _attachment_bytes(piece)
            + struct.pack("<iii", profile_id, item_class, wield_type))


def slot_composite_effect_override(guid, slot, effect):
    """PlayerUpdate sub-opcode 31, SlotCompositeEffectOverride."""
    return struct.pack("<HHQii", 35, 31, guid, slot, effect)


def toolbar(profile_id):
    """Ability (36) sub-opcode 5 with an empty ability set: Sanctuary's SendToolbar for a job without a combat kit."""
    return struct.pack("<HHii", 36, 5, profile_id, TOOLBAR_SLOTS) + bytes(4 * TOOLBAR_SLOTS)


class Inventory:
    """One session's items, equipment and active job. profile_entry(job id, {slot: item id}) builds the job's entry
    of the player data (gateway_probe.profile), which ActivateProfile carries."""

    def __init__(self, character, profile_entry, guid=1, active_job=jobs.STARTER_JOB):
        owned, equipment = items.inventory(character or {}, jobs.selected()) if items.enabled() else ([], {})
        self.items = {item_id: (definition, tint) for item_id, definition, tint in owned}
        self.equipment = {job[0]: dict(equipment.get(job[0], {})) for job in jobs.selected()}
        self.active = active_job
        self.guid = guid
        self.profile_entry = profile_entry
        self.definitions = items.definitions()
        self.classes = {row[0]: row for row in reference_data.item_classes()}
        self.visible = []   # other players who would get the PlayerUpdate packets (none on this test server)

    # --- lookups ---------------------------------------------------------------------------------------------

    def equipped_item(self, slot, job=None):
        item_id = self.equipment.get(self.active if job is None else job, {}).get(slot)
        return item_id if item_id in self.items else None

    def attachment(self, slot, job=None):
        """Sanctuary Player.GetAttachment: the slot's item as the client draws it, or None."""
        item_id = self.equipped_item(slot, job)
        if item_id is None:
            return None
        definition_id, tint = self.items[item_id]
        row = self.definitions.get(definition_id)
        if row is None:
            return None
        effect = row[F_COMPOSITE]
        if slot == WEAPON_SLOT:
            flair = self.equipped_effect(FLAIR_SHARD_SLOT, job)
            if flair > 0:
                effect = flair
        return (row[F_MODEL], row[F_TEXTURE], row[F_TINT_ALIAS], tint or row[F_ICON_TINT], effect, row[F_SLOT])

    def attachments(self, job=None):
        """Sanctuary Player.GetAttachments: every equipped slot of the job, in the order they were equipped."""
        out = []
        for slot in self.equipment.get(self.active if job is None else job, {}):
            piece = self.attachment(slot, job)
            if piece:
                out.append(piece)
        return out

    def equipped_effect(self, slot, job=None):
        item_id = self.equipped_item(slot, job)
        if item_id is None:
            return 0
        row = self.definitions.get(self.items[item_id][0])
        return row[F_COMPOSITE] if row else 0

    def wield_type(self):
        """Sanctuary Player.GetWieldType: the weapon's item class's wield type, 0 without a weapon."""
        item_id = self.equipped_item(WEAPON_SLOT)
        if item_id is None:
            return 0
        row = self.definitions.get(self.items[item_id][0])
        item_class = self.classes.get(row[F_CLASS]) if row else None
        return item_class[3] if item_class else 0

    def profile(self, job):
        return self.profile_entry(job, self.equipment.get(job, {}))

    # --- requests ----------------------------------------------------------------------------------------------

    def handle(self, inner):
        """[(name, packet)] for an inventory request, or None when `inner` is not one."""
        if len(inner) < 4:
            return None
        opcode, sub = struct.unpack_from("<HH", inner)
        fields = _ints(inner)
        if opcode == 42 and sub == 3 and fields:
            job = fields[1] if len(fields) > 1 else self.active
            slot = fields[2] if len(fields) > 2 else None
            return self.equip(fields[0], job, slot)
        if opcode == 42 and sub == 8 and len(fields) >= 2:
            definition_id, tint = fields[0], fields[1]
            job = fields[2] if len(fields) > 2 else self.active
            slot = fields[3] if len(fields) > 3 else None
            item_id = next((i for i, (d, t) in self.items.items() if d == definition_id and t == tint), None)
            return self.equip(item_id, job, slot) if item_id is not None else []
        if opcode == 42 and sub == 2 and fields:
            job = fields[1] if len(fields) > 1 else self.active
            return self.unequip(fields[0], job)
        if opcode == 26 and sub == 13 and fields:
            return self.set_profile(fields[-1])
        return None

    def equip(self, item_id, job, slot=None):
        """Sanctuary InventoryPacketEquipByGuidHandler."""
        if item_id not in self.items:
            return []
        definition_id, tint = self.items[item_id]
        row = self.definitions.get(definition_id)
        if row is None or row[F_TYPE] != 1:
            return []
        if row[F_CLASS] != NO_ITEM_CLASS and row[F_CLASS] not in self.classes:
            return []
        if job not in self.equipment:
            return []
        self.equipment[job][row[F_SLOT]] = item_id
        slot = row[F_SLOT] if slot is None else slot
        piece = (row[F_MODEL], row[F_TEXTURE], row[F_TINT_ALIAS], tint or row[F_ICON_TINT], row[F_COMPOSITE], slot)
        out = [("EquipItem %d" % item_id, clothing.equip_item(item_id, piece, job))]
        if job == self.active and self.visible:
            out.append(("EquipItemChange (others)", equip_item_change(self.guid, item_id, piece, self.active, row[F_CLASS],
                                                                       self.wield_type())))
        out.append(("AbilitySetDefinition", toolbar(self.active)))
        out += self.refresh_weapon_flair(job, slot)
        return out

    def unequip(self, slot, job):
        """Sanctuary InventoryPacketEquippedRemoveHandler."""
        item_id = self.equipment.get(job, {}).get(slot)
        if item_id is None or item_id not in self.items:
            return []
        row = self.definitions.get(self.items[item_id][0])
        if row is None or (row[F_CLASS] != NO_ITEM_CLASS and row[F_CLASS] not in self.classes):
            return []
        del self.equipment[job][slot]
        out = [("UnequipSlot %d" % slot, unequip_slot(slot, job))]
        if job == self.active and self.visible:
            out.append(("EquipItemChange (others)", equip_item_change(self.guid, item_id, ("", "", "", 0, 0, slot), job, 0,
                                                                       self.wield_type())))
        out.append(("AbilitySetDefinition", toolbar(self.active)))
        out += self.refresh_weapon_flair(job, slot)
        return out

    def set_profile(self, job):
        """Sanctuary CommandPacketSetProfileHandler."""
        if job not in self.equipment:
            return []
        out = [("SlotCompositeEffectOverride (clear)", slot_composite_effect_override(self.guid, WEAPON_SLOT, 0))]
        self.active = job
        pieces = self.attachments()
        out.append(("ActivateProfile %d" % job, activate_profile(self.profile(job), pieces)))
        if self.visible:
            out.append(("EquippedItemsChange (others)", clothing.equipped_items_change(self.guid, pieces)))
        out.append(("AbilitySetDefinition", toolbar(self.active)))
        return out

    def refresh_weapon_flair(self, job, changed_slot):
        """Sanctuary Player.RefreshWeaponFlair: for the active job's weapon or flair shard slot, show the weapon again
        (EquipItemChange, also to yourself) and its effect override."""
        if job != self.active or changed_slot not in (WEAPON_SLOT, FLAIR_SHARD_SLOT):
            return []
        out = []
        item_id = self.equipped_item(WEAPON_SLOT)
        piece = self.attachment(WEAPON_SLOT)
        if item_id is not None and piece:
            row = self.definitions[self.items[item_id][0]]
            out.append(("EquipItemChange (weapon)", equip_item_change(self.guid, item_id, piece, self.active, row[F_CLASS],
                                                                      self.wield_type())))
        out.append(("SlotCompositeEffectOverride", slot_composite_effect_override(
            self.guid, WEAPON_SLOT, self.equipped_effect(FLAIR_SHARD_SLOT))))
        return out
