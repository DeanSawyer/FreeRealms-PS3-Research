using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The open world's NPCs: the data, their AddNpc and RemovePlayer, and which ones a client gets.</summary>
[TestClass]
public sealed class NpcsTests
{
    [TestMethod]
    public void EverySpawnIsLoadedWithItsOwnGuid()
    {
        Assert.IsTrue(Npcs.All.Count > 5000, $"{Npcs.All.Count} NPCs");
        Assert.AreEqual(Npcs.All.Count, Npcs.All.Select(npc => npc.Guid).Distinct().Count());
        Assert.IsFalse(Npcs.All.Any(npc => npc.Name.Contains("Warpstone")), "the warpstones are Warpstones' own");
        Assert.IsTrue(Npcs.All.All(npc => npc.ModelFile.EndsWith(".adr", StringComparison.OrdinalIgnoreCase) || npc.ModelFile.EndsWith(".agr", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AddNpcIsAsLongAsTheClientReads()
    {
        var npc = Npcs.All.First(npc => npc.Name.Length > 0 && npc.TextureAlias.Length > 0);
        var packet = Npcs.AddNpc(npc);
        var strings = Encoding.UTF8.GetByteCount(npc.Name) + Encoding.UTF8.GetByteCount(npc.TextureAlias) + Encoding.UTF8.GetByteCount(npc.TintAlias);

        // 264 bytes with every string empty (0xb0c010), plus the strings.
        Assert.AreEqual(264 + strings, packet.Length);
        Assert.AreEqual(npc.Guid, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(4)));
        Assert.AreEqual(npc.ModelId, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0x10)));
        Assert.AreEqual(npc.Scale, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(0x28)));
    }

    [TestMethod]
    public void RemovePlayerNamesTheGuid()
    {
        var packet = Npcs.RemoveNpc(0x0200_0000_0000_0005);

        CollectionAssert.AreEqual(new byte[] { 35, 0, 3, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 2 }, packet);
    }

    [TestMethod]
    public void NearOnesAreAddedAndFarOnesRemoved()
    {
        var near = Npcs.All[0] with { X = 0, Z = 0 };
        var far = Npcs.All[1] with { X = 1000, Z = 0 };
        var relevance = new NpcRelevance([near, far]);

        var (add, remove) = relevance.Update(10, 0);
        CollectionAssert.AreEqual(new[] { near }, add);
        Assert.AreEqual(0, remove.Count);

        Assert.AreEqual(0, relevance.Update(12, 0).Add.Count, "not moved far enough to look again");

        (add, remove) = relevance.Update(950, 0);
        CollectionAssert.AreEqual(new[] { far }, add);
        CollectionAssert.AreEqual(new[] { near }, remove);
    }
}
