using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One thing a merchant sells: the item, its price and Sanctuary's MerchantList entry fields.</summary>
public sealed record MerchantItem(int Definition, int Price, int PurchasableQuantity = -1, bool MembersOnly = false, int Unknown9 = 0, int TintGroup = 0);

/// <summary>A merchant's list: Sanctuary's two unknowns and the items.</summary>
public sealed record MerchantStock(int Unknown, int Unknown4, IReadOnlyList<MerchantItem> Items);

/// <summary>
/// The NPC merchants. Sanctuary has one merchant list (Assistant Chef Edward's, <c>Data/merchants.tsv</c>); the open world's job
/// merchants (their models are <c>*_merchant_&lt;job&gt;_*</c>: archer, brawler, chef, ...) sell their job's gear: the coin store's
/// items (Sanctuary's <c>CoinStoreItems.json</c>, not hidden, buyable) whose item class is one of the job's own classes (the
/// classes Sanctuary's profile gives rank 5, <see cref="Jobs.ItemClasses"/>), lowest rank first. X at one sends CoinStore (165) 10,
/// MerchantList; buying and selling are the coin store's (sub-opcodes 4 and 5, with the merchant's guid).
/// <para>The PS3 reads MerchantList at <c>0x2566f0</c> (entries <c>0xba40e4</c>): <c>i32</c> unknown, <c>i64</c> player guid,
/// <c>u64</c> NPC guid, <c>i32</c> unknown 4, <c>u32</c> count, then per item <c>i32</c> definition, icon, tint, name, description,
/// purchasable quantity, <c>u8</c> members only, <c>i32</c> price, unknown 9 and tint group: 37 bytes. Sanctuary's PC entry ends with
/// a CanBuy <c>u8</c> the PS3 does not read.</para>
/// </summary>
public static class Merchants
{
    public const int MerchantListSub = 10;

    // Definition fields (Items.FieldKinds order).
    private const int FieldName = 1, FieldDescription = 2, FieldIcon = 3, FieldIconTint = 4, FieldClass = 8, FieldType = 16,
        FieldMembersOnly = 18, FieldMinRank = 23;

    private static readonly Regex JobModel = new("_merchant_(?<job>[a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The job merchant models' job names and the job ids.</summary>
    public static readonly IReadOnlyDictionary<string, int> JobsByModel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["ninja"] = 2, ["postman"] = 4, ["medic"] = 11, ["wizard"] = 12, ["miner"] = 14, ["blacksmith"] = 16, ["warrior"] = 32,
        ["archer"] = 35, ["brawler"] = 43, ["chef"] = 45, ["kart"] = 48, ["demolition"] = 49,
    };

    private static readonly Lazy<IReadOnlyDictionary<int, MerchantStock>> Listed = new(LoadListed);
    private static readonly Dictionary<int, MerchantStock> JobStock = [];

    /// <summary>The merchants with their own list in Sanctuary, by NPC id.</summary>
    public static IReadOnlyDictionary<int, MerchantStock> Lists => Listed.Value;

    /// <summary>The job <paramref name="npc"/> sells for, from its model, or null when it is not a job merchant.</summary>
    public static int? JobOf(NpcSpawn npc)
        => JobModel.Match(npc.ModelFile) is { Success: true } match && JobsByModel.TryGetValue(match.Groups["job"].Value, out var job) ? job : null;

    /// <summary>What <paramref name="npc"/> sells, or null when it is no merchant.</summary>
    public static MerchantStock? StockOf(NpcSpawn npc)
    {
        if (Lists.TryGetValue(npc.NpcId, out var own))
            return own;

        return JobOf(npc) is { } job ? ForJob(job) : null;
    }

    /// <summary>Whether <paramref name="npc"/> sells anything.</summary>
    public static bool IsMerchant(NpcSpawn npc) => Lists.ContainsKey(npc.NpcId) || JobOf(npc) is not null;

    /// <summary>A job merchant's stock: the coin store's items of the job's own classes, lowest rank first, then cheapest.</summary>
    public static MerchantStock ForJob(int job)
    {
        lock (JobStock)
        {
            if (JobStock.TryGetValue(job, out var cached))
                return cached;

            var classes = Jobs.ItemClasses.TryGetValue(job, out var list) ? list.Where(pair => pair.Unknown == 5).Select(pair => pair.Class).ToHashSet() : new HashSet<int>();

            var items = CoinStore.Catalog.Values
                .Where(item => !item.Hidden && !item.BuyDisabled && item.Id < CoinStore.CommunityColourIdStep)
                .Where(item => classes.Contains(Items.Int(item.Definition, FieldClass)) && Items.Int(item.Definition, FieldType) == 1)
                .OrderBy(item => Items.Int(item.Definition, FieldMinRank))
                .ThenBy(item => CoinStore.Price(item.Definition, member: true))
                .ThenBy(item => item.Id)
                .Select(item => new MerchantItem(item.Id, CoinStore.Price(item.Definition, member: true), -1, false, 0, item.TintGroupId))
                .ToList();

            var name = Jobs.All.FirstOrDefault(entry => entry.Id == job).NameId;

            return JobStock[job] = new MerchantStock(0, name, items);
        }
    }

    /// <summary>CoinStore (165) 10, MerchantList for <paramref name="npcGuid"/>, as the PS3 reads it.</summary>
    public static byte[] MerchantList(MerchantStock stock, ulong npcGuid, ulong player)
    {
        var entries = stock.Items.Where(item => Items.Definitions.ContainsKey(item.Definition)).ToList();
        var writer = new PacketWriter().U16(CoinStore.Opcode).U16(MerchantListSub)
            .I32(stock.Unknown).I64(unchecked((long)player)).U64(npcGuid).I32(stock.Unknown4).U32((uint)entries.Count);

        foreach (var item in entries)
        {
            var row = Items.Definitions[item.Definition];

            writer.I32(item.Definition).I32(Items.Int(row, FieldIcon)).I32(Items.Int(row, FieldIconTint))
                .I32(Items.Int(row, FieldName)).I32(Items.Int(row, FieldDescription)).I32(item.PurchasableQuantity)
                .U8(item.MembersOnly || Items.Int(row, FieldMembersOnly) != 0 ? 1 : 0)
                .I32(item.Price).I32(item.Unknown9).I32(item.TintGroup);
        }

        return writer.ToArray();
    }

    private static IReadOnlyDictionary<int, MerchantStock> LoadListed()
    {
        using var stream = typeof(Merchants).Assembly.GetManifestResourceStream("Sanctuary.Ps3.merchants.tsv")
            ?? throw new InvalidOperationException("Sanctuary.Ps3.merchants.tsv is not in the assembly.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>The merchant lists in the data file's text (see its header).</summary>
    public static IReadOnlyDictionary<int, MerchantStock> Parse(string text)
    {
        var all = new Dictionary<int, MerchantStock>();

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            int I(string value) => int.Parse(value, CultureInfo.InvariantCulture);

            var items = c[3].Split(',').Select(entry => entry.Split('/'))
                .Select(f => new MerchantItem(I(f[0]), I(f[1]), I(f[2]), f[3] == "1", I(f[4]), I(f[5])))
                .ToList();

            all[I(c[0])] = new MerchantStock(I(c[1]), I(c[2]), items);
        }

        return all;
    }
}
