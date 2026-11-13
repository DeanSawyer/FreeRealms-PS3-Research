using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The coin store: Sanctuary's items and prices, buying and selling as its handlers do, and the PS3 packet layouts.</summary>
[TestClass]
public sealed class CoinStoreTests
{
    private static readonly Dictionary<string, string> Creator = new()
    {
        ["FirstName"] = "Aedon", ["LastName"] = "Amberarm", ["Model"] = "1", ["Head"] = "1",
        ["ItemChest"] = "28216", ["ItemChestTint"] = "234", ["ItemLegs"] = "392", ["ItemLegsTint"] = "262",
        ["ItemFeet"] = "17397", ["ItemFeetTint"] = "234",
    };

    private static readonly GatewayOptions Options = new() { Sanctuary = true, CoinStore = true };

    /// <summary>Gold Farmer Helmet: type 1, tint group 1, cost 320 with a 10% member discount, no stack limit, no resell value.</summary>
    private const int Helmet = 2;

    /// <summary>A pet utility item (type 4): no tint group (icon tint 228), cost 15, at most one, resell value 1.</summary>
    private const int Bowl = 8;

    private const long Now = 1_763_000_000;

    private static byte[] Buy(int definition, int tint, int quantity, bool pcLayout = false)
    {
        var writer = new PacketWriter().U16(165).U16(4).U64(0);

        if (pcLayout)
            writer.I32(0);

        return writer.I32(definition).I32(tint).I32(quantity).ToArray();
    }

    private static byte[] Sell(int itemGuid, int quantity) => new PacketWriter().U16(165).U16(5).U64(0).I32(itemGuid).I32(quantity).ToArray();

    private static CoinStoreResult Result(List<NamedPacket> replies) =>
        (CoinStoreResult)BinaryPrimitives.ReadInt32LittleEndian(replies[^1].Inner.AsSpan(4));

    private static int ItemGuid(List<NamedPacket> replies) => BinaryPrimitives.ReadInt32LittleEndian(replies[^1].Inner.AsSpan(8));

    [TestMethod]
    public void TheStoreHasSanctuarysItemsAndTheirDefinitions()
    {
        Assert.AreEqual(13096, CoinStore.Catalog.Count);
        Assert.AreEqual(101, CoinStore.Catalog[Helmet].CategoryId);
        Assert.AreEqual(1, CoinStore.Catalog[Helmet].TintGroupId);
        Assert.IsTrue(CoinStore.Catalog.Keys.All(Items.Definitions.ContainsKey), "every store item can be shown and owned");

        // Sanctuary GetMemberPurchasePrice: 320 less 10%.
        Assert.AreEqual(288, CoinStore.Price(Items.Definitions[Helmet], member: true));
        Assert.AreEqual(320, CoinStore.Price(Items.Definitions[Helmet], member: false));
        Assert.AreEqual(1, CoinStore.ResellValue(Bowl));
    }

    [TestMethod]
    public void TheItemListHasEveryItemAsThePs3ReadsIt()
    {
        var list = CoinStore.ItemList();

        Assert.AreEqual(4 + 4 + (13 * CoinStore.Catalog.Count) + 4, list.Length, "id key, id, category, one byte each; no dynamic items");
        Assert.AreEqual(CoinStore.Catalog.Count, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(4)));

        // Sanctuary's order: by id % 10,000,000, then id; so the first entry is the lowest id.
        var first = CoinStore.Catalog.Keys.Min(id => id % CoinStore.CommunityColourIdStep);

