using System;
using System.Linq;
using System.Security.Cryptography;
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
    public void DeflateIsByteForByteWhatZlibWrites()
    {
        foreach (var (kind, length, level, packedLength, packedCrc) in ReferenceVectors.Compressed)
        {
            var packed = ZsContainer.Deflate(Sample(kind, length), level);
            var label = $"{kind} x {length} at level {level}";

            Assert.AreEqual(packedLength, packed.Length, label + ": length");
            Assert.AreEqual(packedCrc, Sanctuary.Ps3.Assets.Crc32.Compute(packed), label + ": bytes");
        }
    }

    [TestMethod]
    public void ALevelThatReproducesTheTargetIsUsedAsIs()
    {
        foreach (var (label, raw) in Samples())
        {
            foreach (var level in new[] { 9, 6, 1 })
            {
                var packed = ZsContainer.Deflate(raw, level);

                if (packed.Length >= raw.Length)
                    continue;

                CollectionAssert.AreEqual(packed, new ZsContainer().Payload(raw, packed.Length), $"{label}: level {level}");
            }
        }
    }

    [TestMethod]
    public void PayloadIsPaddedToTheLengthTheDigestNames()
    {
        foreach (var (label, raw) in Samples())
        {
            var natural = ZsContainer.PayloadLength(raw);

            if (natural == raw.Length)
                continue; // stored: covered below

            for (var extra = 24; extra <= 45; extra++)
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

    /// <summary>The inputs of the reference compressor vectors; the formulas are the ones in tools/gen_ps3_vectors.py.</summary>
    private static byte[] Sample(string kind, int count)
    {
        switch (kind)
        {
            case "text":
                var unit = Encoding.ASCII.GetBytes("Free Realms terrain calendar definitions, line of text. ");
                var text = new byte[count];

                for (var index = 0; index < count; index++)
                    text[index] = unit[index % unit.Length];

                return text;

            case "pattern":
                return Pattern(count);

            case "structured":
                var structured = new byte[count];

                for (var index = 0; index < count; index++)
                    structured[index] = (byte)(((index / 7) ^ (index % 13)) & 0xFF);

                return structured;

            case "noise":
                var noise = new byte[count];
                var filled = 0;

                for (var block = 0; filled < count; block++)
                {
                    var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"noise:{block}"));
                    var take = Math.Min(hash.Length, count - filled);

                    hash.AsSpan(0, take).CopyTo(noise.AsSpan(filled));
                    filled += take;
                }

                return noise;

            default:
                throw new ArgumentException(kind);
        }
    }

    private static (string Label, byte[] Raw)[] Samples()
    {
        return
        [
            ("empty", Sample("text", 0)),
            ("one byte", Sample("text", 1)),
            ("403 bytes of text", Sample("text", 403)),
            ("5000 bytes of text", Sample("text", 5000)),
            ("3000 noise bytes", Sample("noise", 3000)),
            ("20000 pattern bytes", Sample("pattern", 20000)),
            ("70000 structured bytes", Sample("structured", 70000)),
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
