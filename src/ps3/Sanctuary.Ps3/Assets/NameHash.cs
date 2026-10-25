using System;

namespace Sanctuary.Ps3.Assets;

/// <summary>
/// The 64-bit hash the client uses as the key of the asset manifest: CRC-64 (reflected, polynomial
/// 0x95AC9329AC4BC9B5, all-ones start and final xor) of the name with only a-z folded to upper case.
/// Read from the client (function 0x1fac0, table 0xdb1f10); see docs/re/assets.md.
/// </summary>
public static class NameHash
{
    private const ulong Polynomial = 0x95AC9329AC4BC9B5UL;

    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];

        for (var i = 0; i < table.Length; i++)
        {
            var value = (ulong)i;

            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;

            table[i] = value;
        }

        return table;
    }

    public static ulong Compute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var crc = ulong.MaxValue;

        foreach (var character in name)
        {
            // Asset names are Latin-1 (in practice ASCII).
            if (character > 0xFF)
                throw new ArgumentException($"'{name}' is not a Latin-1 asset name.", nameof(name));

            var value = (byte)character;

            if (value is >= (byte)'a' and <= (byte)'z')
                value -= 32;

            crc = (crc >> 8) ^ Table[(int)((value ^ crc) & 0xFF)];
        }

        return crc ^ ulong.MaxValue;
    }
}
