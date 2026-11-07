using System;
using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One piece of clothing as the client draws it (Sanctuary's <c>CharacterAttachmentData</c>).</summary>
public readonly record struct Attachment(string Model, string TextureAlias, string TintAlias, int Tint, int CompositeEffect, int Slot);

/// <summary>What the client draws for an item: where it is worn, its model, texture and tint alias, and its icon's tint.</summary>
public readonly record struct ClothingItem(int Slot, string Model, string TextureAlias, string TintAlias, int DefaultTint);

/// <summary>
/// The clothes the character creator picked, like <c>bootstrap/clothing.py</c>. The creator sends item definition ids and tints
/// (<c>ItemChest=28216</c>, <c>ItemChestTint=234</c> and so on); the item definitions (<see cref="Items"/>) give each id's model,
/// texture alias, tint alias and slot.
/// </summary>
public static class Clothing
{
    /// <summary>Creator field, in slot order (head, hands, chest, legs, feet, shoulders).</summary>
    private static readonly string[] CreatorFields = ["ItemHead", "ItemHands", "ItemChest", "ItemLegs", "ItemFeet", "ItemShoulders"];

    private static readonly Lazy<IReadOnlyDictionary<int, ClothingItem>> Table = new(Load);

    /// <summary>The definitions' drawing fields, by item definition id.</summary>
    public static IReadOnlyDictionary<int, ClothingItem> Items => Table.Value;

    /// <summary>
    /// The pieces the creator picked that the table has, in slot order. The tint is the creator's, or the definition's icon tint when
    /// that is 0 (Sanctuary's <c>Player.GetAttachment</c>); there is no composite effect. Ids the table lacks, and 0, are left out.
    /// </summary>
    public static List<Attachment> Attachments(IReadOnlyDictionary<string, string> character)
    {
        var pieces = new List<Attachment>();

        foreach (var field in CreatorFields)
        {
            var id = CharacterMappings.Number(character, field, 0);

            if (id <= 0 || !Items.TryGetValue(id, out var item))
                continue;

            var tint = CharacterMappings.Number(character, field + "Tint", 0);

            pieces.Add(new Attachment(item.Model, item.TextureAlias, item.TintAlias, tint != 0 ? tint : item.DefaultTint, 0, item.Slot));
        }

        return pieces;
    }

    /// <summary>
    /// ClientUpdate (38) sub-opcode 5, EquipItem: put one piece on your own character in profile <paramref name="profileId"/>, as
    /// Sanctuary tells your client when you equip something. The PS3 client (handler 0xfa600, then 0xfec54) reads the item id, the
    /// attachment, the profile id and one unused int; the PC packet's trailing Equip bool is not there.
    /// </summary>
    public static byte[] EquipItem(int itemId, Attachment piece, int profileId) =>
        new PacketWriter().U16(38).U16(5).I32(itemId)
            .Str(piece.Model).Str(piece.TextureAlias).Str(piece.TintAlias).I32(piece.Tint).I32(piece.CompositeEffect).I32(piece.Slot)
            .I32(profileId).I32(0)
            .ToArray();

    /// <summary>
    /// PlayerUpdate (35) sub-opcode 7, EquippedItemsChange: put <paramref name="pieces"/> on character <paramref name="guid"/>. The PS3
    /// client (handler 0x112afc) reads the guid, the attachment list (model, texture alias and tint alias strings, then tint,
    /// composite effect and slot) and one more int, which Sanctuary leaves at 0. It is what Sanctuary sends when you change jobs, and
    /// the client plays the job change effect (4005) before it puts the clothes on, so the gateway only uses it with <c>FR_EQUIP=7</c>.
    /// </summary>
    public static byte[] EquippedItemsChange(ulong guid, IReadOnlyList<Attachment> pieces, int wieldType = 0)
    {
        var writer = new PacketWriter().U16(35).U16(7).U64(guid).I32(pieces.Count);

        foreach (var piece in pieces)
            writer.Str(piece.Model).Str(piece.TextureAlias).Str(piece.TintAlias).I32(piece.Tint).I32(piece.CompositeEffect).I32(piece.Slot);

        return writer.I32(wieldType).ToArray();
    }

    private static IReadOnlyDictionary<int, ClothingItem> Load() =>
        Gateway.Items.Definitions.ToDictionary(
            entry => entry.Key,
            entry => new ClothingItem(
                Gateway.Items.Int(entry.Value, Gateway.Items.SlotField),
                entry.Value[Gateway.Items.ModelField],
                entry.Value[Gateway.Items.TextureAliasField],
                entry.Value[Gateway.Items.TintAliasField],
                Gateway.Items.Int(entry.Value, Gateway.Items.IconTintField)));
}
