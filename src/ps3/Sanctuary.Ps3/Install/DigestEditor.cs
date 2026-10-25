using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Install;

/// <summary>One digest record that was rewritten to describe the local file.</summary>
public sealed record DigestChange(
    string Path,
    ulong OldSize,
    long NewSize,
    ulong OldCrc32,
    uint NewCrc32,
    ulong OldLength,
    ulong NewLength);

/// <summary>Adapts a digest to the files actually installed (<c>--match-local</c>). Ported from <c>digest_edit.adapt_to_local</c>.</summary>
public static class DigestEditor
{
    /// <summary>Largest file for which an LF to CRLF rewrite is attempted when the CRC does not match.</summary>
    public const long TextFixLimit = 64L * 1024 * 1024;

    /// <summary>
    /// Describes the local build: rewrites size, CRC and length of records whose local file differs. A record is left alone when
    /// the local file is missing (the client will download it), already matches, or matches after LF to CRLF conversion (the
    /// server converts those on request).
    /// </summary>
    public static (byte[] Body, List<DigestChange> Changes) AdaptToLocal(byte[] body, string usrDir)
    {
        var tree = DigestTree.Parse(body);
        var changes = new List<DigestChange>();
        var root = Path.GetFullPath(usrDir);

        foreach (var (path, node) in DigestTree.Downloadable(tree).ToList())
        {
            var local = Path.GetFullPath(Path.Combine(root, path));

            if (!local.StartsWith(root, StringComparison.Ordinal) || !File.Exists(local))
                continue;

            var wantSize = DigestTree.GetField(node, DigestTree.TagSize)!.Value;
            var wantCrc = DigestTree.GetField(node, DigestTree.TagCrc32)!.Value;
            var size = new FileInfo(local).Length;

            uint? crc = (ulong)size == wantSize ? Crc32.OfFile(local) : null;

            if (crc is { } known && known == wantCrc)
                continue;

            if ((ulong)size != wantSize && size <= TextFixLimit && wantSize <= (ulong)TextFixLimit)
            {
                var fixedText = ToCrlf(File.ReadAllBytes(local));

                if ((ulong)fixedText.Length == wantSize && Crc32.Compute(fixedText) == wantCrc)
                    continue;
            }

            var data = File.ReadAllBytes(local);
            var newCrc = Crc32.Compute(data);
            var newLength = (ulong)(ZsContainer.PayloadLength(data) + ZsContainer.TrailerLength);

            changes.Add(new DigestChange(
                path,
                wantSize,
                size,
                wantCrc,
                newCrc,
                DigestTree.GetField(node, DigestTree.TagCompressedLength)!.Value,
                newLength));

            DigestTree.SetField(node, DigestTree.TagSize, (ulong)size);
            DigestTree.SetField(node, DigestTree.TagCrc32, newCrc);
            DigestTree.SetField(node, DigestTree.TagCompressedLength, newLength);
        }

        if (changes.Count > 0)
        {
            ulong total = 0;

            foreach (var (_, node) in DigestTree.Downloadable(tree))
                total += DigestTree.GetField(node, DigestTree.TagSize)!.Value;

            DigestTree.SetHeaderNumber(tree, DigestTree.HeaderTotalKiB, total / 1024);
        }

        return (DigestTree.Serialize(tree), changes);
    }

    /// <summary>
    /// Line endings as <c>data.replace("\r\n", "\n").replace("\n", "\r\n")</c> would leave them: every LF becomes CRLF, and a CR
    /// that is not part of a CRLF stays as it is.
    /// </summary>
    public static byte[] ToCrlf(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + (data.Length / 16));

        for (var index = 0; index < data.Length; index++)
        {
            var value = data[index];

            if (value == (byte)'\r' && index + 1 < data.Length && data[index + 1] == (byte)'\n')
                continue; // the LF that follows becomes the whole CRLF

            if (value == (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }
            else
            {
                output.Add(value);
            }
        }

        return [.. output];
    }
}