        Assert.AreEqual(first, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(8)));
        Assert.AreEqual(first, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(12)));
    }

    [TestMethod]
    public void BuyingTakesTheMemberPriceAndStacksAsSanctuaryDoes()
    {
        var inventory = new Inventory(Creator, Options);

        inventory.GiveCoins(1000);

        var bought = inventory.HandleCoinStore(Buy(Helmet, 99, 1), Now)!;

        CollectionAssert.AreEqual(new[] { "ItemAdd", "CoinCount", "CoinStoreTransactionComplete" }, bought.Select(packet => packet.Name.Split(' ')[0]).ToArray());
        Assert.AreEqual(CoinStoreResult.Success, Result(bought));
        Assert.AreEqual(712, inventory.Coins);
        CollectionAssert.AreEqual(CoinStore.CoinCount(712), bought[1].Inner);

        var helmet = inventory.Snapshot().Items.Single(item => item.Definition == Helmet);

        Assert.AreEqual(99, helmet.Tint, "a store item with a tint group keeps the tint asked for");
        Assert.AreEqual(helmet.Id, ItemGuid(bought));

        // The transaction record: buy, no merchant, id 1, the time, the item, the quantity; 53 bytes in all.
        var record = bought[^1].Inner;

        Assert.AreEqual(53, record.Length);
        Assert.AreEqual(1, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12)));
        Assert.AreEqual(1, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(24)));
        Assert.AreEqual(Now, BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(28)));
        Assert.AreEqual(Helmet, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(36)));

        // The same item and tint again goes on the same stack (no stack limit): ItemUpdate with the count.
        var again = inventory.HandleCoinStore(Buy(Helmet, 99, 2, pcLayout: true), Now)!;

        Assert.AreEqual("ItemUpdate", again[0].Name.Split(' ')[0]);
        CollectionAssert.AreEqual(CoinStore.ItemUpdate(helmet.Id, 3), again[0].Inner);
        Assert.AreEqual(3, inventory.Count(helmet.Id));
        Assert.AreEqual(712 - (2 * 288), inventory.Coins);

        // No tint group: the icon tint, whatever was asked; at most one.
        Assert.AreEqual(CoinStoreResult.Success, Result(inventory.HandleCoinStore(Buy(Bowl, 5, 1), Now)!));
        Assert.AreEqual(Items.Int(Items.Definitions[Bowl], 4), inventory.Snapshot().Items.Single(item => item.Definition == Bowl).Tint);
        Assert.AreEqual(CoinStoreResult.InvalidQuantity, Result(inventory.HandleCoinStore(Buy(Bowl, 5, 1), Now)!));

        // Refusals change nothing.
        var coins = inventory.Coins;

        Assert.AreEqual(CoinStoreResult.NotEnoughCoins, Result(inventory.HandleCoinStore(Buy(Helmet, 99, 1000), Now)!));
        Assert.AreEqual(CoinStoreResult.InvalidItem, Result(inventory.HandleCoinStore(Buy(9_999_999, 0, 1), Now)!));
        Assert.AreEqual(CoinStoreResult.InvalidQuantity, Result(inventory.HandleCoinStore(Buy(Helmet, 99, 0), Now)!));
        Assert.AreEqual(coins, inventory.Coins);
    }

    [TestMethod]
    public void SellingPaysTheResellValueAndTakesTheItemOff()
    {
        var inventory = new Inventory(Creator, Options);

        inventory.GiveCoins(1000);
        inventory.HandleCoinStore(Buy(Bowl, 0, 1), Now);
        inventory.HandleCoinStore(Buy(Helmet, 7, 2), Now);

        var start = inventory.Coins;
        var items = inventory.Snapshot().Items;
        var bowl = items.Single(item => item.Definition == Bowl).Id;
        var helmet = items.Single(item => item.Definition == Helmet).Id;

        // All of it: ItemDelete, the resell value.
        var sold = inventory.HandleCoinStore(Sell(bowl, 1), Now)!;

        CollectionAssert.AreEqual(CoinStore.ItemDelete(bowl), sold[0].Inner);
        Assert.AreEqual(CoinStoreResult.Success, Result(sold));
        Assert.AreEqual(start + 1, inventory.Coins);
        Assert.AreEqual(0, inventory.Count(bowl));

        // Part of a stack: ItemUpdate with what is left.
        var some = inventory.HandleCoinStore(Sell(helmet, 1), Now)!;

        CollectionAssert.AreEqual(CoinStore.ItemUpdate(helmet, 1), some[0].Inner);
        Assert.AreEqual(1, inventory.Count(helmet));

        // More than owned, or nothing owned.
        Assert.AreEqual(CoinStoreResult.InvalidQuantity, Result(inventory.HandleCoinStore(Sell(helmet, 2), Now)!));
        Assert.AreEqual(CoinStoreResult.InvalidItem, Result(inventory.HandleCoinStore(Sell(bowl, 1), Now)!));

        // A worn item that is sold is no longer worn.
        var worn = inventory.Equipment(Jobs.StarterJob).First();

        inventory.HandleCoinStore(Sell(worn.Value, 1), Now);

        Assert.IsFalse(inventory.Equipment(Jobs.StarterJob).ContainsKey(worn.Key));
    }

    [TestMethod]
    public void TheStoreScreenAndDefinitionRequestsAreAnswered()
    {
        var inventory = new Inventory(Creator, Options);

        // Opening the store screen: no dynamic items.
        var opened = inventory.HandleCoinStore(new PacketWriter().U16(165).U16(8).ToArray(), Now)!;

        CollectionAssert.AreEqual(CoinStore.DynamicListUpdateResponse(), opened.Single().Inner);

        // Definitions asked for: each known one, then the response with the same ids.
        var asked = inventory.HandleCoinStore(new PacketWriter().U16(165).U16(2).I32(2).I32(Helmet).I32(Bowl).ToArray(), Now)!;

        CollectionAssert.AreEqual(Items.ItemDefinition(Helmet), asked[0].Inner);
        CollectionAssert.AreEqual(Items.ItemDefinition(Bowl), asked[1].Inner);
        CollectionAssert.AreEqual(CoinStore.ItemDefinitionsResponse(true, [Helmet, Bowl]), asked[2].Inner);

        // PlayerUpdate ItemDefinitionRequest.
        CollectionAssert.AreEqual(Items.ItemDefinition(Helmet), inventory.HandleCoinStore(new PacketWriter().U16(35).U16(34).I32(Helmet).ToArray(), Now)!.Single().Inner);

        // Not a coin store packet.
        Assert.IsNull(inventory.HandleCoinStore(new PacketWriter().U16(42).U16(3).I32(1).ToArray(), Now));
    }

    [TestMethod]
    public void CountsAndTheStartingCoinsAreSaved()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fr-coins-{Guid.NewGuid():N}", "characters.json");

        try
        {
            var store = new CharacterStore(file);
            var guid = store.Create(Creator.Select(pair => (pair.Key, pair.Value)));
            var inventory = new Inventory(Creator, Options);

            inventory.GiveCoins(1000);
            inventory.HandleCoinStore(Buy(Helmet, 3, 2), Now);

            Assert.IsTrue(store.SaveProgress(guid, SavedProgress.ToProgress(inventory.Snapshot(), gotStartingCoins: true)));

            var read = new CharacterStore(file).Find(guid)!.Progress!;
            var back = new Inventory(SavedProgress.ToLoadout(read), Options);
            var helmet = back.Snapshot().Items.Single(item => item.Definition == Helmet);

            Assert.IsTrue(read.GotStartingCoins);
            Assert.AreEqual(1000 - (2 * 288), back.Coins);
            Assert.AreEqual(2, helmet.Count);
            Assert.AreEqual(2, BinaryPrimitives.ReadInt32LittleEndian(Items.ItemAdd(helmet, helmet.Count).AsSpan(8 + 12)), "the login ItemAdd carries the count");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
