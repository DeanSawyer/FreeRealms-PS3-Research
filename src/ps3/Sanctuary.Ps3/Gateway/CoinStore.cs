using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// One item the coin store sells, as Sanctuary's <c>Resources/CoinStoreItems.json</c> lists it (its <c>ItemDefinitionMetaData</c>),
/// with the item's definition (the 34 fields of <see cref="Items.FieldKinds"/>) and its resell value.
/// </summary>
public sealed record StoreItem(
    int Id,
    int CategoryId,
    int TintGroupId,
    bool BuyDisabled,
    bool CoinStoreOnly,
    bool Hidden,
    int ResellValue,
    string[] Definition);

/// <summary>Sanctuary's <c>CoinStoreTransactionResult</c>.</summary>
public enum CoinStoreResult
{
    Success = 1,
    InvalidItem = 3,
    InvalidQuantity = 4,
    NotEnoughCoins = 7,
    Error = 8,
}

/// <summary>Sanctuary's <c>CoinStoreTransactionType</c>: the player buys from the store, or sells to it.</summary>
public enum CoinStoreTransactionType
{
    Buy = 1,
    Sell = 2,
}

/// <summary>Sanctuary's <c>CoinStoreTransactionRecord</c>: what a finished buy or sell was.</summary>
public sealed record CoinStoreTransaction(
    CoinStoreTransactionType Type,
    ulong MerchantGuid = 0,
    int Id = 0,
    long Timestamp = 0,
    int Definition = 0,
    int Tint = 0,
    int Quantity = 0,
    int Price = 0);

/// <summary>A coin store buy request (sub-opcode 4, <c>CoinStoreSellToClientRequest</c>: the store sells to the client).</summary>
public sealed record CoinStoreBuyRequest(ulong MerchantGuid, int Definition, int Tint, int Quantity);

/// <summary>A coin store sell request (sub-opcode 5, <c>CoinStoreBuyFromClientRequest</c>: the store buys from the client).</summary>
public sealed record CoinStoreSellRequest(ulong MerchantGuid, int ItemGuid, int Quantity);

/// <summary>
/// The coin store, one for one with Sanctuary's (<c>BaseCoinStorePacket</c>, opcode 165, and its handlers in Sanctuary.Gateway).
/// <list type="bullet">
/// <item>Sub-opcode 1, ItemList: every store item, sent once when the client is ready (Sanctuary <c>BaseZone.SendCoinStoreItemList</c>).</item>
/// <item>2 / 3, ItemDefinitionsRequest / Response: the client asks for definitions; Sanctuary answers success with the same ids.</item>
/// <item>4, SellToClientRequest (buy) and 5, BuyFromClientRequest (sell); both answered with 6, TransactionComplete.</item>
/// <item>8 / 9, ItemDynamicListUpdateRequest / Response: sent when the store screen opens; Sanctuary has no dynamic items.</item>
/// </list>
/// The PS3 layouts were read from <c>game.elf</c> (dispatcher <c>0x1388c8</c>, see docs/protocol/coin-store.md). Two differ from the PC:
/// an ItemList entry is the id, the category and one byte (the PC has three ints and three bools), and the transaction record ends with
/// one byte instead of the PC's quantity remaining.
/// </summary>
public static class CoinStore
{
    public const int Opcode = 165;

    public const int ItemListSub = 1;
    public const int ItemDefinitionsRequestSub = 2;
    public const int ItemDefinitionsResponseSub = 3;
    public const int BuyRequestSub = 4;
    public const int SellRequestSub = 5;
    public const int TransactionCompleteSub = 6;
    public const int DynamicListRequestSub = 8;
    public const int DynamicListResponseSub = 9;

    /// <summary>Sanctuary <c>ItemDefinitionHelper.CommunityColourIdStep</c>: store ids at or above it are recoloured copies of id % step.</summary>
    public const int CommunityColourIdStep = 10_000_000;

    /// <summary>Sanctuary <c>login.json</c> <c>StartingCoins</c>: what a character starts with.</summary>
    public const int SanctuaryStartingCoins = 999_999_999;

    /// <summary>Most definitions one ItemDefinitionsRequest is answered with (Sanctuary sends store definitions in batches of 100).</summary>
    public const int DefinitionBatch = 100;

