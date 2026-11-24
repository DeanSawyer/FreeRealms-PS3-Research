using System;
using System.Buffers.Binary;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The NPC merchants: who sells what, and MerchantList as the PS3 reads it.</summary>
[TestClass]
public sealed class MerchantsTests
{
    [TestMethod]
    public void JobMerchantsSellTheirJobsGear()
    {
        var archer = Npcs.All.First(npc => npc.ModelFile.StartsWith("human_f_merchant_archer", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(35, Merchants.JobOf(archer));
        Assert.IsTrue(NpcTalk.WithCursor(archer with { HasCursor = false, CursorId = 0 }).HasCursor, "a merchant can be targeted");

        var stock = Merchants.StockOf(archer)!;
        var archerClasses = Jobs.ItemClasses[35].Where(pair => pair.Unknown == 5).Select(pair => pair.Class).ToHashSet();

        Assert.IsTrue(stock.Items.Count > 50, $"{stock.Items.Count} items");
        Assert.IsTrue(stock.Items.All(item => archerClasses.Contains(Items.Int(Items.Definitions[item.Definition], 8))));
        Assert.IsTrue(Merchants.JobsByModel.Values.All(job => Merchants.ForJob(job).Items.Count > 0), "every job merchant has something");

        Assert.IsNull(Merchants.StockOf(Npcs.All.First(npc => npc.Name == "Nikki")), "not a merchant");
    }

    [TestMethod]
    public void SanctuarysOwnMerchantListIsKept()
    {
        var edward = Merchants.Lists[33632];

        Assert.AreEqual(25, edward.Items.Count);
        Assert.AreEqual(13231, edward.Items[0].Definition);
        Assert.AreEqual(1000, edward.Items[0].Price);
    }

    [TestMethod]
    public void MerchantListIsAsLongAsThePs3Reads()
    {
        var stock = new MerchantStock(468, 90184, [new MerchantItem(Items.Definitions.Keys.First(), 5), new MerchantItem(Items.Definitions.Keys.Skip(1).First(), 7)]);
        var packet = Merchants.MerchantList(stock, 0x0200_0000_0000_0005, 9);

        // 0x2566f0: header, unknown, player, NPC, unknown 4, count, then 37 bytes per item.
        Assert.AreEqual(4 + 4 + 8 + 8 + 4 + 4 + 2 * 37, packet.Length);
        Assert.AreEqual(165, BinaryPrimitives.ReadUInt16LittleEndian(packet));
        Assert.AreEqual(10, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.AreEqual(0x0200_0000_0000_0005UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(0x10)));
        Assert.AreEqual(5, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0x20 + 25)), "the price");
    }
}
