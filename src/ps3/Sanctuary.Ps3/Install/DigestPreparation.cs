using System.Collections.Generic;

namespace Sanctuary.Ps3.Install;

/// <summary>The digest the server hands out, and what was done to it.</summary>
/// <param name="Digest">The bytes to serve: a zlib stream plus the 129-byte trailer.</param>
/// <param name="Trailer">The trailer of the digest that was read (kept unchanged by <c>--match-local</c>).</param>
/// <param name="Changes">The records rewritten to describe the local files; empty without <c>--match-local</c>.</param>
/// <param name="OriginalLength">False when the digest is not the 21466-byte original (the client replaces its copy with the last one it downloaded).</param>
public sealed record PreparedDigest(byte[] Digest, byte[] Trailer, IReadOnlyList<DigestChange> Changes, bool OriginalLength);

/// <summary>Validates the digest and applies <c>--match-local</c>. Ported from <c>main()</c> in <c>bootstrap/fr_ps3_bootstrap.py</c>.</summary>
public static class DigestPreparation
{
    /// <param name="blob">The digest file.</param>
    /// <param name="matchUsrDir">The <c>USRDIR</c> to describe; null serves the digest unchanged.</param>
    /// <exception cref="System.IO.InvalidDataException">The file is not a digest.</exception>
    public static PreparedDigest Prepare(byte[] blob, string? matchUsrDir)
    {
        var (body, trailer) = DigestFile.Split(blob);
        var digest = blob;
        IReadOnlyList<DigestChange> changes = [];

        if (matchUsrDir is not null)
        {
            var (adapted, rewritten) = DigestEditor.AdaptToLocal(body, matchUsrDir);

            changes = rewritten;

            if (rewritten.Count > 0)
                digest = DigestFile.Pack(adapted, trailer);
        }

        return new PreparedDigest(digest, trailer, changes, blob.Length == DigestFile.OriginalLength);
    }
}