    private const string StoreResource = "Sanctuary.Ps3.coin_store.tsv.gz";
    private const string ResellResource = "Sanctuary.Ps3.ResellValues.txt";

    // Definition fields (Items.FieldKinds order)
    private const int FieldCost = 7, FieldMemberDiscount = 32;

    private static readonly Lazy<IReadOnlyDictionary<int, StoreItem>> StoreTable = new(LoadStore);
    private static readonly Lazy<IReadOnlyDictionary<int, int>> ResellTable = new(LoadResell);

    /// <summary>The store items by id, in Sanctuary's file order.</summary>
    public static IReadOnlyDictionary<int, StoreItem> Catalog => StoreTable.Value;

    /// <summary>Sanctuary <c>ClientItemDefinition.ResellValue</c> of a definition (0 when it has none).</summary>
    public static int ResellValue(int definition) =>
        Catalog.TryGetValue(definition, out var item) ? item.ResellValue : ResellTable.Value.GetValueOrDefault(definition);

    /// <summary>Sanctuary <c>ClientItemDefinition.Cost</c>, or for a member <c>GetMemberPurchasePrice</c> (the cost less the member discount).</summary>
    public static int Price(string[] definition, bool member)
    {
        var cost = Items.Int(definition, FieldCost);

        if (!member)
            return cost;

        var discount = Items.Int(definition, FieldMemberDiscount);

        if (cost > 0 && discount - 1 <= 99)
            return (int)((1.0f - discount * 0.01f) * cost);

        return cost;
    }

    /// <summary>
    /// Sub-opcode 1, ItemList: the static items (a count, then for each its id as the key, its id, its category and a byte), then the
    /// dynamic items (none). Ordered as Sanctuary orders them: by id % <see cref="CommunityColourIdStep"/>, then by id.
    /// </summary>
    public static byte[] ItemList(IEnumerable<StoreItem>? items = null)
    {
        var list = (items ?? Catalog.Values)
            .OrderBy(item => item.Id % CommunityColourIdStep)
            .ThenBy(item => item.Id)
            .ToList();

        var writer = new PacketWriter().U16(Opcode).U16(ItemListSub).I32(list.Count);

        foreach (var item in list)
            writer.I32(item.Id).I32(item.Id).I32(item.CategoryId).U8(0);

        return writer.I32(0).ToArray();
    }

    /// <summary>Sub-opcode 3, ItemDefinitionsResponse: success (a byte) and the ids (an i32 count, then the ids).</summary>
    public static byte[] ItemDefinitionsResponse(bool success, IReadOnlyList<int> ids)
    {
        var writer = new PacketWriter().U16(Opcode).U16(ItemDefinitionsResponseSub).U8(success ? 1 : 0).I32(ids.Count);

        foreach (var id in ids)
            writer.I32(id);

        return writer.ToArray();
    }

    /// <summary>Sub-opcode 9, ItemDynamicListUpdateResponse: no dynamic items (Sanctuary's TODO).</summary>
    public static byte[] DynamicListUpdateResponse() => new PacketWriter().U16(Opcode).U16(DynamicListResponseSub).I32(0).ToArray();

    /// <summary>
    /// Sub-opcode 6, TransactionComplete (the PS3 calls it CoinStoreSellToClientResponse, reader <c>0x1368a4</c>): the result, the item's
    /// guid and the record: type, merchant guid (u64), id, time (i64 Unix seconds), definition, tint, quantity, price and one byte.
    /// </summary>
    public static byte[] TransactionComplete(CoinStoreResult result, int itemGuid, CoinStoreTransaction record) =>
        new PacketWriter()
            .U16(Opcode).U16(TransactionCompleteSub)
            .I32((int)result).I32(itemGuid)
            .I32((int)record.Type).U64(record.MerchantGuid).I32(record.Id).I64(record.Timestamp)
            .I32(record.Definition).I32(record.Tint).I32(record.Quantity).I32(record.Price).U8(0)
            .ToArray();

    /// <summary>ClientUpdate (38) sub-opcode 19, CoinCount (PS3 handler <c>0xfa3dc</c>).</summary>
    public static byte[] CoinCount(int coins) => new PacketWriter().U16(38).U16(19).I32(coins).ToArray();

