using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// One session's inventory, one for one with Sanctuary's, like <c>bootstrap/inventory.py</c>: the items and each job's equipment (as
/// Sanctuary creates them for a new character, <see cref="Items.Inventory"/>), the active job, and the client's inventory requests.
/// The handlers follow Sanctuary.Gateway's, check for check:
/// <list type="bullet">
/// <item>Inventory (42) sub-opcode 3, EquipByGuid: the item must be owned, have a definition of type 1 whose class is 0 or a known
/// item class, and the job must exist. Reply: ClientUpdate EquipItem, EquipItemChange to the others for the active job, the toolbar,
/// the weapon flair.</item>
/// <item>Sub-opcode 8, EquipByItemRecord: the same, finding the item by definition and tint.</item>
/// <item>Sub-opcode 2, EquippedRemove: empties a job's slot. Reply: ClientUpdate UnequipSlot, the toolbar, the weapon flair.</item>
/// <item>Command (26) sub-opcode 13, SetProfile: switches job. Reply: the weapon flair cleared, ClientUpdate ActivateProfile (the job's
/// profile entry, its attachments, animation 3001, effect 4005), EquippedItemsChange to the others, the toolbar.</item>
/// </list>
/// The request layouts are not read from the client yet, so the fields are taken as little-endian ints after the u16 opcode and u16
/// sub-opcode, in Sanctuary's order, with fallbacks for anything missing at the end.
/// </summary>
public sealed class Inventory
{
    /// <summary>Sanctuary Player.WeaponSlot.</summary>
    public const int WeaponSlot = 7;

    /// <summary>Sanctuary Player.FlairShardSlot.</summary>
    public const int FlairShardSlot = 13;

    /// <summary>Sanctuary ItemDefinitionHelper.NoItemClass.</summary>
    public const int NoItemClass = 0;

    /// <summary>emo_outfit_all, played on a job switch.</summary>
    public const int JobSwitchAnimation = 3001;

    /// <summary>PFX_Job_Swirl, played on a job switch.</summary>
    public const int JobSwitchEffect = 4005;

    // Definition fields (Items.FieldKinds order)
    private const int FieldIconTint = 4, FieldClass = 8, FieldSlot = 10, FieldModel = 13, FieldTexture = 14, FieldType = 16, FieldComposite = 21, FieldTintAlias = 30;

    private readonly Dictionary<int, (int Definition, int Tint)> _items = [];
    private readonly Dictionary<int, OrderedDictionary<int, int>> _equipment = [];
    private readonly Dictionary<int, int[]> _classes;
    private readonly bool _itemClasses;
    private readonly ulong _guid;

    /// <summary>The character's items and each chosen job's equipment, as <see cref="Ps3Packets.CharacterItems"/> gives them.</summary>
    public Inventory(IReadOnlyDictionary<string, string> character, GatewayOptions options, ulong guid = 1, int activeJob = Jobs.StarterJob)
    {
        var (owned, equipment) = Ps3Packets.CharacterItems(character, options);

        foreach (var item in owned)
            _items[item.Id] = (item.Definition, item.Tint);

        foreach (var job in Jobs.Selected(options.Jobs))
        {
            var slots = new OrderedDictionary<int, int>();

            foreach (var (slot, itemId) in equipment.GetValueOrDefault(job.Id) ?? new Dictionary<int, int>())
                slots[slot] = itemId;

            _equipment[job.Id] = slots;
        }

        _classes = ReferenceData.ItemClasses.ToDictionary(row => row[0]);
        _itemClasses = options.Items;
        _guid = guid;
        Active = activeJob;
    }

    /// <summary>The active job.</summary>
    public int Active { get; private set; }

    /// <summary>Other players who would get the PlayerUpdate packets; none on this test server.</summary>
    public List<ulong> Visible { get; } = [];

    /// <summary>A job's equipment, {slot: item id} in the order the slots were filled; empty for a job the character lacks.</summary>
    public IReadOnlyDictionary<int, int> Equipment(int job) =>
        _equipment.TryGetValue(job, out var slots) ? slots : new Dictionary<int, int>();

    /// <summary>The replies to an inventory request, or null when <paramref name="inner"/> is not one.</summary>
    public List<NamedPacket>? Handle(byte[] inner)
    {
        if (inner.Length < 4)
            return null;

        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(inner);
        var sub = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(2));
        var fields = Ints(inner);

        if (opcode == 42 && sub == 3 && fields.Count > 0)
            return Equip(fields[0], fields.Count > 1 ? fields[1] : Active, fields.Count > 2 ? fields[2] : null);

        if (opcode == 42 && sub == 8 && fields.Count >= 2)
        {
            var job = fields.Count > 2 ? fields[2] : Active;
            int? slot = fields.Count > 3 ? fields[3] : null;

            foreach (var (itemId, (definition, tint)) in _items)
            {
                if (definition == fields[0] && tint == fields[1])
                    return Equip(itemId, job, slot);
            }

            return [];
        }

