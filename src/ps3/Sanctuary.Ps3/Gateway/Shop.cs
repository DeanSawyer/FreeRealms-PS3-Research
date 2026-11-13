using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// The Station Cash shop of the Marketplace, from Sanctuary's resources (<c>Resources/Stores.json</c>, <c>StoreBundles.json</c>,
/// <c>StoreBundleCategories.json</c>, <c>StoreBundleCategoryGroups.json</c>, <c>StoreBundleGroups.json</c>, embedded as
/// <c>Data/shop/*.json.gz</c>), sent as Sanctuary's <c>BaseZone.SendInGamePurchase</c> sends it but in the PS3's older layouts:
/// a store bundle is a PS3 "store item" (reader <c>0xa980e0</c>, field names from its debug print at <c>0x8d4d40</c>).
/// <list type="bullet">
/// <item>Category groups: PS3 42 (PC 6), as the PC: id, then the category ids.</item>
/// <item>Categories: PS3 14 (PC 7): id, name, image, tint, parent (the client files children under the fifth int).</item>
/// <item>The store and its items: PS3 5 type 2 (PC 5 type 1): store id, the store, then each item.</item>
/// <item>Item groups: PS3 35 (PC 9): id, name, description, image, tint, exclusive, then (store item id, order) pairs.</item>
/// </list>
/// Fields marked as guesses in docs/protocol/coin-store.md (the categories' fifth int, the groups' third int, the order of an item's
/// three entry ints) are not checked against the PS3 yet.
/// </summary>
public static class Shop
{
    public const int CategoryGroupsSub = 42;
    public const int CategoriesSub = 14;
    public const int StoreItemsSub = 5;
    public const int StoreItemsResponseType = 2;
    public const int ItemGroupsSub = 35;

    /// <summary>Sanctuary's main store (<c>Stores.json</c> id 1).</summary>
    public const int MainStore = 1;

    private static readonly Lazy<JsonElement[]> Bundles = new(() => Load("StoreBundles.json"));
    private static readonly Lazy<JsonElement[]> Categories = new(() => Load("StoreBundleCategories.json"));
    private static readonly Lazy<JsonElement[]> CategoryGroups = new(() => Load("StoreBundleCategoryGroups.json"));
    private static readonly Lazy<JsonElement[]> Groups = new(() => Load("StoreBundleGroups.json"));
    private static readonly Lazy<JsonElement[]> Stores = new(() => Load("Stores.json"));

    /// <summary>How many bundles the main store has.</summary>
    public static int BundleCount => MainBundles().Count();

    /// <summary>The Marketplace packets, in Sanctuary's order after EnableMarketplace and the payment sources.</summary>
    public static List<NamedPacket> Packets() =>
    [
        new NamedPacket("InGamePurchaseStoreItemCategoryGroups", CategoryGroupsPacket()),
        new NamedPacket("InGamePurchaseStoreItemCategories", CategoriesPacket()),
        new NamedPacket("InGamePurchaseStoreItems", StoreItemsPacket()),
        new NamedPacket("InGamePurchaseStoreItemGroups", ItemGroupsPacket()),
    ];

    public static byte[] CategoryGroupsPacket()
    {
        var groups = CategoryGroups.Value;
        var writer = new PacketWriter().U16(Marketplace.Opcode).U16(CategoryGroupsSub).I32(groups.Length);

        foreach (var group in groups)
        {
            var ids = group.TryGetProperty("CategoryIds", out var list) ? list.EnumerateArray().Select(id => id.GetInt32()).ToList() : new List<int>();

            writer.I32(Int(group, "Id")).I32(Int(group, "Id")).I32(ids.Count);

            foreach (var id in ids)
                writer.I32(id);
        }

        return writer.ToArray();
    }

    public static byte[] CategoriesPacket()
    {
        var categories = Categories.Value;
        var writer = new PacketWriter().U16(Marketplace.Opcode).U16(CategoriesSub).I32(categories.Length);

        foreach (var category in categories)
        {
            writer.I32(Int(category, "Id")).I32(Int(category, "Id")).I32(Int(category, "NameId"))
                .I32(Image(category)).I32(Tint(category)).I32(Int(category, "ParentId"));
        }

        // The PS3 reads nothing after the categories (0xa97f80); the PC's "sorted" byte made the client refuse the packet.
        return writer.ToArray();
    }

