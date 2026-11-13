using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sanctuary.Ps3.Assets;

/// <summary>One line of the select panel: the id the panel reports, the name it shows, and whether it is the "make a new character" line.</summary>
public sealed record PanelEntry(string Id, string Name, bool IsNew);

/// <summary>
/// Fills the PC client's character select movie (<c>characterSelectV2.gfx</c>) for the PS3: adds an ActionScript block to the end of
/// its first frame that calls the movie's own <c>addCharacter(index, id, name, state)</c> for each character (state "character", or
/// "add" for the new character line), selects the first one, and turns the selection with D-pad left and right. The PS3 game sends
/// the pad to a movie as key presses with the ASCII codes of L, R, U, D, X, O, S and T (as its own <c>characterNameScreen</c>
/// reads them). The character wheel fix turns the game's own list with the same presses, so the line the panel shows selected is
/// the character X plays.
/// </summary>
public static class SelectPanelMovie
{
    private const ushort ShowFrame = 1;
    private const ushort DoAction = 12;

    /// <summary>The movie with the block added (same header kind; a compressed movie stays compressed).</summary>
    public static byte[] WithCharacters(ReadOnlySpan<byte> movie, IReadOnlyList<PanelEntry> entries)
    {
        if (movie.Length < 8)
            throw new InvalidDataException("Not a Flash movie.");

        var signature = movie[0];
        var compressed = signature is (byte)'C';

        if (signature is not ((byte)'C' or (byte)'F' or (byte)'G') || movie[1] is not ((byte)'F' or (byte)'W') || movie[2] is not ((byte)'X' or (byte)'S'))
            throw new InvalidDataException("Not a Flash movie (no FWS, CWS, GFX or CFX header).");

        var body = compressed ? Inflate(movie[8..]) : movie[8..].ToArray();

        // The frame rectangle, frame rate and frame count come before the tags.
        var bits = body[0] >> 3;
        var position = (5 + 4 * bits + 7) / 8 + 4;
        var insertAt = -1;

        while (position + 2 <= body.Length)
        {
            var start = position;
            var header = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position));
            var code = header >> 6;
            var length = header & 63;
            position += 2;

