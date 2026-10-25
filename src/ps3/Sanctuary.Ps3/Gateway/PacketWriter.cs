using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>Builds a little-endian packet field by field (the PS3 application protocol is little endian; UdpLibrary headers are big endian).</summary>
public sealed class PacketWriter
{
    private readonly MemoryStream _data = new();

    public int Length => (int)_data.Length;

    public PacketWriter U8(int value)
    {
        _data.WriteByte(checked((byte)value));

        return this;
    }

    public PacketWriter U16(int value)
    {
        Span<byte> buffer = stackalloc byte[2];

        BinaryPrimitives.WriteUInt16LittleEndian(buffer, checked((ushort)value));
        _data.Write(buffer);

        return this;
    }

    public PacketWriter I32(int value)
    {
        Span<byte> buffer = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _data.Write(buffer);

        return this;
    }

    public PacketWriter U32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _data.Write(buffer);

        return this;
    }

    public PacketWriter I64(long value)
    {
        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        _data.Write(buffer);

        return this;
    }

    public PacketWriter U64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _data.Write(buffer);

        return this;
    }

    /// <summary>A 32-bit float (a double is rounded to the nearest float, like <c>struct.pack("&lt;f")</c>).</summary>
    public PacketWriter F32(double value)
    {
        Span<byte> buffer = stackalloc byte[4];

        BinaryPrimitives.WriteSingleLittleEndian(buffer, (float)value);
        _data.Write(buffer);

        return this;
    }

    /// <summary>A string: <c>u32</c> length, then the UTF-8 bytes.</summary>
    public PacketWriter Str(string value)
    {
        var raw = new UTF8Encoding(false).GetBytes(value);

        U32((uint)raw.Length);
        _data.Write(raw);

        return this;
    }

    public PacketWriter Raw(ReadOnlySpan<byte> bytes)
    {
        _data.Write(bytes);

        return this;
    }

    public PacketWriter Zeros(int count)
    {
        _data.Write(new byte[count]);

        return this;
    }

    public byte[] ToArray() => _data.ToArray();
}
