using System;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Install;

namespace Sanctuary.Ps3.Tests;

[TestClass]
public sealed class ZsContainerTests
{
    [TestMethod]
    public void StoredVariantsMatchReference()
    {
        foreach (var (index, label, data) in ReferenceVectors.StoredVariants)
        {
            var (actual, actualLabel) = ZsContainer.EncodeStored(index, ReferenceVectors.StoredSample);

            Assert.AreEqual(label, actualLabel, $"variant {index}");
            CollectionAssert.AreEqual(data, actual, $"variant {index}");
        }

        Assert.AreEqual(7, ZsContainer.StoredVariantLabels.Count);
    }

    [TestMethod]
    public void StoredZlibVariantMatchesReferenceAcrossBlockBoundaries()
    {
        foreach (var (rawLength, encodedLength, encodedCrc) in ReferenceVectors.StoredZlib)
        {
            var (actual, _) = ZsContainer.EncodeStored(6, Pattern(rawLength));

            Assert.AreEqual(encodedLength, actual.Length, $"length for {rawLength} bytes");
            Assert.AreEqual(encodedCrc, Sanctuary.Ps3.Assets.Crc32.Compute(actual), $"crc for {rawLength} bytes");
        }
    }

    [TestMethod]
    public void Adler32MatchesTheKnownValue()
    {
        Assert.AreEqual(0x11E60398u, ZsContainer.Adler32(Encoding.ASCII.GetBytes("Wikipedia")));
        Assert.AreEqual(1u, ZsContainer.Adler32([]));
    }

    [TestMethod]
    public void WithoutATargetTheContainerIsZlibOrRawPlusTheZeroTrailer()
    {
        foreach (var (label, raw) in Samples())
        {
            var container = new ZsContainer().Encode(raw, null);

            AssertZeroTrailer(container, label);
            AssertPayloadDecodesTo(container[..^ZsContainer.TrailerLength], raw, label);
        }
    }

    [TestMethod]
    public void PayloadReachesTheLengthTheDigestNames()
    {
        foreach (var (label, raw) in Samples())
        {
            var natural = ZsContainer.PayloadLength(raw);

            if (natural == raw.Length)
                continue; // stored: covered below

            for (var extra = 0; extra <= 13; extra++)
            {
                var container = new ZsContainer().Encode(raw, natural + extra);

                Assert.AreEqual(natural + extra + ZsContainer.TrailerLength, container.Length, $"{label}: natural {natural} + {extra}");
                AssertZeroTrailer(container, label);
                AssertPayloadDecodesTo(container[..^ZsContainer.TrailerLength], raw, $"{label} + {extra}");
            }
        }
    }

    [TestMethod]
    public void AFileListedAsStoredIsSentRaw()
    {
        foreach (var (label, raw) in Samples())
        {
            var payload = new ZsContainer().Payload(raw, raw.Length);

            CollectionAssert.AreEqual(raw, payload, label);
        }
    }

    [TestMethod]
    public void AnUnreachableTargetStillGivesAValidContainer()
    {
        var raw = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("free realms ", 300)));

        var container = new ZsContainer().Encode(raw, 3);

        AssertZeroTrailer(container, "unreachable");
        AssertPayloadDecodesTo(container[..^ZsContainer.TrailerLength], raw, "unreachable");
    }

    [TestMethod]
    public void TheDigestTrailerCodecAppendsTheDigestTail()
    {
        var tail = new byte[ZsContainer.TrailerLength];

        for (var index = 0; index < tail.Length; index++)
            tail[index] = (byte)(255 - index);

        var raw = Encoding.ASCII.GetBytes("some file");
        var container = new ZsContainer(ZsCodec.DigestTrailer, tail).Encode(raw, null);

        CollectionAssert.AreEqual(tail, container[^ZsContainer.TrailerLength..]);
        Assert.ThrowsExactly<InvalidOperationException>(() => new ZsContainer(ZsCodec.DigestTrailer, new byte[5]));
    }

    [TestMethod]
    public void StoredBlocksSurviveAnInflater()
    {
        foreach (var length in new[] { 0, 1, 65535, 65536, 131071 })
        {
            var raw = Pattern(length);

            CollectionAssert.AreEqual(raw, ZsContainer.Inflate(ZsContainer.StoredZlib(raw)), $"{length} bytes");
        }
    }

    private static byte[] Pattern(int count)
    {
        var bytes = new byte[count];

        for (var index = 0; index < count; index++)
            bytes[index] = (byte)(((index * 7) + 3) % 251);

        return bytes;
    }

    private static (string Label, byte[] Raw)[] Samples()
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Free Realms terrain calendar definitions, line of text. ", 8)))[..403];

        var noise = new byte[3000];
        new Random(12345).NextBytes(noise);

        var structured = new byte[100_000];

        for (var index = 0; index < structured.Length; index++)
            structured[index] = (byte)((index / 7) ^ (index % 13));

        return
        [
            ("empty", []),
            ("one byte", [42]),
            ("403 bytes of text", text),
            ("3000 random bytes", noise),
            ("100 KB structured", structured),
        ];
    }

    private static void AssertZeroTrailer(byte[] container, string label)
    {
        Assert.IsTrue(container.Length >= ZsContainer.TrailerLength, label + ": too short");

        var trailer = container[^ZsContainer.TrailerLength..];

        Assert.AreEqual((byte)1, trailer[0], label + ": trailer marker");
        Assert.IsTrue(trailer.AsSpan(1).IndexOfAnyExcept((byte)0) < 0, label + ": trailer body must be zero");
    }

    private static void AssertPayloadDecodesTo(byte[] payload, byte[] raw, string label)
    {
        if (payload.AsSpan().SequenceEqual(raw))
            return; // stored raw

        CollectionAssert.AreEqual(raw, ZsContainer.Inflate(payload), label);
    }
}
