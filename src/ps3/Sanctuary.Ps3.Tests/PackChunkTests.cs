using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

/// <summary>The rules that make an index chunk "valid" in the client's parser, one at a time.</summary>
[TestClass]
public sealed class PackChunkTests
{
    [TestMethod]
    public void ValidChunkIsParsed()
    {
        var entries = new List<PackEntry>();

        Assert.IsTrue(PackIndex.TryParseChunk(Chunk(0, "a.txt", 10, 5, 0xAABBCCDD), 100, out var next, entries));
        Assert.AreEqual(0u, next);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(new PackEntry("a.txt", 10, 5, 0xAABBCCDD), entries[0]);
    }

    [TestMethod]
    public void TrailingNulIsDropped()
    {
        var entries = new List<PackEntry>();

        Assert.IsTrue(PackIndex.TryParseChunk(Chunk(0, "a.txt\0", 10, 5, 1), 100, out _, entries));
        Assert.AreEqual("a.txt", entries.Single().Name);
    }

    [TestMethod]
    public void NextPointerIsReturned()
    {
        var entries = new List<PackEntry>();

        Assert.IsTrue(PackIndex.TryParseChunk(Chunk(0x2000, "a", 0, 0, 0), 100, out var next, entries));
        Assert.AreEqual(0x2000u, next);
    }

    [TestMethod]
    public void ControlCharacterInNameIsRejected()
    {
        Assert.IsFalse(PackIndex.TryParseChunk(Chunk(0, "a\u0001b", 10, 5, 1), 100, out _, new List<PackEntry>()));
    }

    [TestMethod]
    public void NameThatIsOnlyANulIsRejected()
    {
        Assert.IsFalse(PackIndex.TryParseChunk(Chunk(0, "\0", 10, 5, 1), 100, out _, new List<PackEntry>()));
    }

    [TestMethod]
    public void OffsetBeyondTheFileIsRejected()
    {
        Assert.IsFalse(PackIndex.TryParseChunk(Chunk(0, "a", 101, 0, 1), 100, out _, new List<PackEntry>()));
    }

    [TestMethod]
    public void LengthBeyondTheFileIsRejectedButSixteenBytesOfSlackAreAllowed()
    {
        Assert.IsFalse(PackIndex.TryParseChunk(Chunk(0, "a", 50, 67, 1), 100, out _, new List<PackEntry>()));
        Assert.IsTrue(PackIndex.TryParseChunk(Chunk(0, "a", 50, 66, 1), 100, out _, new List<PackEntry>()));
    }

    [TestMethod]
    public void TruncatedChunkIsRejected()
    {
        var block = Chunk(0, "a.txt", 10, 5, 1);

        Assert.IsFalse(PackIndex.TryParseChunk(block.AsSpan(0, block.Length - 1), 100, out _, new List<PackEntry>()));
        Assert.IsFalse(PackIndex.TryParseChunk(new byte[7], 100, out _, new List<PackEntry>()));
    }

    [TestMethod]
    public void IndexThatLoopsBackIsUnreadable()
    {
        // Chunk 0 -> chunk at 0x2000 -> itself.
        var data = new byte[PackIndex.ChunkSize * 2];

        BinaryPrimitives.WriteUInt32BigEndian(data, 0x2000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(PackIndex.ChunkSize), 0x2000);

        using var install = new TempInstall([("assets_ps3_000.pack", data)]);

        var packs = PackIndex.Load(install.Root);

        CollectionAssert.AreEqual(new[] { "assets_ps3_000.pack" }, packs.Unreadable.ToArray());
        Assert.AreEqual(0, packs.Entries.Count);
    }

    /// <summary>One index chunk holding a single entry.</summary>
    private static byte[] Chunk(uint next, string name, uint offset, uint length, uint crc)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        var block = new byte[8 + 4 + nameBytes.Length + 12];

        BinaryPrimitives.WriteUInt32BigEndian(block, next);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), (uint)nameBytes.Length);

        nameBytes.CopyTo(block, 12);

        var fields = 12 + nameBytes.Length;

        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(fields), offset);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(fields + 4), length);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(fields + 8), crc);

        return block;
    }
}
