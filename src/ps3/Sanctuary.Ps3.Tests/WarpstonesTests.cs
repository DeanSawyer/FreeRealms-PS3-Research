using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The towns' warpstones: their AddNpc and using one.</summary>
[TestClass]
public sealed class WarpstonesTests
{
    [TestMethod]
    public void SixteenWarpstonesWithoutSunstoneValley()
    {
        Assert.AreEqual(16, Warpstones.All.Count);
        Assert.IsFalse(Warpstones.All.Any(stone => stone.Name.Contains("Sunstone") || stone.Name.Contains("Rumble")));
        Assert.AreEqual(Warpstones.All.Count, Warpstones.All.Select(stone => stone.Guid).Distinct().Count());
    }

    [TestMethod]
    public void AddNpcHasTheFieldsWhereThePs3ClientReadsThem()
    {
        var stone = Warpstones.All.First(stone => stone.Name == "Snowhill Warpstone");
        var packet = Warpstones.AddNpc(stone);
        var name = stone.Name.Length;

        // 264 bytes with every string empty (the client's reader, 0xb0c010), plus the name.
        Assert.AreEqual(264 + name, packet.Length);
        Assert.AreEqual(35, BinaryPrimitives.ReadUInt16LittleEndian(packet));
        Assert.AreEqual(2, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.AreEqual(stone.Guid, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(0x04)));
        Assert.AreEqual(stone.NameId, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0x0c)));
        Assert.AreEqual(Warpstones.ModelId, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0x10)));
        Assert.AreEqual(1f, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(0x28)), "scale");
        Assert.AreEqual(stone.X, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(0x2c)));
        Assert.AreEqual(stone.Z, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(0x34)));
        Assert.AreEqual(name, (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0x79)));
        Assert.AreEqual(stone.Name, Encoding.UTF8.GetString(packet, 0x7d, name));
        Assert.AreEqual(1, packet[0xea + name], "interactable");
    }

    [TestMethod]
    public void XAtAWarpstoneOpensTheAtlas()
    {
        var stone = Warpstones.All[0];
        var request = new byte[13];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 26);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(4), stone.Guid);

        Assert.AreEqual(stone, Warpstones.FromInteract(request));

        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(4), 1);
        Assert.IsNull(Warpstones.FromInteract(request), "another character");

        var open = Warpstones.OpenAtlas();
        Assert.AreEqual(47, BinaryPrimitives.ReadUInt16LittleEndian(open));
        Assert.AreEqual(7, open[2]);
        Assert.AreEqual("Atlas.Show", Encoding.UTF8.GetString(open, 7, (int)BinaryPrimitives.ReadUInt32LittleEndian(open.AsSpan(3))));
    }
}
