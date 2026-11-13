using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sanctuary.Ps3.Assets;

/// <summary>
/// What <see cref="AssetReplacement.Replace"/> changed, so <see cref="AssetReplacement.Restore"/> can put it back: the pack, where the
/// asset's index entry keeps its offset and length, their old values, the pack's old length, and the manifest record's old size.
/// </summary>
public sealed record AssetBackup(
    string UsrDir,
    string Name,
    string Pack,
    long EntryPosition,
    uint Offset,
    uint Length,
    long PackLength,
    string? Manifest,
    long ManifestPosition,
    uint ManifestSize);

/// <summary>
/// Replaces the bytes of one asset in a PS3 install: the new bytes are added at the end of the pack that holds the asset and its
/// index entry is pointed at them. The entry keeps its CRC-32, and so does the manifest: the client reads a pack entry when the two
/// CRC-32s are equal (0x801020) and does not check the bytes against them. The manifest's size for the asset is set to the new size.
/// <see cref="Restore"/> puts the old offset, length and size back and cuts the pack to its old length.
/// </summary>
public static class AssetReplacement
{
    private const string ManifestName = "assets_ps3_manifest.bin";

    /// <summary>Puts <paramref name="data"/> in place of the asset <paramref name="name"/> (case-insensitive) of the install in <paramref name="usrDir"/>.</summary>
    /// <exception cref="FileNotFoundException">No pack of the install lists the asset.</exception>
    public static AssetBackup Replace(string usrDir, string name, ReadOnlySpan<byte> data)
    {
        var (pack, position, offset, length) = FindEntry(usrDir, name) ?? throw new FileNotFoundException($"No pack in {usrDir} lists {name}.");

        long packLength;
        long newOffset;

        using (var stream = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            packLength = stream.Length;
            newOffset = (packLength + 15) & ~15L;

            if (newOffset + data.Length > uint.MaxValue)
                throw new IOException($"{Path.GetFileName(pack)} would grow past 4 GB.");

            stream.SetLength(newOffset);
            stream.Seek(newOffset, SeekOrigin.Begin);
            stream.Write(data);

            WriteEntry(stream, position, (uint)newOffset, (uint)data.Length);
        }

        var manifest = FindManifest(usrDir);
        long manifestPosition = -1;
        uint manifestSize = 0;

        if (manifest is not null && FindManifestRecord(manifest, name) is { } record)
        {
            manifestPosition = record.Position;
            manifestSize = record.Size;
            WriteManifestSize(manifest, record.Position, record.BigEndian, (record.Size & 0x80000000) | ((uint)data.Length & 0x7FFFFFFF));
        }
        else
        {
            manifest = null;
        }

        return new AssetBackup(usrDir, name, pack, position, offset, length, packLength, manifest, manifestPosition, manifestSize);
    }

    /// <summary>Undoes <see cref="Replace"/>.</summary>
    public static void Restore(AssetBackup backup)
    {
        using (var stream = new FileStream(backup.Pack, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            WriteEntry(stream, backup.EntryPosition, backup.Offset, backup.Length);

            if (stream.Length > backup.PackLength)
                stream.SetLength(backup.PackLength);
        }

        if (backup.Manifest is { } manifest && backup.ManifestPosition >= 0 && File.Exists(manifest)
            && FindManifestRecord(manifest, backup.Name) is { } record && record.Position == backup.ManifestPosition)
        {
            WriteManifestSize(manifest, record.Position, record.BigEndian, backup.ManifestSize);
        }
    }

    /// <summary>The install's manifest: in USRDIR, or else the first one below it.</summary>
    private static string? FindManifest(string usrDir)
    {
        var top = Path.Combine(usrDir, ManifestName);

        return File.Exists(top) ? top : Directory.EnumerateFiles(usrDir, ManifestName, SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>The pack listing the asset, where its entry's offset field is, and the entry's offset and length.</summary>
    internal static (string Pack, long Position, uint Offset, uint Length)? FindEntry(string usrDir, string name)
    {
        if (!Directory.Exists(usrDir))
            return null;

        var packs = Directory.GetFiles(usrDir, "assets_ps3_*.pack");
        Array.Sort(packs, StringComparer.Ordinal);

        foreach (var pack in packs)
        {
            using var stream = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var size = stream.Length;
            var seen = new HashSet<long>();
            long chunk = 0;

            while (chunk + 8 <= size && seen.Add(chunk))
            {
                var block = new byte[(int)Math.Min(PackIndex.ChunkSize, size - chunk)];
                stream.Seek(chunk, SeekOrigin.Begin);
                stream.ReadExactly(block);

                if (!PackIndex.TryParseChunk(block, size, out var next, []))
                    break;

                var count = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));
                var position = 8;

                for (uint index = 0; index < count; index++)
                {
                    var nameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(position));
                    var entryName = Encoding.ASCII.GetString(block, position + 4, nameLength).TrimEnd('\0');
                    var fields = position + 4 + nameLength;

                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return (pack, chunk + fields,
                            BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(fields)),
                            BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(fields + 4)));
                    }

                    position = fields + 12;
                }

                if (next == 0)
                    break;

                chunk = next;
            }
        }

        return null;
    }

    private static void WriteEntry(FileStream stream, long position, uint offset, uint length)
    {
        Span<byte> fields = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(fields, offset);
        BinaryPrimitives.WriteUInt32BigEndian(fields[4..], length);

        stream.Seek(position, SeekOrigin.Begin);
        stream.Write(fields);
    }

    private static (long Position, uint Size, bool BigEndian)? FindManifestRecord(string manifest, string name)
    {
        var raw = File.ReadAllBytes(manifest);

        if (raw.Length % 16 != 0)
            return null;

        var bigEndian = new AssetManifest(raw).BigEndian;
        var hash = NameHash.Compute(name);

        for (var position = 0; position < raw.Length; position += 16)
        {
            var record = raw.AsSpan(position, 16);
            var recordHash = bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(record) : BinaryPrimitives.ReadUInt64LittleEndian(record);

            if (recordHash == hash)
            {
                var size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(record[12..]) : BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);

                return (position, size, bigEndian);
            }
        }

        return null;
    }

    private static void WriteManifestSize(string manifest, long position, bool bigEndian, uint size)
    {
        Span<byte> field = stackalloc byte[4];

        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(field, size);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(field, size);

        using var stream = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(position + 12, SeekOrigin.Begin);
        stream.Write(field);
    }
}
