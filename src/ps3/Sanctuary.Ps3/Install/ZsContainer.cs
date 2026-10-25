using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Sanctuary.Ps3.Install;

/// <summary>Which trailer a <c>.zs</c> container gets after its payload.</summary>
public enum ZsCodec
{
    /// <summary><c>0x01</c> followed by 128 zero bytes: the container the client accepts (docs/install/digest-format.md).</summary>
    ZeroTrailer,

    /// <summary>The 129-byte tail of the digest itself (an experiment that works the same, because the trailer is not checked).</summary>
    DigestTrailer,
}

/// <summary>
/// The <c>.zs</c> asset container: a zlib stream (or the raw bytes when zlib does not make the file smaller) followed by a
/// 129-byte trailer. The digest records the container's length, so the payload has to be exactly <c>length - 129</c> bytes.
/// Ported from <c>bootstrap/codec_candidates.py</c>. Only the accepted container is ported; the rejected early experiments
/// (lzma and friends) stay in the Python reference.
/// </summary>
public sealed class ZsContainer
{
    public const int TrailerLength = 129;

    private static readonly int[] DefaultLevels = [9, 6, 1, 2, 3, 4, 5, 7, 8];

    private static readonly byte[] Signature = new byte[128];

    /// <summary>
    /// How a file stored without compression is wrapped. The first trailer byte probably says how the payload is encoded;
    /// 0x01 works for zlib payloads, but the client rejected raw bytes + 0x01, so these are tried in order, one per retry.
    /// </summary>
    private static readonly (string Label, byte Marker, bool ZlibStored)[] StoredVariants =
    [
        ("raw+trailer00", 0x00, false),
        ("raw+trailer02", 0x02, false),
        ("raw+trailer03", 0x03, false),
        ("raw+trailer04", 0x04, false),
        ("raw+trailer80", 0x80, false),
        ("raw+trailerff", 0xFF, false),
        ("zlibstored+trailer01", 0x01, true),
    ];

    private readonly object _gate = new();
    private readonly List<int> _levels = [.. DefaultLevels];
    private readonly byte[]? _digestTrailer;

    public ZsContainer(ZsCodec codec = ZsCodec.ZeroTrailer, byte[]? digestTrailer = null)
    {
        Codec = codec;

        if (codec == ZsCodec.DigestTrailer)
        {
            _digestTrailer = digestTrailer is { Length: TrailerLength }
                ? digestTrailer
                : throw new InvalidOperationException("The digest trailer codec needs the 129-byte tail of a valid digest.");
        }
    }

    public ZsCodec Codec { get; }

    public static IReadOnlyList<string> StoredVariantLabels { get; } = Array.ConvertAll(StoredVariants, variant => variant.Label);

    /// <summary>Payload size the container uses for <paramref name="data"/>: zlib level 9, or raw if that is not smaller.</summary>
    public static int PayloadLength(byte[] data)
    {
        var packed = Deflate(data, 9).Length;

        return packed < data.Length ? packed : data.Length;
    }

    /// <summary>A whole container for <paramref name="raw"/>; <paramref name="payloadTarget"/> is the payload length the manifest implies (length - 129).</summary>
    public byte[] Encode(byte[] raw, int? payloadTarget)
    {
        var payload = Payload(raw, payloadTarget);
        var trailer = Codec == ZsCodec.DigestTrailer ? _digestTrailer! : ZeroTrailer();
        var output = new byte[payload.Length + trailer.Length];

        payload.CopyTo(output, 0);
        trailer.CopyTo(output, payload.Length);

        return output;
    }

    /// <summary>A container for a file the digest lists as stored (payload == size); <paramref name="index"/> picks the variant.</summary>
    public static (byte[] Data, string Label) EncodeStored(int index, byte[] raw)
    {
        var (label, marker, zlibStored) = StoredVariants[index % StoredVariants.Length];

        var payload = zlibStored ? StoredZlib(raw) : raw;
        var output = new byte[payload.Length + TrailerLength];

        payload.CopyTo(output, 0);
        output[payload.Length] = marker;

        return (output, label);
    }

    /// <summary>
    /// The payload: the raw bytes when the target says the file is stored; otherwise a zlib stream, preferring a level that
    /// reproduces the target length and padding a valid stream to exactly that length when none does.
    /// </summary>
    public byte[] Payload(byte[] raw, int? target)
    {
        if (target is not null && target == raw.Length)
            return raw;

        if (target is { } wanted)
        {
            int[] order;

            lock (_gate)
                order = [.. _levels];

            foreach (var level in order)
            {
                var candidate = Deflate(raw, level);

                if (candidate.Length != wanted)
                    continue;

                lock (_gate)
                {
                    // Try the level that worked last time first.
                    _levels.Remove(level);
                    _levels.Insert(0, level);
                }

                return candidate;
            }

            var padded = PadZlib(raw, wanted);

            if (padded is not null)
                return padded;
        }

        var packed = Deflate(raw, 9);

        return packed.Length < raw.Length ? packed : raw;
    }

