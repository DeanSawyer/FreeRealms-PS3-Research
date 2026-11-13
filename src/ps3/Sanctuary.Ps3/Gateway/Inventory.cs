using System;
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
    private const int FieldIconTint = 4, FieldClass = 8, FieldSlot = 10, FieldModel = 13, FieldTexture = 14, FieldType = 16, FieldComposite = 21, FieldMaxStack = 28, FieldTintAlias = 30;

    /// <summary>Sanctuary ItemDefinitionHelper.PetClothingItemType and PetUtilityItemType: bought as plain items.</summary>
    private const int PetClothingItemType = 3, PetUtilityItemType = 4;

    private readonly Dictionary<int, (int Definition, int Tint)> _items = [];
    private readonly Dictionary<int, int> _counts = [];
    private int _transactions;
    private readonly Dictionary<int, OrderedDictionary<int, int>> _equipment = [];
    private readonly Dictionary<int, int[]> _classes;
    private readonly bool _itemClasses;
    private readonly ulong _guid;

    /// <summary>The character's items and each chosen job's equipment, as <see cref="Ps3Packets.CharacterItems"/> gives them.</summary>
    public Inventory(IReadOnlyDictionary<string, string> character, GatewayOptions options, ulong guid = 1, int activeJob = Jobs.StarterJob)
        : this(Loadout.ForNewCharacter(character, options) with { ActiveJob = activeJob }, options, guid)
    {
    }

    /// <summary>
    /// A saved character's inventory: <paramref name="loadout"/>'s items, each chosen job's equipment (a job the loadout lacks starts
    /// empty) and its active job (the first job when that job is no longer chosen, <c>FR_JOBS</c>).
    /// </summary>
    public Inventory(Loadout loadout, GatewayOptions options, ulong guid = 1)
    {
        foreach (var item in loadout.Items)
        {
            _items[item.Id] = (item.Definition, item.Tint);
            _counts[item.Id] = item.Count > 0 ? item.Count : 1;
        }

        foreach (var job in Jobs.Selected(options.Jobs))
        {
            var slots = new OrderedDictionary<int, int>();

            foreach (var (slot, itemId) in loadout.Equipment.GetValueOrDefault(job.Id) ?? new Dictionary<int, int>())
                slots[slot] = itemId;

            _equipment[job.Id] = slots;
        }

        _classes = ReferenceData.ItemClasses.ToDictionary(row => row[0]);
        _itemClasses = options.Items;
        _guid = guid;
        Coins = loadout.Coins;
        Active = _equipment.ContainsKey(loadout.ActiveJob) || _equipment.Count == 0 ? loadout.ActiveJob : Jobs.StarterJob;
    }

    /// <summary>The character's coins (the coin store changes them).</summary>
    public int Coins { get; private set; }

    /// <summary>Adds <paramref name="coins"/> (the starting coins), stopping at int.MaxValue.</summary>
    public void GiveCoins(int coins) => Coins = (int)Math.Clamp((long)Coins + coins, 0, int.MaxValue);

    /// <summary>How many of an item the character has (0 when it has none).</summary>
    public int Count(int itemId) => _items.ContainsKey(itemId) ? _counts.GetValueOrDefault(itemId, 1) : 0;

    /// <summary>What the character has now, to save: the items, each job's equipment in order, the active job and the coins.</summary>
    public Loadout Snapshot() => new(
        [.. _items.OrderBy(pair => pair.Key).Select(pair => new OwnedItem(pair.Key, pair.Value.Definition, pair.Value.Tint, Count(pair.Key)))],
        _equipment.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<int, int>)new OrderedDictionary<int, int>(pair.Value)),
        Active,
        Coins);

    /// <summary>What the active job wears, as the client draws it (to dress a saved character after it loads).</summary>
    public IReadOnlyList<Attachment> ActiveAttachments() => Attachments();

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

    /// <summary>
    /// The replies to a coin store packet (<see cref="CoinStore"/>, opcode 165) or an item definition request (PlayerUpdate 35/34), or null
    /// when <paramref name="inner"/> is neither. <paramref name="now"/> is the Unix time in seconds for the transaction record.
    /// </summary>
    public List<NamedPacket>? HandleCoinStore(byte[] inner, long now, bool member = true)
    {
        if (inner.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == 35 && BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(2)) == 34)
        {
            // PlayerUpdate ItemDefinitionRequest: the definition, as Sanctuary's handler sends it (one at a time here).
            var id = BinaryPrimitives.ReadInt32LittleEndian(inner.AsSpan(4));

            return Items.Definitions.ContainsKey(id) ? [new NamedPacket($"ItemDefinition {id}", Items.ItemDefinition(id))] : [];
        }

        switch (CoinStore.Sub(inner))
        {
            case CoinStore.ItemDefinitionsRequestSub when CoinStore.ReadDefinitionsRequest(inner) is { } ids:
            {
                // Sanctuary answers success with the same ids (its definitions go out through the PlayerUpdate request); the PS3 also
                // gets the definitions it asked for, so the store can show them.
                var replies = ids.Distinct().Where(Items.Definitions.ContainsKey).Take(CoinStore.DefinitionBatch)
                    .Select(id => new NamedPacket($"ItemDefinition {id}", Items.ItemDefinition(id)))
                    .ToList();

                replies.Add(new NamedPacket("CoinStoreItemDefinitionsResponse", CoinStore.ItemDefinitionsResponse(true, ids)));

                return replies;
            }

            case CoinStore.BuyRequestSub when CoinStore.ReadBuy(inner) is { } buy:
                return Buy(buy, now, member);

            case CoinStore.SellRequestSub when CoinStore.ReadSell(inner) is { } sell:
                return Sell(sell, now);

            case CoinStore.DynamicListRequestSub:
                return [new NamedPacket("CoinStoreItemDynamicListUpdateResponse", CoinStore.DynamicListUpdateResponse())];

            default:
                return null;
        }
    }

    /// <summary>
    /// Sanctuary CoinStoreSellToClientRequestPacketHandler (the player buys). The definition is the store id % the community colour
    /// step and must be of an item type Sanctuary sells as an item; the price is the definition's cost (a member's: less the member
    /// discount) times the quantity; the tint is the one asked for only when the store item has a tint group, otherwise the icon tint;
    /// a stackable item goes on the stack of the same definition and tint, within its maximum stack size. Replies: ItemAdd (a new item)
    /// or ItemUpdate, CoinCount and TransactionComplete. Pets (type 2) are not sold here: this server has no pets. A quantity below 1
    /// is refused (Sanctuary checks it only for pets).
    /// </summary>
    public List<NamedPacket> Buy(CoinStoreBuyRequest request, long now, bool member = true)
    {
        List<NamedPacket> Fail(CoinStoreResult result) =>
            [new NamedPacket($"CoinStoreTransactionComplete {result}", CoinStore.TransactionComplete(result, 0, new CoinStoreTransaction(CoinStoreTransactionType.Buy)))];

        var definition = request.Definition % CoinStore.CommunityColourIdStep;

        if (!Items.Definitions.TryGetValue(definition, out var row))
            return Fail(CoinStoreResult.InvalidItem);

        if (Items.Int(row, FieldType) is not (1 or PetClothingItemType or PetUtilityItemType or 12))
            return Fail(CoinStoreResult.Error);

        if (request.Quantity < 1)
            return Fail(CoinStoreResult.InvalidQuantity);

        var total = (long)CoinStore.Price(row, member) * request.Quantity;

        if (Coins < total)
            return Fail(CoinStoreResult.NotEnoughCoins);

        var hasTintGroup = CoinStore.Catalog.TryGetValue(definition, out var storeItem) && storeItem.TintGroupId != 0;
        var tint = hasTintGroup ? request.Tint : Items.Int(row, FieldIconTint);
        var owned = _items.Where(pair => pair.Value.Definition == definition && (!hasTintGroup || pair.Value.Tint == tint)).Sum(pair => (long)Count(pair.Key));
        var maxStack = Items.Int(row, FieldMaxStack);

        if (maxStack > 0 && owned + request.Quantity > maxStack)
            return Fail(CoinStoreResult.InvalidQuantity);

        var existing = _items.Where(pair => pair.Value.Definition == definition && pair.Value.Tint == tint).Select(pair => (int?)pair.Key).FirstOrDefault();
        var itemId = existing ?? (_items.Count == 0 ? 1 : _items.Keys.Max() + 1);

        _items[itemId] = (definition, tint);
        _counts[itemId] = (int)Math.Min((long)(existing is null ? 0 : Count(itemId)) + request.Quantity, int.MaxValue);
        Coins = (int)(Coins - total);

        var replies = new List<NamedPacket>
        {
            existing is null
                ? new NamedPacket($"ItemAdd {itemId}", Items.ItemAdd(new OwnedItem(itemId, definition, tint, _counts[itemId]), _counts[itemId]))
                : new NamedPacket($"ItemUpdate {itemId}", CoinStore.ItemUpdate(itemId, _counts[itemId])),
            new NamedPacket("CoinCount", CoinStore.CoinCount(Coins)),
        };

        var record = new CoinStoreTransaction(CoinStoreTransactionType.Buy, 0, ++_transactions, now, definition, tint, request.Quantity);

        replies.Add(new NamedPacket("CoinStoreTransactionComplete Success", CoinStore.TransactionComplete(CoinStoreResult.Success, itemId, record)));

        return replies;
    }

    /// <summary>
    /// Sanctuary CoinStoreBuyFromClientRequestPacketHandler (the player sells): the item must be owned in that quantity; the coins go up
    /// by the definition's resell value times the quantity. Replies: ItemDelete (all of it sold) or ItemUpdate, CoinCount and
    /// TransactionComplete. A sold item is also taken out of every job's equipment, so the saved character never wears an item it no
    /// longer has. A quantity below 1 is refused.
    /// </summary>
    public List<NamedPacket> Sell(CoinStoreSellRequest request, long now)
    {
        List<NamedPacket> Fail(CoinStoreResult result) =>
            [new NamedPacket($"CoinStoreTransactionComplete {result}", CoinStore.TransactionComplete(result, 0, new CoinStoreTransaction(CoinStoreTransactionType.Sell)))];

        if (!_items.TryGetValue(request.ItemGuid, out var item))
            return Fail(CoinStoreResult.InvalidItem);

        var count = Count(request.ItemGuid);

        if (request.Quantity < 1 || count < request.Quantity)
            return Fail(CoinStoreResult.InvalidQuantity);

        if (!Items.Definitions.ContainsKey(item.Definition))
            return Fail(CoinStoreResult.InvalidItem);

        var sold = count == request.Quantity;

        if (sold)
        {
            _items.Remove(request.ItemGuid);
            _counts.Remove(request.ItemGuid);

            foreach (var slots in _equipment.Values)
            {
                foreach (var slot in slots.Where(pair => pair.Value == request.ItemGuid).Select(pair => pair.Key).ToList())
                    slots.Remove(slot);
            }
        }
        else
        {
            _counts[request.ItemGuid] = count - request.Quantity;
        }

        GiveCoins((int)Math.Min((long)CoinStore.ResellValue(item.Definition) * request.Quantity, int.MaxValue));

        var replies = new List<NamedPacket>
        {
            sold
                ? new NamedPacket($"ItemDelete {request.ItemGuid}", CoinStore.ItemDelete(request.ItemGuid))
                : new NamedPacket($"ItemUpdate {request.ItemGuid}", CoinStore.ItemUpdate(request.ItemGuid, count - request.Quantity)),
            new NamedPacket("CoinCount", CoinStore.CoinCount(Coins)),
        };

        var record = new CoinStoreTransaction(CoinStoreTransactionType.Sell, 0, ++_transactions, now, item.Definition, item.Tint, request.Quantity);

        replies.Add(new NamedPacket("CoinStoreTransactionComplete Success", CoinStore.TransactionComplete(CoinStoreResult.Success, request.ItemGuid, record)));

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