        if (opcode == 42 && sub == 2 && fields.Count > 0)
            return Unequip(fields[0], fields.Count > 1 ? fields[1] : Active);

        if (opcode == 26 && sub == 13 && fields.Count > 0)
            return SetProfile(fields[^1]);

        return null;
    }

    /// <summary>Sanctuary InventoryPacketEquipByGuidHandler.</summary>
    public List<NamedPacket> Equip(int itemId, int job, int? slot = null)
    {
        if (!_items.TryGetValue(itemId, out var item))
            return [];

        if (!Items.Definitions.TryGetValue(item.Definition, out var row) || Items.Int(row, FieldType) != 1)
            return [];

        var itemClass = Items.Int(row, FieldClass);

        if (itemClass != NoItemClass && !_classes.ContainsKey(itemClass))
            return [];

        if (!_equipment.TryGetValue(job, out var slots))
            return [];

        slots[Items.Int(row, FieldSlot)] = itemId;

        var pieceSlot = slot ?? Items.Int(row, FieldSlot);
        var piece = Piece(row, item.Tint, Items.Int(row, FieldComposite), pieceSlot);
        var replies = new List<NamedPacket> { new NamedPacket($"EquipItem {itemId}", Clothing.EquipItem(itemId, piece, job)) };

        if (job == Active && Visible.Count > 0)
            replies.Add(new NamedPacket("EquipItemChange (others)", EquipItemChange(_guid, itemId, piece, Active, itemClass, WieldType())));

        replies.Add(new NamedPacket("AbilitySetDefinition", SanctuaryFlow.Toolbar(Active)));
        replies.AddRange(RefreshWeaponFlair(job, pieceSlot));

        return replies;
    }

    /// <summary>Sanctuary InventoryPacketEquippedRemoveHandler.</summary>
    public List<NamedPacket> Unequip(int slot, int job)
    {
        if (!_equipment.TryGetValue(job, out var slots) || !slots.TryGetValue(slot, out var itemId) || !_items.TryGetValue(itemId, out var item))
            return [];

        if (!Items.Definitions.TryGetValue(item.Definition, out var row))
            return [];

        var itemClass = Items.Int(row, FieldClass);

        if (itemClass != NoItemClass && !_classes.ContainsKey(itemClass))
            return [];

        slots.Remove(slot);

        var replies = new List<NamedPacket> { new NamedPacket($"UnequipSlot {slot}", UnequipSlot(slot, job)) };

        if (job == Active && Visible.Count > 0)
            replies.Add(new NamedPacket("EquipItemChange (others)", EquipItemChange(_guid, itemId, new Attachment(string.Empty, string.Empty, string.Empty, 0, 0, slot), job, 0, WieldType())));

        replies.Add(new NamedPacket("AbilitySetDefinition", SanctuaryFlow.Toolbar(Active)));
        replies.AddRange(RefreshWeaponFlair(job, slot));

        return replies;
    }

    /// <summary>Sanctuary CommandPacketSetProfileHandler.</summary>
    public List<NamedPacket> SetProfile(int job)
    {
        if (!_equipment.ContainsKey(job))
            return [];

        var replies = new List<NamedPacket> { new NamedPacket("SlotCompositeEffectOverride (clear)", SlotCompositeEffectOverride(_guid, WeaponSlot, 0)) };

        Active = job;

        var pieces = Attachments();

        replies.Add(new NamedPacket($"ActivateProfile {job}", ActivateProfile(Ps3Packets.Profile(job, _equipment[job], _itemClasses), pieces)));

        if (Visible.Count > 0)
            replies.Add(new NamedPacket("EquippedItemsChange (others)", Clothing.EquippedItemsChange(_guid, pieces)));

        replies.Add(new NamedPacket("AbilitySetDefinition", SanctuaryFlow.Toolbar(Active)));

        return replies;
    }

    /// <summary>ClientUpdate (38) sub-opcode 6, UnequipSlot: slot, job.</summary>
    public static byte[] UnequipSlot(int slot, int profileId) => new PacketWriter().U16(38).U16(6).I32(slot).I32(profileId).ToArray();

    /// <summary>ClientUpdate (38) sub-opcode 21, ActivateProfile: i32-length profile entry, attachment list, animation, effect, an unused int.</summary>
    public static byte[] ActivateProfile(byte[] profileEntry, IReadOnlyList<Attachment> pieces, int animation = JobSwitchAnimation, int effect = JobSwitchEffect)
    {
        var writer = new PacketWriter().U16(38).U16(21).I32(profileEntry.Length).Raw(profileEntry).I32(pieces.Count);

        foreach (var piece in pieces)
            WriteAttachment(writer, piece);

        return writer.I32(animation).I32(effect).I32(0).ToArray();
    }

    /// <summary>PlayerUpdate (35) sub-opcode 6, EquipItemChange (what the other players see).</summary>
    public static byte[] EquipItemChange(ulong guid, int itemId, Attachment piece, int profileId, int itemClass, int wieldType)
    {
        var writer = new PacketWriter().U16(35).U16(6).U64(guid).I32(itemId);

        WriteAttachment(writer, piece);

        return writer.I32(profileId).I32(itemClass).I32(wieldType).ToArray();
    }

    /// <summary>PlayerUpdate (35) sub-opcode 31, SlotCompositeEffectOverride.</summary>
    public static byte[] SlotCompositeEffectOverride(ulong guid, int slot, int effect) =>
        new PacketWriter().U16(35).U16(31).U64(guid).I32(slot).I32(effect).ToArray();

    private int? EquippedItem(int slot, int? job = null)
    {
        if (_equipment.TryGetValue(job ?? Active, out var slots) && slots.TryGetValue(slot, out var itemId) && _items.ContainsKey(itemId))
            return itemId;

        return null;
    }

    /// <summary>Sanctuary Player.GetAttachment: the slot's item as the client draws it, or null.</summary>
    private Attachment? AttachmentOf(int slot, int? job = null)
    {
        if (EquippedItem(slot, job) is not { } itemId)
            return null;

        var (definition, tint) = _items[itemId];

        if (!Items.Definitions.TryGetValue(definition, out var row))
            return null;

        var effect = Items.Int(row, FieldComposite);

        if (slot == WeaponSlot)
        {
            var flair = EquippedEffect(FlairShardSlot, job);

            if (flair > 0)
                effect = flair;
        }

        return Piece(row, tint, effect, Items.Int(row, FieldSlot));
    }

    /// <summary>Sanctuary Player.GetAttachments: every equipped slot of the active job, in the order they were equipped.</summary>
    private List<Attachment> Attachments()
    {
        var pieces = new List<Attachment>();

        foreach (var slot in (_equipment.GetValueOrDefault(Active) ?? new OrderedDictionary<int, int>()).Keys)
        {
            if (AttachmentOf(slot) is { } piece)
                pieces.Add(piece);
        }

        return pieces;
    }

    private int EquippedEffect(int slot, int? job = null)
    {
        if (EquippedItem(slot, job) is not { } itemId)
            return 0;

        return Items.Definitions.TryGetValue(_items[itemId].Definition, out var row) ? Items.Int(row, FieldComposite) : 0;
    }

    /// <summary>Sanctuary Player.GetWieldType: the weapon's item class's wield type, 0 without a weapon.</summary>
    private int WieldType()
    {
        if (EquippedItem(WeaponSlot) is not { } itemId || !Items.Definitions.TryGetValue(_items[itemId].Definition, out var row))
            return 0;

        return _classes.TryGetValue(Items.Int(row, FieldClass), out var itemClass) ? itemClass[3] : 0;
    }

    /// <summary>
    /// Sanctuary Player.RefreshWeaponFlair: for the active job's weapon or flair shard slot, show the weapon again (EquipItemChange, also to
    /// yourself) and its effect override.
    /// </summary>
    private List<NamedPacket> RefreshWeaponFlair(int job, int changedSlot)
    {
        if (job != Active || (changedSlot != WeaponSlot && changedSlot != FlairShardSlot))
            return [];

        var replies = new List<NamedPacket>();

        if (EquippedItem(WeaponSlot) is { } itemId && AttachmentOf(WeaponSlot) is { } piece)
        {
            var row = Items.Definitions[_items[itemId].Definition];

            replies.Add(new NamedPacket("EquipItemChange (weapon)", EquipItemChange(_guid, itemId, piece, Active, Items.Int(row, FieldClass), WieldType())));
        }

        replies.Add(new NamedPacket("SlotCompositeEffectOverride", SlotCompositeEffectOverride(_guid, WeaponSlot, EquippedEffect(FlairShardSlot))));

        return replies;
    }

    private static Attachment Piece(string[] row, int tint, int effect, int slot) =>
        new(row[FieldModel], row[FieldTexture], row[FieldTintAlias], tint != 0 ? tint : Items.Int(row, FieldIconTint), effect, slot);

    private static void WriteAttachment(PacketWriter writer, Attachment piece) =>
        writer.Str(piece.Model).Str(piece.TextureAlias).Str(piece.TintAlias).I32(piece.Tint).I32(piece.CompositeEffect).I32(piece.Slot);

    /// <summary>The little-endian ints after the u16 opcode and u16 sub-opcode.</summary>
    private static List<int> Ints(byte[] inner)
    {
        var values = new List<int>();

        for (var offset = 4; offset + 4 <= inner.Length; offset += 4)
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(inner.AsSpan(offset)));

        return values;
    }
}
