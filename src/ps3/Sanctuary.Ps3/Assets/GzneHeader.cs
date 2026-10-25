using System;
using System.Buffers.Binary;

namespace Sanctuary.Ps3.Assets;

/// <summary>
/// Header of a <c>&lt;zone&gt;.gzne</c> terrain file as the client's parser reads it (0x7df87c). <see cref="Edge"/> is the
/// number of cells along a tile edge (A / B, stored at terrain+0x440); the cell box is given in cells.
/// </summary>
public sealed record GzneHeader(
    int Version,
    int A,
    int B,
    int Edge,
    double CellSize,
    int MinX,
    int MinZ,
    int ExtX,
    int ExtZ,
    bool BigEndian)
{
    /// <summary>Parses the header, or returns null when the data is not a GZNE file.</summary>
    public static GzneHeader? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x30 || data[0] != 'G' || data[1] != 'Z' || data[2] != 'N' || data[3] != 'E')
            return null;

        var version = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        var bigEndian = version is > 0 and < 256;

        int Int(int offset) => bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

        var cellSize = bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[0x10..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[0x10..]);

        var a = Int(8);
        var b = Int(12);

        return new GzneHeader(Int(4), a, b, b != 0 ? FloorDivide(a, b) : 0, cellSize, Int(0x18), Int(0x1C), Int(0x20), Int(0x24), bigEndian);
    }

    /// <summary>Floor division like Python's <c>//</c>, done in 64 bits so int.MinValue / -1 cannot throw.</summary>
    private static int FloorDivide(int value, int divisor)
    {
        long dividend = value;
        long quotient = dividend / divisor;

        if (dividend % divisor != 0 && ((dividend < 0) != (divisor < 0)))
            quotient--;

        return unchecked((int)quotient);
    }
}
