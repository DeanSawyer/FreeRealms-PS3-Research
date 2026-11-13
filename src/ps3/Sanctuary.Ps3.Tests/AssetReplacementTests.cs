using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

/// <summary>Putting another file in place of an asset of an install, and taking it out again.</summary>
[TestClass]
public sealed class AssetReplacementTests
{
    private static readonly byte[] Original = Encoding.ASCII.GetBytes("CFX original movie");
    private static readonly byte[] Other = Encoding.ASCII.GetBytes("before");

    [TestMethod]
    public void TheNewBytesAreReadAndTheOldOnesComeBack()
    {
        using var install = Install();
        var replacement = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();

        var backup = AssetReplacement.Replace(install.Root, "CharacterCreatePopUps.GFX", replacement);

        var read = PackIndex.ReadAsset(install.Root, "characterCreatePopUps.gfx")!;
        CollectionAssert.AreEqual(replacement, read.Data);
        Assert.AreEqual(Crc32.Compute(Original), read.StoredCrc, "the entry keeps its CRC-32, so the client still reads it from the pack");
        Assert.AreEqual(Crc32.Compute(Original), AssetManifest.Load(Path.Combine(install.Root, "assets_ps3_manifest.bin")).Find("characterCreatePopUps.gfx")!.Value.Crc32);
        Assert.AreEqual(1000u, AssetManifest.Load(Path.Combine(install.Root, "assets_ps3_manifest.bin")).Find("characterCreatePopUps.gfx")!.Value.Size);
        CollectionAssert.AreEqual(Other, PackIndex.ReadAsset(install.Root, "other.txt")!.Data);

        AssetReplacement.Restore(backup);

        CollectionAssert.AreEqual(Original, PackIndex.ReadAsset(install.Root, "characterCreatePopUps.gfx")!.Data);
        Assert.AreEqual((uint)Original.Length, AssetManifest.Load(Path.Combine(install.Root, "assets_ps3_manifest.bin")).Find("characterCreatePopUps.gfx")!.Value.Size);
        Assert.AreEqual(backup.PackLength, new FileInfo(backup.Pack).Length);
    }

    [TestMethod]
    public void AnAssetNoPackListsIsAnError()
    {
        using var install = Install();

        Assert.ThrowsExactly<FileNotFoundException>(() => AssetReplacement.Replace(install.Root, "missing.gfx", [1, 2, 3]));
    }

    /// <summary>One pack (index chunk, then "other.txt" and the movie) and a big-endian manifest listing both.</summary>
    private static TempInstall Install()
    {
        var pack = new byte[PackIndex.ChunkSize + Other.Length + Original.Length];
        var position = 8;

        BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(4), 2);
        Entry("other.txt", PackIndex.ChunkSize, Other);
        Entry("characterCreatePopUps.gfx", PackIndex.ChunkSize + Other.Length, Original);
        Other.CopyTo(pack, PackIndex.ChunkSize);
        Original.CopyTo(pack, PackIndex.ChunkSize + Other.Length);

        var records = new[] { ("other.txt", Other), ("characterCreatePopUps.gfx", Original) }
            .Select(item => (Hash: NameHash.Compute(item.Item1), Data: item.Item2))
            .OrderBy(item => item.Hash)
            .ToArray();

        var manifest = new byte[16 * records.Length];

        for (var index = 0; index < records.Length; index++)
        {
            var record = manifest.AsSpan(16 * index);
            BinaryPrimitives.WriteUInt64BigEndian(record, records[index].Hash);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], Crc32.Compute(records[index].Data));
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)records[index].Data.Length);
        }

        return new TempInstall([("assets_ps3_000.pack", pack), ("assets_ps3_manifest.bin", manifest)]);

        void Entry(string name, int offset, byte[] data)
        {
            var bytes = Encoding.ASCII.GetBytes(name);

            BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(position), (uint)bytes.Length);
            bytes.CopyTo(pack, position + 4);
            position += 4 + bytes.Length;
            BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(position), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(position + 4), (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(position + 8), Crc32.Compute(data));
            position += 12;
        }
    }
}
