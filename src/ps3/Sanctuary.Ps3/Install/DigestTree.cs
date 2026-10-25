using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sanctuary.Ps3.Install;

/// <summary>What a digest node holds: a plain value, a directory of nodes, or one file record.</summary>
public enum DigestKind
{
    Leaf,
    Directory,
    File,
}

/// <summary>
/// One tag/length/value node of the digest body (docs/install/digest-format.md). The length prefix is kept as found in the
/// source (<see cref="Width"/> 1, 2 or 5 bytes), so an unedited digest serialises back to the same bytes.
/// </summary>
public sealed class DigestNode
{
    public DigestNode(int tag, DigestKind kind, byte[]? value = null, List<DigestNode>? children = null, int width = 1)
    {
        Tag = tag;
        Kind = kind;
        Value = value ?? [];
        Children = children ?? [];
        Width = width;
    }

    public int Tag { get; }

    public DigestKind Kind { get; }

    /// <summary>The bytes of a leaf (empty for a container).</summary>
    public byte[] Value { get; set; }

    public List<DigestNode> Children { get; }

    public int Width { get; }
}

/// <summary>
/// Reader, editor and writer for the digest body. Length prefix: below 0x80 one byte; 0x80 to 0xFE two bytes (15 bits);
/// 0xFF then four bytes. Top level: header fields, then tag 2 = root directory. Inside a directory: tag 1 name, tag 2 child
/// directory, tag 3 file record, tag 0xFE child counts. Inside a file record: tag 1 name, 2 compressed length, 3 size,
/// 4 CRC-32, 8 timestamp, 6 flag. Ported from <c>bootstrap/digest_edit.py</c>.
/// </summary>
public static class DigestTree
{
    /// <summary>Header tag 21: the sum of all downloadable file sizes in KiB.</summary>
    public const int HeaderTotalKiB = 21;

    public const int TagName = 1;
    public const int TagCompressedLength = 2;
    public const int TagSize = 3;
    public const int TagCrc32 = 4;

    private enum Context
    {
        Top,
        Directory,
        File,
    }

    public static List<DigestNode> Parse(ReadOnlySpan<byte> body) => ParseSequence(body, 0, body.Length, Context.Top);

    public static byte[] Serialize(IEnumerable<DigestNode> nodes)
    {
        using var output = new MemoryStream();

        foreach (var node in nodes)
            WriteNode(output, node);

        return output.ToArray();
    }

    /// <summary>Every file record with its path, in digest order.</summary>
    public static IEnumerable<(string Path, DigestNode File)> Files(IEnumerable<DigestNode> nodes)
    {
        foreach (var top in nodes)
        {
            if (top.Kind != DigestKind.Directory)
                continue;

            foreach (var file in Walk(top, string.Empty))
                yield return file;
        }
    }

    /// <summary>Records the client downloads: they carry compressed length, size and CRC-32 (tags 2, 3 and 4).</summary>
    public static IEnumerable<(string Path, DigestNode File)> Downloadable(IEnumerable<DigestNode> nodes)
    {
        return Files(nodes).Where(item =>
            GetField(item.File, TagCompressedLength) is not null
            && GetField(item.File, TagSize) is not null
            && GetField(item.File, TagCrc32) is not null);
    }

    /// <summary>A numeric field of a file record (big endian, up to 8 bytes), or null when the record has no such tag.</summary>
    public static ulong? GetField(DigestNode file, int tag)
    {
        foreach (var child in file.Children)
        {
            if (child.Tag == tag && child.Kind == DigestKind.Leaf)
                return FromBigEndian(child.Value);
        }

        return null;
    }

    /// <summary>Sets a numeric field, keeping its byte width when the new value still fits.</summary>
    public static void SetField(DigestNode file, int tag, ulong value)
    {
        foreach (var child in file.Children)
        {
            if (child.Tag != tag || child.Kind != DigestKind.Leaf)
                continue;

            child.Value = ToBigEndian(value, child.Value.Length);

            return;
        }

        throw new KeyNotFoundException($"file record has no tag {tag}");
    }

    public static bool SetHeaderNumber(IEnumerable<DigestNode> nodes, int tag, ulong value)
    {
        foreach (var node in nodes)
        {
            if (node.Tag != tag || node.Kind != DigestKind.Leaf)
                continue;

            node.Value = ToBigEndian(value, node.Value.Length);

            return true;
        }

        return false;
    }

