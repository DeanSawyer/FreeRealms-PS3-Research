using System;
using System.Buffers.Binary;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The client's path requests (the quest trail, Take Me There) and the straight-line answer.</summary>
[TestClass]
public sealed class ClientPathTests
{
    // fr_test_20261124_032434: the objective helper's request after a quest was taken.
    private static readonly byte[] Request = Convert.FromHexString(
        "62000109000000000000000100000000000000ea10e842bcc507421ac7d5c30000803f356eda42214105426b9ad5c30000803f00000000");

    [TestMethod]
    public void TheRequestIsRead()
    {
        var request = ClientPath.FromRequest(Request)!;

        Assert.AreEqual(9UL, request.Guid);
        Assert.AreEqual(1, request.Type);
        Assert.AreEqual(116.03, request.FromX, 0.01);
        Assert.AreEqual(-427.21, request.ToZ, 0.01);
    }

    [TestMethod]
    public void TheReplyIsTheStraightLine()
    {
        var request = new PathRequest(9, 2, 7, 0, 0, 0, 40, 4, 0);
        var points = ClientPath.Straight(request);

        Assert.AreEqual(6, points.Count, "the start and a point every 8 units");
        Assert.AreEqual(1, points[0].Y, 0.001, "the start raised a unit");
        Assert.AreEqual((40f, 4f, 0f), points[^1]);

        var reply = ClientPath.Reply(request, points);
        Assert.AreEqual(98, BinaryPrimitives.ReadUInt16LittleEndian(reply));
        Assert.AreEqual(2, reply[2]);
        Assert.AreEqual(2, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(3)));
        Assert.AreEqual(7, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(7)));
        Assert.AreEqual(6u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(11)));
        Assert.AreEqual(15 + 6 * 16, reply.Length);

        var far = ClientPath.Straight(new PathRequest(9, 1, 0, 0, 0, 0, 5000, 0, 0));
        Assert.AreEqual(ClientPath.MaxPoints, far.Count);
    }
}