    public static byte[] StoreItemsPacket()
    {
        var store = Stores.Value.FirstOrDefault(entry => Int(entry, "Id") == MainStore);
        var bundles = MainBundles().ToList();
        var writer = new PacketWriter().U16(Marketplace.Opcode).U16(StoreItemsSub).I32(StoreItemsResponseType).I32(MainStore);

        writer.I32(MainStore)
            .I32(store.ValueKind == JsonValueKind.Object ? Int(store, "NameId") : 0)
            .I32(store.ValueKind == JsonValueKind.Object ? Int(store, "DescriptionId") : 0)
            .I32(store.ValueKind == JsonValueKind.Object ? Image(store) : 0)
            .I32(store.ValueKind == JsonValueKind.Object ? Tint(store) : 0)
            .I32(bundles.Count);

        foreach (var bundle in bundles)
        {
            writer.I32(Int(bundle, "Id"));
            StoreItem(writer, bundle);
        }

        return writer.ToArray();
    }

    public static byte[] ItemGroupsPacket()
    {
        var groups = Groups.Value;
        var writer = new PacketWriter().U16(Marketplace.Opcode).U16(ItemGroupsSub).I32(groups.Length);

        foreach (var group in groups)
        {
            var entries = group.TryGetProperty("Entries", out var map) && map.ValueKind == JsonValueKind.Object
                ? map.EnumerateObject().Select(pair => (Id: Int(pair.Value, "StoreBundleId"), Order: Int(pair.Value, "DisplayOrder"))).ToList()
                : new List<(int Id, int Order)>();

            writer.I32(Int(group, "Id")).I32(Int(group, "Id")).I32(Int(group, "NameId")).I32(Int(group, "DescriptionId"))
                .I32(Image(group)).I32(Tint(group)).U8(Bool(group, "IsExclusive") ? 1 : 0).I32(entries.Count);

            foreach (var (id, order) in entries)
                writer.I32(id).I32(order);
        }

        return writer.ToArray();
    }

    /// <summary>One bundle as a PS3 store item (wire order of reader <c>0xa980e0</c>).</summary>
    private static void StoreItem(PacketWriter writer, JsonElement bundle)
    {
        var entries = bundle.TryGetProperty("Entries", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : new List<JsonElement>();

        writer.I32(Int(bundle, "StoreId", MainStore)).I32(Int(bundle, "Id")).I32(Int(bundle, "CategoryGroupId"))
            .I32(Int(bundle, "NameId")).I32(Int(bundle, "DescriptionId")).I32(Image(bundle)).I32(Tint(bundle))
            .I32(Int(bundle, "Price")).I32(Int(bundle, "SalePrice")).I32(Int(bundle, "MembersOnlyPrice"))
            .U8(Bool(bundle, "New") ? 1 : 0).U8(Bool(bundle, "IsViewOnly") ? 1 : 0).U8(Bool(bundle, "IsMembersOnly") ? 1 : 0).U8(0)
            .I32(0).I32(0) // rental period, rental price
            .I32(Int(bundle, "TermsAndConditionsId")).I32(Int(bundle, "MaxPerOrder")).I32(Int(bundle, "DisplayOrder"))
            .I32(Int(bundle, "CategoryDisplayOrder")).I32(Int(bundle, "LimitedSeriesId")).U8(Bool(bundle, "IsTintable") ? 1 : 0)
            .I32(entries.Count);

        foreach (var entry in entries)
            writer.I32(Int(entry, "Quantity", 1)).I32(Int(entry, "GameItemId")).I32(Int(entry, "ImageTintOverride"));
    }

    /// <summary>The main store's visible bundles, as Sanctuary lists them: each bundle followed by its community colours.</summary>
    private static IEnumerable<JsonElement> MainBundles()
    {
        var main = Bundles.Value.Where(bundle => Int(bundle, "StoreId", MainStore) == MainStore).ToList();
        var colours = main.Where(bundle => Int(bundle, "Id") >= CoinStore.CommunityColourIdStep)
            .ToLookup(bundle => Int(bundle, "Id") % CoinStore.CommunityColourIdStep);

        return main.Where(bundle => Int(bundle, "Id") < CoinStore.CommunityColourIdStep)
            .SelectMany(bundle => colours[Int(bundle, "Id")].Prepend(bundle));
    }

    private static int Image(JsonElement element) =>
        element.TryGetProperty("Image", out var image) && image.ValueKind == JsonValueKind.Object
        && image.TryGetProperty("Image", out var id)
        && int.TryParse(id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static int Tint(JsonElement element) =>
        element.TryGetProperty("Image", out var image) && image.ValueKind == JsonValueKind.Object ? Int(image, "Tint") : 0;

    private static int Int(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static JsonElement[] Load(string name)
    {
        var resource = $"Sanctuary.Ps3.shop.{name}.gz";

        using var stream = typeof(Shop).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded resource {resource} is missing");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
    }
}