    /// <summary>The name leaf (tag 1) of a directory or file record, without trailing NULs.</summary>
    public static string NameOf(DigestNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Tag == TagName && child.Kind == DigestKind.Leaf)
            {
                var length = child.Value.Length;

                while (length > 0 && child.Value[length - 1] == 0)
                    length--;

                return Encoding.Latin1.GetString(child.Value, 0, length);
            }
        }

        return string.Empty;
    }

    private static IEnumerable<(string Path, DigestNode File)> Walk(DigestNode directory, string prefix)
    {
        var name = NameOf(directory);
        var baseName = prefix + (name.Length > 0 ? name + "/" : string.Empty);

        foreach (var child in directory.Children)
        {
            if (child.Kind == DigestKind.Directory)
            {
                foreach (var file in Walk(child, baseName))
                    yield return file;
            }
            else if (child.Kind == DigestKind.File)
            {
                yield return (baseName + NameOf(child), child);
            }
        }
    }

    private static ulong FromBigEndian(byte[] value)
    {
        if (value.Length > 8)
            throw new InvalidDataException($"A numeric field of {value.Length} bytes does not fit 64 bits.");

        ulong result = 0;

        foreach (var b in value)
            result = (result << 8) | b;

        return result;
    }

    private static byte[] ToBigEndian(ulong value, int minimumWidth)
    {
        var needed = 0;

        for (var rest = value; rest != 0; rest >>= 8)
            needed++;

        var width = Math.Max(Math.Max(minimumWidth, needed), 1);
        var bytes = new byte[width];

        for (var index = width - 1; index >= 0 && value != 0; index--)
        {
            bytes[index] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }

    private static List<DigestNode> ParseSequence(ReadOnlySpan<byte> data, int start, int end, Context context)
    {
        var nodes = new List<DigestNode>();
        var position = start;

        while (position < end)
        {
            var tag = data[position];
            var (length, width) = ReadLength(data, position + 1);
            var valueStart = position + 1 + width;
            var valueEnd = (long)valueStart + length;

            if (valueEnd > end)
                throw new InvalidDataException($"TLV overruns its parent at offset {position}");

            var valueStartIndex = valueStart;
            var valueEndIndex = (int)valueEnd;

            if (context == Context.Top && tag == 2)
            {
                nodes.Add(new DigestNode(tag, DigestKind.Directory, children: ParseSequence(data, valueStartIndex, valueEndIndex, Context.Directory), width: width));
            }
            else if (context == Context.Directory && tag == 2)
            {
                nodes.Add(new DigestNode(tag, DigestKind.Directory, children: ParseSequence(data, valueStartIndex, valueEndIndex, Context.Directory), width: width));
            }
            else if (context == Context.Directory && tag == 3)
            {
                nodes.Add(new DigestNode(tag, DigestKind.File, children: ParseSequence(data, valueStartIndex, valueEndIndex, Context.File), width: width));
            }
            else
            {
                nodes.Add(new DigestNode(tag, DigestKind.Leaf, value: data[valueStartIndex..valueEndIndex].ToArray(), width: width));
            }

            position = valueEndIndex;
        }

        return nodes;
    }

    private static (int Length, int Width) ReadLength(ReadOnlySpan<byte> data, int position)
    {
        if (position >= data.Length)
            throw new InvalidDataException("Digest ends inside a length prefix.");

        var first = data[position];

        if (first < 0x80)
            return (first, 1);

        if (first == 0xFF)
        {
            if (position + 5 > data.Length)
                throw new InvalidDataException("Digest ends inside a length prefix.");

            var value = ((uint)data[position + 1] << 24) | ((uint)data[position + 2] << 16) | ((uint)data[position + 3] << 8) | data[position + 4];

            if (value > int.MaxValue)
                throw new InvalidDataException("A length prefix is larger than 2 GiB.");

            return ((int)value, 5);
        }

        if (position + 2 > data.Length)
            throw new InvalidDataException("Digest ends inside a length prefix.");

        return (((first & 0x7F) << 8) | data[position + 1], 2);
    }

    private static void WriteLength(Stream output, int length, int minimumWidth)
    {
        if (length < 0x80 && minimumWidth <= 1)
        {
            output.WriteByte((byte)length);
        }
        else if (length <= 0x7EFF && minimumWidth <= 2)
        {
            output.WriteByte((byte)(0x80 | (length >> 8)));
            output.WriteByte((byte)(length & 0xFF));
        }
        else
        {
            output.WriteByte(0xFF);
            output.WriteByte((byte)(length >> 24));
            output.WriteByte((byte)(length >> 16));
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)length);
        }
    }

    private static void WriteNode(Stream output, DigestNode node)
    {
        byte[] payload;

        if (node.Kind == DigestKind.Leaf)
        {
            payload = node.Value;
        }
        else
        {
            using var inner = new MemoryStream();

            foreach (var child in node.Children)
                WriteNode(inner, child);

            payload = inner.ToArray();
        }

        output.WriteByte((byte)node.Tag);
        WriteLength(output, payload.Length, node.Width);
        output.Write(payload, 0, payload.Length);
    }
}
