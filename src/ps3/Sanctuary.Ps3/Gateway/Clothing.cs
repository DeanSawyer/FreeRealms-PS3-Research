using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One piece of clothing as the client draws it (Sanctuary's <c>CharacterAttachmentData</c>).</summary>
public readonly record struct Attachment(string Model, string TextureAlias, string TintAlias, int Tint, int CompositeEffect, int Slot);

/// <summary>One row of <c>bootstrap/data/clothing_items.tsv.gz</c>: where an item is worn and what the client draws for it.</summary>
public readonly record struct ClothingItem(int Slot, string Model, string TextureAlias, string TintAlias, int DefaultTint);

/// <summary>
/// The clothes the character creator picked, like <c>bootstrap/clothing.py</c>. The creator sends item definition ids and tints
/// (<c>ItemChest=28216</c>, <c>ItemChestTint=234</c> and so on); <c>bootstrap/data/clothing_items.tsv.gz</c> (embedded in this
/// assembly, written by <c>tools/gen_clothing_items.py</c> from Sanctuary's <c>Resources/ClientItemDefinitions.json</c>) gives each
/// id's model, texture alias, tint alias and slot.
/// </summary>
public static class Clothing
{
    private const string ResourceName = "Sanctuary.Ps3.clothing_items.tsv.gz";

    /// <summary>Creator field, in slot order (head, hands, chest, legs, feet, shoulders).</summary>
    private static readonly string[] CreatorFields = ["ItemHead", "ItemHands", "ItemChest", "ItemLegs", "ItemFeet", "ItemShoulders"];

    private static readonly Lazy<IReadOnlyDictionary<int, ClothingItem>> Table = new(Load);

    /// <summary>The table, by item definition id; read from the embedded file on first use.</summary>
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
    /// PlayerUpdate (35) sub-opcode 7, EquippedItemsChange: put <paramref name="pieces"/> on character <paramref name="guid"/>. The PS3
    /// client (handler 0x112afc) reads the guid, the attachment list (model, texture alias and tint alias strings, then tint,
    /// composite effect and slot) and one more int, which Sanctuary leaves at 0.
    /// </summary>
    public static byte[] EquippedItemsChange(ulong guid, IReadOnlyList<Attachment> pieces, int wieldType = 0)
    {
        var writer = new PacketWriter().U16(35).U16(7).U64(guid).I32(pieces.Count);

        foreach (var piece in pieces)
            writer.Str(piece.Model).Str(piece.TextureAlias).Str(piece.TintAlias).I32(piece.Tint).I32(piece.CompositeEffect).I32(piece.Slot);

        return writer.I32(wieldType).ToArray();
    }

    private static IReadOnlyDictionary<int, ClothingItem> Load()
    {
        using var stream = typeof(Clothing).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var table = new Dictionary<int, ClothingItem>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('\t');

            table[int.Parse(parts[0], CultureInfo.InvariantCulture)] = new ClothingItem(
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                parts[2],
                parts[3],
                parts[4],
                int.Parse(parts[5], CultureInfo.InvariantCulture));
        }

        return table;
    }
}