    /// <summary>ClientUpdate (38) sub-opcode 3, ItemUpdate (handler <c>0xfabdc</c>): guid, count, action bar -1, consumed, ability count, rental time.</summary>
    public static byte[] ItemUpdate(int itemGuid, int count) =>
        new PacketWriter().U16(38).U16(3).I32(itemGuid).I32(count).I32(-1).I32(0).I32(0).I64(0).ToArray();

    /// <summary>ClientUpdate (38) sub-opcode 4, ItemDelete (handler <c>0xfa9e4</c>).</summary>
    public static byte[] ItemDelete(int itemGuid) => new PacketWriter().U16(38).U16(4).I32(itemGuid).ToArray();

    /// <summary>The sub-opcode of a coin store packet, or null when <paramref name="inner"/> is not one.</summary>
    public static int? Sub(ReadOnlySpan<byte> inner) =>
        inner.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == Opcode ? BinaryPrimitives.ReadUInt16LittleEndian(inner[2..]) : null;

    /// <summary>
    /// Sub-opcode 4: merchant guid (u64), then the item record (definition, tint) and the quantity. The PC sends an int between the
    /// merchant and the record (Sanctuary's MerchantUnknown); the PS3's packet object has none (sender <c>0x134278</c>), so both are read.
    /// </summary>
    public static CoinStoreBuyRequest? ReadBuy(ReadOnlySpan<byte> inner)
    {
        if (Sub(inner) != BuyRequestSub || inner.Length < 4 + 8 + 12)
            return null;

        var merchant = BinaryPrimitives.ReadUInt64LittleEndian(inner[4..]);
        var ints = Ints(inner[12..]);
        var start = ints.Count >= 4 ? ints.Count - 3 : 0;

        return new CoinStoreBuyRequest(merchant, ints[start], ints[start + 1], ints[start + 2]);
    }

    /// <summary>Sub-opcode 5: merchant guid (u64), item guid, quantity (sender <c>0x1340dc</c>, as the PC).</summary>
    public static CoinStoreSellRequest? ReadSell(ReadOnlySpan<byte> inner)
    {
        if (Sub(inner) != SellRequestSub || inner.Length < 4 + 8 + 8)
            return null;

        return new CoinStoreSellRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(inner[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(inner[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(inner[16..]));
    }

    /// <summary>Sub-opcode 2: the definition ids (an i32 count, then the ids); at most what the packet holds.</summary>
    public static List<int>? ReadDefinitionsRequest(ReadOnlySpan<byte> inner)
    {
        if (Sub(inner) != ItemDefinitionsRequestSub || inner.Length < 8)
            return null;

        var count = BinaryPrimitives.ReadInt32LittleEndian(inner[4..]);
        var ids = Ints(inner[8..]);

        return count >= 0 && count < ids.Count ? ids.GetRange(0, count) : ids;
    }

    private static List<int> Ints(ReadOnlySpan<byte> data)
    {
        var values = new List<int>();

        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));

        return values;
    }

    private static IReadOnlyDictionary<int, StoreItem> LoadStore()
    {
        using var stream = typeof(CoinStore).Assembly.GetManifestResourceStream(StoreResource)
            ?? throw new InvalidOperationException($"embedded resource {StoreResource} is missing");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        // Columns: store category, tint group, buy disabled, coin store only, hidden, resell value, then the 34 definition fields.
        const int Head = 6;
        var table = new Dictionary<int, StoreItem>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('\t');
            var definition = parts[Head..];

            if (definition.Length != Items.FieldKinds.Length)
                continue;

            int At(int index) => int.Parse(parts[index], CultureInfo.InvariantCulture);

            var id = int.Parse(definition[0], CultureInfo.InvariantCulture);

            table[id] = new StoreItem(id, At(0), At(1), At(2) != 0, At(3) != 0, At(4) != 0, At(5), definition);
        }

        return table;
    }

    private static IReadOnlyDictionary<int, int> LoadResell()
    {
        using var stream = typeof(CoinStore).Assembly.GetManifestResourceStream(ResellResource)
            ?? throw new InvalidOperationException($"embedded resource {ResellResource} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var table = new Dictionary<int, int>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('\t');

            if (parts.Length >= 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                table[id] = value;
        }

        return table;
    }
}
