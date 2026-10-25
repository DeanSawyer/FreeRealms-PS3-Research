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

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;

        foreach (var value in data)
            crc = (crc >> 8) ^ Table[(crc ^ value) & 0xFF];

        return crc ^ uint.MaxValue;
    }
}
