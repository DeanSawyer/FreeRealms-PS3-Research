using System;
using System.IO;
using System.IO.Compression;

namespace Sanctuary.Ps3.Install;

/// <summary>The digest as stored on disk and served: a zlib stream followed by a 129-byte trailer.</summary>
public static class DigestFile
{
    /// <summary>Size of the digest in the original build; the client replaces its local copy with what it last downloaded.</summary>
    public const int OriginalLength = 21466;

    /// <summary>Splits a digest into its decompressed body and trailer. Throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static (byte[] Body, byte[] Trailer) Split(byte[] blob)
    {
        if (blob.Length <= ZsContainer.TrailerLength)
            throw new InvalidDataException($"A digest is a zlib stream plus a {ZsContainer.TrailerLength}-byte trailer; this file has only {blob.Length} bytes.");

        try
        {
            using var input = new MemoryStream(blob, 0, blob.Length - ZsContainer.TrailerLength);
            using var stream = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            stream.CopyTo(output);

            return (output.ToArray(), blob[^ZsContainer.TrailerLength..]);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("The digest's zlib stream is not valid: " + exception.Message, exception);
        }
    }

    /// <summary>Compresses a body and appends the trailer.</summary>
    public static byte[] Pack(byte[] body, byte[] trailer)
    {
        var packed = ZsContainer.Deflate(body, 9);
        var output = new byte[packed.Length + trailer.Length];

        packed.CopyTo(output, 0);
        trailer.CopyTo(output, packed.Length);

        return output;
    }
}