            if (length == 63)
            {
                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(position));
                position += 4;
            }

            if (code == ShowFrame)
            {
                insertAt = start;
                break;
            }

            if (code == 0)
                break;

            position += length;
        }

        if (insertAt < 0)
            throw new InvalidDataException("The movie has no first frame to add to.");

        var action = Actions(entries);
        var tag = new byte[6 + action.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(tag, (DoAction << 6) | 63);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.AsSpan(2), (uint)action.Length);
        action.CopyTo(tag, 6);

        var result = new byte[body.Length + tag.Length];
        body.AsSpan(0, insertAt).CopyTo(result);
        tag.CopyTo(result, insertAt);
        body.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + tag.Length));

        using var output = new MemoryStream();
        output.Write(movie[..4]);

        Span<byte> length32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length32, (uint)(result.Length + 8));
        output.Write(length32);

        if (compressed)
        {
            using var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
            zlib.Write(result);
        }
        else
        {
            output.Write(result);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        return output.ToArray();
    }

    /// <summary>The ActionScript (AVM1) of the added block.</summary>
    public static byte[] Actions(IReadOnlyList<PanelEntry> entries)
    {
        var main = new Avm1();

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            // addCharacter(index, id, name, state): arguments are pushed last first.
            main.Push(entry.IsNew ? "add" : "character", entry.Name, entry.Id, index, 4, "addCharacter");
            main.Op(Avm1.CallFunction).Op(Avm1.Pop);
        }

        main.Push("frCount", Math.Max(1, entries.Count)).Op(Avm1.SetVariable);
        main.Push("frSel", 0).Op(Avm1.SetVariable);

        // frShow(i): select line i of the list and set the buttons for it (Play and Delete, or New character).
        var show = new Avm1();
        show.Push("i").Op(Avm1.GetVariable).Push(1, "characterList").Op(Avm1.GetVariable).Push("__set__selectedIndex").Op(Avm1.CallMethod).Op(Avm1.Pop);

        foreach (var (field, function) in new[] { ("id", "setCharacterId"), ("state", "setButtonState") })
        {
            show.Push("characterData").Op(Avm1.GetVariable).Push("i").Op(Avm1.GetVariable).Op(Avm1.GetMember).Push(field).Op(Avm1.GetMember);
            show.Push(1, function).Op(Avm1.CallFunction).Op(Avm1.Pop);
        }

        main.DefineFunction("frShow", ["i"], show);

        if (entries.Count > 0)
            main.Push(0, 1, "frShow").Op(Avm1.CallFunction).Op(Avm1.Pop);

        // A key listener: R (D-pad right) selects the next line, L (left) the previous one, wrapping around as the game's list does.
        var keys = new Avm1();
        keys.Push("frK", 0, "Key").Op(Avm1.GetVariable).Push("getAscii").Op(Avm1.CallMethod).Op(Avm1.SetVariable);

        foreach (var (code, step) in new[] { ((int)'R', 1), ((int)'L', -1) })
        {
            keys.Push("frK").Op(Avm1.GetVariable).Push(code).Op(Avm1.Equals2).Op(Avm1.Not);
            var skip = keys.If();

            // frSel = (frSel + frCount + step) % frCount
            keys.Push("frSel", "frSel").Op(Avm1.GetVariable).Push("frCount").Op(Avm1.GetVariable).Op(Avm1.Add2).Push(step).Op(Avm1.Add2);
            keys.Push("frCount").Op(Avm1.GetVariable).Op(Avm1.Modulo).Op(Avm1.SetVariable);
            keys.Push("frSel").Op(Avm1.GetVariable).Push(1, "frShow").Op(Avm1.CallFunction).Op(Avm1.Pop);
            keys.Land(skip);
        }

        main.Push("frKeys", 0, "Object").Op(Avm1.NewObject).Op(Avm1.SetVariable);
        main.Push("frKeys").Op(Avm1.GetVariable).Push("onKeyDown");
        main.DefineFunction(string.Empty, [], keys);
        main.Op(Avm1.SetMember);
        main.Push("frKeys").Op(Avm1.GetVariable).Push(1, "Key").Op(Avm1.GetVariable).Push("addListener").Op(Avm1.CallMethod).Op(Avm1.Pop);

        main.Op(0);

        return main.ToArray();
    }

    /// <summary>A small AVM1 (ActionScript 2 bytecode) writer: the actions this block needs, pushes of strings and integers, If, and functions.</summary>
    internal sealed class Avm1
    {
        public const byte Add2 = 0x47;
        public const byte CallFunction = 0x3D;
        public const byte CallMethod = 0x52;
        public const byte Equals2 = 0x49;
        public const byte GetMember = 0x4E;
        public const byte GetVariable = 0x1C;
        public const byte Modulo = 0x3F;
        public const byte NewObject = 0x40;
        public const byte Not = 0x12;
        public const byte Pop = 0x17;
        public const byte SetMember = 0x4F;
        public const byte SetVariable = 0x1D;

        private readonly List<byte> _code = [];

        public Avm1 Op(byte code)
        {
            _code.Add(code);

            return this;
        }

        public Avm1 Push(params object[] values)
        {
            var payload = new List<byte>();

            foreach (var value in values)
            {
                switch (value)
                {
                    case string text:
                        payload.Add(0);
                        payload.AddRange(Encoding.UTF8.GetBytes(text));
                        payload.Add(0);
                        break;

                    case int number:
                        payload.Add(7);
                        payload.AddRange(BitConverter.GetBytes(number));
                        break;

                    default:
                        throw new ArgumentException($"Cannot push {value?.GetType().Name ?? "null"}.", nameof(values));
                }
            }

            return Action(0x96, payload);
        }

        /// <summary>An If whose target is set by <see cref="Land"/>; returns where its offset is.</summary>
        public int If()
        {
            Action(0x9D, [0, 0]);

            return _code.Count - 2;
        }

        /// <summary>Points the If at <paramref name="offsetAt"/> to the next action.</summary>
        public void Land(int offsetAt)
        {
            var offset = (short)(_code.Count - (offsetAt + 2));
            _code[offsetAt] = (byte)offset;
            _code[offsetAt + 1] = (byte)(offset >> 8);
        }

        /// <summary>DefineFunction (SWF 5 style): the function's name (empty for an anonymous one), its parameters and its body.</summary>
        public Avm1 DefineFunction(string name, string[] parameters, Avm1 body)
        {
            var payload = new List<byte>();
            payload.AddRange(Encoding.UTF8.GetBytes(name));
            payload.Add(0);
            payload.AddRange(BitConverter.GetBytes((ushort)parameters.Length));

            foreach (var parameter in parameters)
            {
                payload.AddRange(Encoding.UTF8.GetBytes(parameter));
                payload.Add(0);
            }

            var code = body.ToArray();
            payload.AddRange(BitConverter.GetBytes((ushort)code.Length));
            Action(0x9B, payload);
            _code.AddRange(code);

            return this;
        }

        public byte[] ToArray() => [.. _code];

        private Avm1 Action(byte code, IReadOnlyCollection<byte> payload)
        {
            _code.Add(code);
            _code.AddRange(BitConverter.GetBytes((ushort)payload.Count));
            _code.AddRange(payload);

            return this;
        }
    }
}
