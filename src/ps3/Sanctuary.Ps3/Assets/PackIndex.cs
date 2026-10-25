using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sanctuary.Ps3.Assets;

/// <summary>
/// Reader for <c>assets_ps3_NNN.pack</c> index chunks. The index is a linked list of 0x2000 byte chunks; each chunk is
/// <c>u32 next | u32 count | count x { u32 name length | name | u32 offset | u32 length | u32 crc }</c>, all big endian.
/// Read from the client (0xa28f8c, 0xda46c4, 0xda4368); see docs/re/assets.md.
/// </summary>
public static class PackIndex
{
    public const int ChunkSize = 0x2000;

    private const int MaxNameLength = 512;

    /// <summary>Parses one index chunk. Returns false when the block is not a valid chunk.</summary>
    public static bool TryParseChunk(ReadOnlySpan<byte> block, long fileSize, out uint next, List<PackEntry> entries)
    {
        next = 0;

        if (block.Length < 8)
            return false;

        next = BinaryPrimitives.ReadUInt32BigEndian(block);
        var count = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);

        var position = 8;

        for (uint index = 0; index < count; index++)
        {
            if (position + 4 > block.Length)
                return false;

            var nameLength = BinaryPrimitives.ReadUInt32BigEndian(block[position..]);

            if (nameLength == 0 || nameLength > MaxNameLength || position + 4L + nameLength + 12 > block.Length)
                return false;

            var name = block.Slice(position + 4, (int)nameLength);

            if (name[^1] == 0)
                name = name[..^1];

            if (name.IsEmpty)
                return false;

            foreach (var value in name)
            {
                if (value < 32 || value > 126)
                    return false;
            }

            var fields = position + 4 + (int)nameLength;

            var offset = BinaryPrimitives.ReadUInt32BigEndian(block[fields..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(block[(fields + 4)..]);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(block[(fields + 8)..]);

            if (offset > fileSize || (long)offset + length > fileSize + 16)
                return false;

            entries.Add(new PackEntry(Encoding.ASCII.GetString(name), offset, length, crc));

            position = fields + 12;
        }

        return true;
    }

    /// <summary>All entries of one pack, or null when its index does not parse.</summary>
    public static List<PackEntry>? Read(string path)
    {
        var size = new FileInfo(path).Length;

        var result = new List<PackEntry>();
        var seen = new HashSet<long>();

        long position = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (true)
        {
            if (seen.Contains(position) || position + 8 > size)
                return null;

            seen.Add(position);

            stream.Seek(position, SeekOrigin.Begin);

            var block = new byte[(int)Math.Min(ChunkSize, size - position)];
            stream.ReadExactly(block);

            var chunk = new List<PackEntry>();

            if (!TryParseChunk(block, size, out var next, chunk))
                return null;

            result.AddRange(chunk);

            if (next == 0)
                return result;

            position = next;
        }
    }

    /// <summary>Indexes every <c>assets_ps3_*.pack</c> in a folder.</summary>
    public static PackSet Load(string usrDir)
    {
        var packs = ListPacks(usrDir);

        var entries = new List<PackedEntry>();
        var unreadable = new List<string>();

        foreach (var pack in packs)
        {
            var fileName = Path.GetFileName(pack);
            var index = Read(pack);

            if (index is null)
            {
                unreadable.Add(fileName);
                continue;
            }

            foreach (var entry in index)
                entries.Add(new PackedEntry(fileName, entry.Name, entry.Offset, entry.Length, entry.Crc));
        }

        return new PackSet(packs, entries, unreadable);
    }

    /// <summary>Bytes of one asset by name (case-insensitive), or null when no pack lists it.</summary>
    public static PackedAsset? ReadAsset(string usrDir, string name)
    {
        foreach (var pack in ListPacks(usrDir))
        {
            var index = Read(pack);

            if (index is null)
                continue;

            foreach (var entry in index)
            {
                if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var stream = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Seek(entry.Offset, SeekOrigin.Begin);

                var buffer = new byte[entry.Length];
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

                var data = read == buffer.Length ? buffer : buffer[..read];

                return new PackedAsset(data, Path.GetFileName(pack), entry.Crc, Crc32.Compute(data) == entry.Crc);
            }
        }

        return null;
    }

    private static List<string> ListPacks(string usrDir)
    {
        var packs = new List<string>();

        if (Directory.Exists(usrDir))
            packs.AddRange(Directory.GetFiles(usrDir, "assets_ps3_*.pack"));

        packs.Sort(StringComparer.Ordinal);

        return packs;
    }
}
