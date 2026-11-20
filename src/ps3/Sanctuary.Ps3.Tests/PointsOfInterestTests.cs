using System;
using System.Buffers.Binary;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The atlas's points of interest and teleporting to them.</summary>
[TestClass]
public sealed class PointsOfInterestTests
{
    [TestMethod]
    public void EveryPointIsLoaded()
    {
        Assert.IsTrue(PointsOfInterest.All.Count >= 100, $"{PointsOfInterest.All.Count} points");

        var snowhill = PointsOfInterest.All[3];
        Assert.AreEqual(3961, snowhill.NameId);
        Assert.AreEqual(51.391f, snowhill.Position.X, 0.001f);
        Assert.AreEqual(363.156f, snowhill.Position.Z, 0.001f);
        Assert.AreEqual("Snowhill", snowhill.Comment);
    }

    [TestMethod]
    public void TheReplyHasTwelveFieldsPerPoint()
    {
        var reply = PointsOfInterest.DefinitionReply();
        var count = PointsOfInterest.All.Count;

        Assert.AreEqual(57, BinaryPrimitives.ReadUInt16LittleEndian(reply));
        Assert.AreEqual(reply.Length - 6, (int)BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(2)), "the payload's length");
        Assert.AreEqual(6 + count * (1 + 12 * 4) + 1, reply.Length);
        Assert.AreEqual(1, reply[6], "a point follows");
        Assert.AreEqual(0, reply[^1], "no more points");

        var first = PointsOfInterest.All.Values.OrderBy(point => point.Id).First();
        Assert.AreEqual(first.Id, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(7)));
        Assert.AreEqual(first.TeleportLocationId, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(7 + 11 * 4)));
    }

    [TestMethod]
    public void ATeleportRequestNamesAPoint()
    {
        byte[] request = [90, 0, 3, 0, 0, 0];

        Assert.AreEqual(3, PointsOfInterest.FromRequest(request)?.Id);
        Assert.IsNull(PointsOfInterest.FromRequest([90, 0, 0x7f, 0x7f, 0, 0]), "no such point");
        Assert.IsNull(PointsOfInterest.FromRequest([91, 0, 3, 0, 0, 0]), "another opcode");
    }

    [TestMethod]
    public void AnActivityPointWithoutASpawnSpotIsLandedOnFromAbove()
    {
        var table = new PointOfInterest(1, 2, 3, (10, 5, 20), (10, 5, 20), 0, 7546, 6, 0, 0, 0, "table");
        var town = new PointOfInterest(1, 2, 3, (10, 5, 20), (10, 5, 20), 0, 1345, 7, 0, 0, 0, "town");

        Assert.AreEqual(8, PointsOfInterest.Landing(table).Y, 0.001);
        Assert.AreEqual(5, PointsOfInterest.Landing(town).Y, 0.001);
        Assert.AreEqual(1, PointsOfInterest.Landing(town).RotationZ, 0.001, "heading 0 faces +z");
    }

    [TestMethod]
    public void UpdateLocationIsAClientUpdateTwelveTeleport()
    {
        var packet = PointsOfInterest.UpdateLocation(new Placement(1, 2, 3, 0, 0, 1));

        Assert.AreEqual(38, BinaryPrimitives.ReadUInt16LittleEndian(packet));
        Assert.AreEqual(12, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.AreEqual(2 + 2 + 16 + 16 + 2, packet.Length);
        Assert.AreEqual(2f, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(8)));
        Assert.AreEqual(1, packet[^2], "teleport");
    }
}
