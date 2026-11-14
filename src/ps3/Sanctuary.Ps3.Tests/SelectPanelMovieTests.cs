using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

/// <summary>Filling the select panel movie with the characters.</summary>
[TestClass]
public sealed class SelectPanelMovieTests
{
    private static readonly PanelEntry[] Entries =
    [
        new("3", "Aedon Amberarm", false),
        new("2", "Kayla Starfeather", false),
        new("2147418112", "New character", true),
    ];

    [TestMethod]
    public void TheBlockIsAddedToTheFirstFrameOfACompressedMovie()
    {
        var filled = SelectPanelMovie.WithCharacters(Movie(compressed: true), Entries);

        Assert.AreEqual("CFX", Encoding.ASCII.GetString(filled, 0, 3));
        Assert.AreEqual(10, filled[3], "the version is kept");

        var body = Inflate(filled.AsSpan(8));
        Assert.AreEqual(body.Length + 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(filled.AsSpan(4)), "the header's length is the uncompressed length");

        var tags = Tags(body);
        CollectionAssert.AreEqual(new[] { 9, 12, 12, 1, 0 }, tags.Select(tag => tag.Code).ToArray(), "the movie's own block, then the new one, before the first ShowFrame");

        var added = tags[2].Body;
        var text = Encoding.UTF8.GetString(added);

        foreach (var entry in Entries)
            StringAssert.Contains(text, entry.Name);

        StringAssert.Contains(text, "addCharacter");
        StringAssert.Contains(text, "character\0");
        StringAssert.Contains(text, "add\0");
        StringAssert.Contains(text, "? X: yes, O: no");
        Assert.AreEqual(0, added[^1], "the block ends with ActionEnd");

        // The bytes, for reading them with an AVM1 disassembler.
        Console.WriteLine("select panel block: " + Convert.ToHexString(added));
    }

    [TestMethod]
    public void AnUncompressedMovieStaysUncompressed()
    {
        var filled = SelectPanelMovie.WithCharacters(Movie(compressed: false), Entries);

        Assert.AreEqual("FWS", Encoding.ASCII.GetString(filled, 0, 3));
        Assert.AreEqual(filled.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(filled.AsSpan(4)));
        Assert.AreEqual(5, Tags(filled[8..]).Count);
    }

    [TestMethod]
    public void TheActionsAreWholeActions()
    {
        // Walking the block action by action ends exactly at its last byte (each action's length field is right).
        var code = SelectPanelMovie.Actions(Entries);
        var position = 0;
        var functions = 0;

        while (position < code.Length)
        {
            var op = code[position++];

            if (op >= 0x80)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(position));
                position += 2 + length;

                if (op == 0x9B)
                    functions++;
            }
        }

        Assert.AreEqual(code.Length, position);
        Assert.AreEqual(8, functions, "frShow, the list's handleInput, frKey, frPress, DisplayText, SetButtonVisibility, and the listener's onKeyDown and onKeyUp");
    }

    [TestMethod]
    public void NotAMovieIsAnError()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SelectPanelMovie.WithCharacters(Encoding.ASCII.GetBytes("not a movie at all"), Entries));
    }

    /// <summary>A movie with a 1-byte frame rectangle (no bits), a SetBackgroundColor, a DoAction, ShowFrame and End.</summary>
    private static byte[] Movie(bool compressed)
    {
        var body = new List<byte> { 0x00, 0x00, 0x18, 0x01, 0x00 };

        void Tag(int code, byte[] data)
        {
            body.AddRange(BitConverter.GetBytes((ushort)((code << 6) | data.Length)));
            body.AddRange(data);
        }

        Tag(9, [0xff, 0xff, 0xff]);
        Tag(12, [0x07, 0x00]);
        Tag(1, []);
        Tag(0, []);

        var header = new List<byte>(Encoding.ASCII.GetBytes(compressed ? "CFX" : "FWS")) { 10 };
        header.AddRange(BitConverter.GetBytes((uint)(body.Count + 8)));

        if (!compressed)
            return [.. header, .. body];

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(body.ToArray());

        return [.. header, .. output.ToArray()];
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        return output.ToArray();
    }

    private static List<(int Code, byte[] Body)> Tags(byte[] body)
    {
        var tags = new List<(int, byte[])>();
        var position = (5 + 4 * (body[0] >> 3) + 7) / 8 + 4;

        while (position < body.Length)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position));
            var length = header & 63;
            position += 2;

            if (length == 63)
            {
                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(position));
                position += 4;
            }

            tags.Add((header >> 6, body.AsSpan(position, length).ToArray()));
            position += length;

            if (header >> 6 == 0)
                break;
        }

        return tags;
    }
}
