using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Sanctuary.Ps3.Assets;

/// <summary>What the manifest knows about one asset: its CRC-32, its size and whether it is stored compressed.</summary>
public readonly record struct ManifestEntry(uint Crc32, uint Size, bool Compressed);

/// <summary>
/// <c>assets_ps3_manifest.bin</c>: 16 byte records <c>u64 name hash | u32 crc32 | u32 size</c> (top bit of the size = stored
/// compressed), sorted by the hash. The client byte-swaps the records on the PS3 (0x8035c0), so the byte order is detected
/// from the sort order. A name the manifest does not list is refused by the client at once (0x802828).
/// </summary>
public sealed class AssetManifest
{
    private readonly Dictionary<ulong, ManifestEntry> _byHash = [];

    public AssetManifest(ReadOnlySpan<byte> raw)
    {
        if (raw.Length % 16 != 0)
            throw new InvalidDataException($"Size {raw.Length} is not a multiple of 16; not a binary manifest.");

        Count = raw.Length / 16;

        var bestScore = -1.0;
        var bestRecords = Array.Empty<Record>();
        var bestBigEndian = true;
        var haveBest = false;

        foreach (var bigEndian in new[] { true, false })
        {
            var records = ReadRecords(raw, bigEndian);

            var ordered = 0;

            for (var index = 0; index + 1 < records.Length; index++)
            {
                if (records[index].Hash <= records[index + 1].Hash)
                    ordered++;
            }

            var score = (double)ordered / Math.Max(1, records.Length - 1);

            if (!haveBest || score > bestScore)
            {
                haveBest = true;
                bestScore = score;
                bestRecords = records;
                bestBigEndian = bigEndian;
            }
        }

        SortedFraction = bestScore;
        BigEndian = bestBigEndian;

        foreach (var record in bestRecords)
        {
            // The first record of a hash wins, as in the reference reader.
            _byHash.TryAdd(record.Hash, new ManifestEntry(record.Crc, record.Size & 0x7FFFFFFF, (record.Size & 0x80000000) != 0));
        }
    }

    public int Count { get; }

    /// <summary>Fraction of neighbouring records that are in ascending order (1.0 for a correctly read manifest).</summary>
    public double SortedFraction { get; }

    public bool BigEndian { get; }

    public static AssetManifest Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>The entry for an asset name, or null when the client would say "not in manifest".</summary>
    public ManifestEntry? Find(string name)
    {
        return _byHash.TryGetValue(NameHash.Compute(name), out var entry) ? entry : null;
    }

    private readonly record struct Record(ulong Hash, uint Crc, uint Size);

    private static Record[] ReadRecords(ReadOnlySpan<byte> raw, bool bigEndian)
    {
        var records = new Record[raw.Length / 16];

        for (var index = 0; index < records.Length; index++)
        {
            var slice = raw.Slice(index * 16, 16);

            records[index] = bigEndian
                ? new Record(
                    BinaryPrimitives.ReadUInt64BigEndian(slice),
                    BinaryPrimitives.ReadUInt32BigEndian(slice[8..]),
                    BinaryPrimitives.ReadUInt32BigEndian(slice[12..]))
                : new Record(
                    BinaryPrimitives.ReadUInt64LittleEndian(slice),
                    BinaryPrimitives.ReadUInt32LittleEndian(slice[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(slice[12..]));
        }

        return records;
    }
}
