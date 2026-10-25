using System;

namespace Sanctuary.Ps3.Assets;

/// <summary>CRC-32 as used by zlib (polynomial 0xEDB88320), which is what the packs and the digest store.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;

            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Continues a CRC-32 over more data, like <c>zlib.crc32(data, crc)</c> (start with 0).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc ^= uint.MaxValue;

        foreach (var value in data)
            crc = (crc >> 8) ^ Table[(crc ^ value) & 0xFF];

        return crc ^ uint.MaxValue;
    }

    /// <summary>CRC-32 of a whole file, read in blocks.</summary>
    public static uint OfFile(string path)
    {
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);

        var buffer = new byte[1 << 20];
        var crc = 0u;

        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            crc = Update(crc, buffer.AsSpan(0, read));

        return crc;
    }
}
