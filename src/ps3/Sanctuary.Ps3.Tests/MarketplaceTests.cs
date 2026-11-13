using System;
using System.Buffers.Binary;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The Marketplace requests the PS3 sends at login, answered as Sanctuary answers them, under the PS3's sub-opcode numbers.</summary>
[TestClass]
public sealed class MarketplaceTests
{
    private static byte[] Request(int sub, string? locale = "en_US")
    {
        var writer = new PacketWriter().U16(66).U16(sub);

        return locale is null ? writer.ToArray() : writer.Str(locale).ToArray();
    }

    [TestMethod]
    public void TheLoginRequestsAreAnsweredUnderThePs3Numbers()
    {
        // As the client sent them on 13 November: 29, 15 and 21 with the locale, 19 without.
        Assert.AreEqual(30, Sub(Marketplace.Handle(Request(29))!.Single().Inner));
        Assert.AreEqual(16, Sub(Marketplace.Handle(Request(15))!.Single().Inner));
        Assert.AreEqual(22, Sub(Marketplace.Handle(Request(21))!.Single().Inner));
        Assert.AreEqual(20, Sub(Marketplace.Handle(Request(19, null))!.Single().Inner));

        Assert.IsNull(Marketplace.Handle(Request(10, null)));
        Assert.IsNull(Marketplace.Handle(new PacketWriter().U16(165).U16(15).ToArray()));
    }

    [TestMethod]
    public void TheRepliesCarrySanctuarysValues()
    {
        CollectionAssert.AreEqual(new byte[] { 0x42, 0, 28, 0, 1 }, Marketplace.EnableMarketplace());
        CollectionAssert.AreEqual(new PacketWriter().U16(66).U16(30).I32(1).Str("US").Str("USD").U8(0).ToArray(), Marketplace.AccountInfo());

        // The currency map's key: Sanctuary's JenkinsHelper.OneAtATimeHash("USD").
        Assert.AreEqual(1330388359u, Marketplace.OneAtATimeHash("USD"));

        var currencies = Marketplace.CurrencyCodes("en_US");

        Assert.AreEqual(1, BinaryPrimitives.ReadInt32LittleEndian(currencies.AsSpan(4)), "error code 1");
        Assert.AreEqual(1330388359u, BinaryPrimitives.ReadUInt32LittleEndian(currencies.AsSpan(4 + 4 + 4 + 5 + 4)));
    }

    private static int Sub(byte[] inner) => BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(2));
}
