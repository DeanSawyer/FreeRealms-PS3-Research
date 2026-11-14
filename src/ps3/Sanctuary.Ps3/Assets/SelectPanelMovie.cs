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
/// "add" for the new character line), selects the first one, follows the game's list as the character wheel fix turns it (each
/// turn sends the popups' DisplayText event with no text, one per step forward), and asks on Triangle
/// whether to delete the selected character (X deletes, Circle keeps it). The PS3 game sends the pad to a movie as key presses with
/// the ASCII codes of L, R, U, D, X, O, S and T (as its own <c>characterNameScreen</c> reads them). The character wheel fix turns the
/// game's own list with the same presses and opens the game's own delete prompt on Triangle, so the line the panel shows selected
/// is the character X plays or deletes. The PS3 version of the movie also has the Triangle hint <c>frTri</c> and the message line
/// <c>frMsg</c>, which the block uses when they are there.
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
        main.Push("frPresses", 0).Op(Avm1.SetVariable);
        main.Push("frAsking", 0).Op(Avm1.SetVariable);
        main.Push("frDeleting", 0).Op(Avm1.SetVariable);

        // frShow(i): select line i of the list and set the buttons for it (Play and Delete, or New character); the Triangle hint
        // above Delete (frTri, in the PS3 version of the movie) only shows for a saved character.
        //
        // The list only shows which line is selected by focus ("over"), which the list's own key handling used to give it: the line
        // shown before loses displayFocus, the new one gets it.
        var show = new Avm1();
        show.PushBool(false).Push(1);
        PushRenderer(show, "frShown");
        show.Push("__set__displayFocus").Op(Avm1.CallMethod).Op(Avm1.Pop);
        show.Push("i").Op(Avm1.GetVariable).Push(1, "characterList").Op(Avm1.GetVariable).Push("__set__selectedIndex").Op(Avm1.CallMethod).Op(Avm1.Pop);
        show.PushBool(true).Push(1);
        PushRenderer(show, "i");
        show.Push("__set__displayFocus").Op(Avm1.CallMethod).Op(Avm1.Pop);
        show.Push("frShown", "i").Op(Avm1.GetVariable).Op(Avm1.SetVariable);

        foreach (var (field, function) in new[] { ("id", "setCharacterId"), ("state", "setButtonState") })
        {
            show.Push("characterData").Op(Avm1.GetVariable).Push("i").Op(Avm1.GetVariable).Op(Avm1.GetMember).Push(field).Op(Avm1.GetMember);
            show.Push(1, function).Op(Avm1.CallFunction).Op(Avm1.Pop);
        }

        show.Push("frTri").Op(Avm1.GetVariable).Push("_visible");
        show.Push("characterData").Op(Avm1.GetVariable).Push("i").Op(Avm1.GetVariable).Op(Avm1.GetMember).Push("state").Op(Avm1.GetMember);
        show.Push("character").Op(Avm1.Equals2).Op(Avm1.SetMember);

        main.DefineFunction("frShow", ["i"], show);
        main.Push("frShown", 0).Op(Avm1.SetVariable);

        // The list must not move by itself on the pad (it did on long presses): only the game's list turns it.
        main.Push("characterList").Op(Avm1.GetVariable).Push("handleInput");
        main.DefineFunction(string.Empty, ["details", "pathToFocus"], new Avm1());
        main.Op(Avm1.SetMember);

        if (entries.Count > 0)
            main.Push(0, 1, "frShow").Op(Avm1.CallFunction).Op(Avm1.Pop);

        // The key listener. The game's own select prompt reads the same presses: left and right turn its list, Triangle opens its
        // delete prompt (X deletes, Circle keeps) for a saved character, X plays. The panel follows them.
        //
        // frKey(): the key of the last key event. A short press reaches the movie as a key down and a key up in the same frame, and
        // by the time the listeners run Key.getAscii() is the key up's (0): then Key.getCode() is used, and arrow codes count as the
        // letters the game sends (37 L, 38 U, 39 R, 40 D). frAscii and frCode keep the raw values for the presses line.
        var key = new Avm1();
        key.Push("frAscii", 0, "Key").Op(Avm1.GetVariable).Push("getAscii").Op(Avm1.CallMethod).Op(Avm1.SetVariable);
        key.Push("frCode", 0, "Key").Op(Avm1.GetVariable).Push("getCode").Op(Avm1.CallMethod).Op(Avm1.SetVariable);
        key.Push("frA", "frAscii").Op(Avm1.GetVariable).Op(Avm1.SetVariable);
        key.Push("frA").Op(Avm1.GetVariable).Push(0).Op(Avm1.Equals2).Op(Avm1.Not);
        var hasAscii = key.If();
        key.Push("frA", "frCode").Op(Avm1.GetVariable).Op(Avm1.SetVariable);
        key.Land(hasAscii);

        foreach (var (arrow, letter) in new[] { (37, 'L'), (38, 'U'), (39, 'R'), (40, 'D') })
        {
            key.Push("frA").Op(Avm1.GetVariable).Push(arrow).Op(Avm1.Equals2).Op(Avm1.Not);
            var other = key.If();
            key.Push("frA", (int)letter).Op(Avm1.SetVariable);
            key.Land(other);
        }

        key.Push("frA").Op(Avm1.GetVariable).Op(Avm1.Return);
        main.DefineFunction("frKey", [], key);

        // frPress(k): one press.
        var keys = new Avm1();
        keys.Push("frK", "k").Op(Avm1.GetVariable).Op(Avm1.SetVariable);
        keys.Push("frPresses", "frPresses").Op(Avm1.GetVariable).Op(Avm1.Increment).Op(Avm1.SetVariable);
        keys.Push("panel key ").Push("frK").Op(Avm1.GetVariable).Op(Avm1.Add2).Op(Avm1.Trace);

        // Deleting: nothing more until the game moves on.
        keys.Push("frDeleting").Op(Avm1.GetVariable).Op(Avm1.Not);
        var notDeleting = keys.If();
        keys.Push(0).Op(Avm1.Return);
        keys.Land(notDeleting);

        // Asked whether to delete: X deletes, Circle keeps the character.
        keys.Push("frAsking").Op(Avm1.GetVariable).Op(Avm1.Not);
        var notAsking = keys.If();
        var notCross = IfNotKey(keys, 'X');
        keys.Push("frDeleting", 1).Op(Avm1.SetVariable);
        Say(keys, text => { text.Push("Deleting "); PushSelectedName(text); text.Op(Avm1.Add2).Push("...").Op(Avm1.Add2); });
        keys.Land(notCross);
        var notCircle = IfNotKey(keys, 'O');
        keys.Push("frAsking", 0).Op(Avm1.SetVariable);
        Say(keys, text => text.Push(string.Empty));
        keys.Land(notCircle);
        keys.Push(0).Op(Avm1.Return);
        keys.Land(notAsking);

        // Which presses arrive (for now, to see whether the panel misses any): "press 7: R".
        Say(keys, text =>
        {
            text.Push("press ").Push("frPresses").Op(Avm1.GetVariable).Op(Avm1.Add2).Push(": ").Op(Avm1.Add2).Push("frK").Op(Avm1.GetVariable).Op(Avm1.Add2);
            text.Push(" (").Op(Avm1.Add2).Push("frAscii").Op(Avm1.GetVariable).Op(Avm1.Add2).Push("/").Op(Avm1.Add2).Push("frCode").Op(Avm1.GetVariable).Op(Avm1.Add2).Push(")").Op(Avm1.Add2);
        });

        // T (Triangle) on a saved character: ask.
        var notTriangle = IfNotKey(keys, 'T');
        keys.Push("characterData").Op(Avm1.GetVariable).Push("frSel").Op(Avm1.GetVariable).Op(Avm1.GetMember).Push("state").Op(Avm1.GetMember);
        keys.Push("character").Op(Avm1.Equals2).Op(Avm1.Not);
        var notSaved = keys.If();
        keys.Push("frAsking", 1).Op(Avm1.SetVariable);
        Say(keys, text => { text.Push("Delete "); PushSelectedName(text); text.Op(Avm1.Add2).Push("?  X: delete   O: keep").Op(Avm1.Add2); });
        keys.Land(notSaved);
        keys.Land(notTriangle);

        main.DefineFunction("frPress", ["k"], keys);

        // The listener: a key down is a press; a key up is one too when its key down did not come through as that key.
        var down = new Avm1();
        down.Push("frDown", 0, "frKey").Op(Avm1.CallFunction).Op(Avm1.SetVariable);
        down.Push("frDown").Op(Avm1.GetVariable).Push(1, "frPress").Op(Avm1.CallFunction).Op(Avm1.Pop);

        var up = new Avm1();
        up.Push("frUp", 0, "frKey").Op(Avm1.CallFunction).Op(Avm1.SetVariable);
        up.Push("frUp").Op(Avm1.GetVariable).Push("frDown").Op(Avm1.GetVariable).Op(Avm1.Equals2);
        var seen = up.If();
        up.Push("frUp").Op(Avm1.GetVariable).Push(1, "frPress").Op(Avm1.CallFunction).Op(Avm1.Pop);
        up.Land(seen);
        up.Push("frDown", 0).Op(Avm1.SetVariable);

        // DisplayText(text): the game's popups script passes the popups' DisplayText event on to this movie. The character wheel fix
        // sends it with no text once for each step its list turns forward (a step back is count - 1 steps forward), so the selection
        // follows the game's list whatever key presses reach the movie; the game's own DisplayText always has a text, and is left alone
        // (no text, from the script, is nil: undefined or null here).
        var step = new Avm1();

        // For now: count the calls and show them ("DisplayText 3: undefined"), also with trace, to see whether they come through.
        step.Push("frTurns", "frTurns").Op(Avm1.GetVariable).Op(Avm1.Increment).Op(Avm1.SetVariable);
        step.Push("DisplayText ").Push("frTurns").Op(Avm1.GetVariable).Op(Avm1.Add2).Push(": ").Op(Avm1.Add2).Push("t").Op(Avm1.GetVariable).Op(Avm1.Add2).Op(Avm1.Trace);
        Say(step, text => text.Push("DisplayText ").Push("frTurns").Op(Avm1.GetVariable).Op(Avm1.Add2).Push(": ").Op(Avm1.Add2).Push("t").Op(Avm1.GetVariable).Op(Avm1.Add2));
        step.Push("t").Op(Avm1.GetVariable).PushUndefined().Op(Avm1.Equals2).Op(Avm1.Not);
        var hasText = step.If();
        step.Push("frSel", "frSel").Op(Avm1.GetVariable).Push(1).Op(Avm1.Add2).Push("frCount").Op(Avm1.GetVariable).Op(Avm1.Modulo).Op(Avm1.SetVariable);
        step.Push("frSel").Op(Avm1.GetVariable).Push(1, "frShow").Op(Avm1.CallFunction).Op(Avm1.Pop);
        step.Land(hasText);
        main.DefineFunction("DisplayText", ["t"], step);
        main.DefineFunction("SetButtonVisibility", ["v"], new Avm1());

        main.Push("frDown", 0).Op(Avm1.SetVariable);
        main.Push("frTurns", 0).Op(Avm1.SetVariable);
        main.Push("frKeys", 0, "Object").Op(Avm1.NewObject).Op(Avm1.SetVariable);
        main.Push("frKeys").Op(Avm1.GetVariable).Push("onKeyDown");
        main.DefineFunction(string.Empty, [], down);
        main.Op(Avm1.SetMember);
        main.Push("frKeys").Op(Avm1.GetVariable).Push("onKeyUp");
        main.DefineFunction(string.Empty, [], up);
        main.Op(Avm1.SetMember);
        main.Push("frKeys").Op(Avm1.GetVariable).Push(1, "Key").Op(Avm1.GetVariable).Push("addListener").Op(Avm1.CallMethod).Op(Avm1.Pop);

        main.Op(0);

        return main.ToArray();
    }

    /// <summary>Compares the key of the press with <paramref name="key"/>; returns the If that skips what follows when it is another key.</summary>
    private static int IfNotKey(Avm1 code, char key)
    {
        code.Push("frK").Op(Avm1.GetVariable).Push((int)key).Op(Avm1.Equals2).Op(Avm1.Not);

        return code.If();
    }

    /// <summary>Puts a line of text in the panel's message field (frMsg, in the PS3 version of the movie), centred.</summary>
    private static void Say(Avm1 code, Action<Avm1> text)
    {
        code.Push("frMsg").Op(Avm1.GetVariable).Push("htmlText").Push("<p align=\"center\">");
        text(code);
        code.Op(Avm1.Add2).Push("</p>").Op(Avm1.Add2).Op(Avm1.SetMember);
    }

    /// <summary>Pushes the list's renderer for the line in the variable <paramref name="index"/>.</summary>
    private static void PushRenderer(Avm1 code, string index) =>
        code.Push(index).Op(Avm1.GetVariable).Push(1, "characterList").Op(Avm1.GetVariable).Push("getRendererAt").Op(Avm1.CallMethod);

    /// <summary>Pushes the selected line's name.</summary>
    private static void PushSelectedName(Avm1 code) =>
        code.Push("characterData").Op(Avm1.GetVariable).Push("frSel").Op(Avm1.GetVariable).Op(Avm1.GetMember).Push("name").Op(Avm1.GetMember);

    /// <summary>A small AVM1 (ActionScript 2 bytecode) writer: the actions this block needs, pushes of strings and integers, If, and functions.</summary>
    internal sealed class Avm1
    {
        public const byte Add2 = 0x47;
        public const byte CallFunction = 0x3D;
        public const byte CallMethod = 0x52;
        public const byte Equals2 = 0x49;
        public const byte GetMember = 0x4E;
        public const byte GetVariable = 0x1C;
        public const byte Increment = 0x50;
        public const byte Modulo = 0x3F;
        public const byte NewObject = 0x40;
        public const byte Not = 0x12;
        public const byte Pop = 0x17;
        public const byte Return = 0x3E;
        public const byte SetMember = 0x4F;
        public const byte SetVariable = 0x1D;
        public const byte Trace = 0x26;

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

        /// <summary>Pushes a Boolean.</summary>
        public Avm1 PushBool(bool value) => Action(0x96, [5, value ? (byte)1 : (byte)0]);

        /// <summary>Pushes undefined (equal to null and undefined with Equals2).</summary>
        public Avm1 PushUndefined() => Action(0x96, [3]);

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