    /// <summary>
    /// A valid zlib stream for <paramref name="raw"/> that is exactly <paramref name="target"/> bytes long, or null.
    /// Built as: zlib header, a level 9 deflate of all but the last few bytes, a short independently compressed segment, empty
    /// stored blocks (5 bytes each) to land on the exact length, stored blocks for the rest, then the Adler-32.
    /// Any standard inflater reads it back to <paramref name="raw"/>.
    /// </summary>
    public static byte[]? PadZlib(byte[] raw, int target)
    {
        // zlib header, one empty final stored block and the Adler-32 are the least a stream can be.
        if (target < 2 + 5 + 4)
            return null;

        foreach (var level in new[] { 9, 6, 1 })
        {
            foreach (var tail in new[] { 96, 400, 2000 })
            {
                var result = PadZlib(raw, target, tail, level);

                if (result is not null)
                    return result;
            }
        }

        return null;
    }

    private static byte[]? PadZlib(byte[] raw, int target, int tail, int level)
    {
        var n0 = Math.Max(raw.Length - tail, 0);
        var main = n0 > 0 ? DeflateSegment(raw.AsSpan(0, n0), level) : [];

        var adler = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));

        var maxSplit = Math.Min(tail, raw.Length - n0);

        for (var split = 0; split <= maxSplit; split++)
        {
            var segment = split > 0 ? DeflateSegment(raw.AsSpan(n0, split), level) : [];
            var final = StoredBlocks(raw.AsSpan(n0 + split), final: true);

            var total = 2 + main.Length + segment.Length + final.Length + adler.Length;
            var gap = target - total;

            if (gap < 0 || gap % 5 != 0)
                continue;

            using var output = new MemoryStream(target);

            output.WriteByte(0x78);
            output.WriteByte(0xDA);
            output.Write(main, 0, main.Length);
            output.Write(segment, 0, segment.Length);

            for (var index = 0; index < gap / 5; index++)
                output.Write([0x00, 0x00, 0x00, 0xFF, 0xFF]);

            output.Write(final, 0, final.Length);
            output.Write(adler, 0, adler.Length);

            return output.ToArray();
        }

        return null;
    }

    /// <summary>A zlib stream made only of stored blocks.</summary>
    public static byte[] StoredZlib(byte[] raw)
    {
        var blocks = StoredBlocks(raw, final: true);
        var output = new byte[2 + blocks.Length + 4];

        output[0] = 0x78;
        output[1] = 0x01;

        blocks.CopyTo(output, 2);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(2 + blocks.Length), Adler32(raw));

        return output;
    }

    /// <summary>Deflate stored (uncompressed) blocks covering <paramref name="data"/>.</summary>
    public static byte[] StoredBlocks(ReadOnlySpan<byte> data, bool final)
    {
        using var output = new MemoryStream();

        var position = 0;

        while (true)
        {
            var length = Math.Min(data.Length - position, 65535);
            position += length;

            var last = final && position >= data.Length;

            output.WriteByte(last ? (byte)1 : (byte)0);
            output.WriteByte((byte)(length & 0xFF));
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)(~length & 0xFF));
            output.WriteByte((byte)((~length >> 8) & 0xFF));
            output.Write(data.Slice(position - length, length));

            if (position >= data.Length)
                return output.ToArray();
        }
    }

    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>A complete zlib stream at the given level (0 to 9).</summary>
    public static byte[] Deflate(byte[] data, int level)
    {
        using var output = new MemoryStream();

        using (var stream = new ZLibStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true))
            stream.Write(data, 0, data.Length);

        return output.ToArray();
    }

    /// <summary>Decodes a zlib stream (used by tests and by the digest reader).</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var stream = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        stream.CopyTo(output);

        return output.ToArray();
    }

    /// <summary>
    /// Raw deflate blocks for <paramref name="data"/> that end on a byte boundary without a final block (a sync flush), so
    /// that segments can be concatenated.
    /// </summary>
    private static byte[] DeflateSegment(ReadOnlySpan<byte> data, int level)
    {
        using var output = new MemoryStream();

        using var stream = new DeflateStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true);

        stream.Write(data);
        stream.Flush();

        // Whatever Dispose would add (the final block) is not wanted, so copy before it runs.
        return output.ToArray();
    }

    private static byte[] ZeroTrailer()
    {
        var trailer = new byte[TrailerLength];

        trailer[0] = 0x01;
        Signature.CopyTo(trailer, 1);

        return trailer;
    }
}
