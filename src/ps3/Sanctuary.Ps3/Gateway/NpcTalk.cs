using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One line an NPC says: its text, and the caption of the button that answers it (0 = the generic "You got it!").</summary>
public sealed record TalkLine(int TextId, int ResponseTextId);

/// <summary>
/// Talking to NPCs: what each says when the player presses X at it (<c>Data/npc_talk.tsv</c>, from Sanctuary's quests and greeting
/// scripts) and the dialog packets that show it. Pressing X sends Command (26) 8, InteractRequest; the server answers with
/// Command 3, ShowDialog, one line at a time. The player's button press comes back as Command 6, DialogResponse, and after the last
/// line the server closes the dialog with Command 4, EndDialog, as Sanctuary's QuestDialogue does.
/// </summary>
public static class NpcTalk
{
    /// <summary>Sanctuary's quest cursor (InteractionMenuHelper.QuestCursorId): an NPC with something to say and no cursor of its own gets it.</summary>
    public const byte QuestCursorId = 5;

    public const int ShowDialogSub = 3;
    public const int EndDialogSub = 4;
    public const int DialogResponseSub = 6;

    // Sanctuary's QuestDialogue: "You got it!", the plus and leave images, the green button image set.
    private const int YouGotItTextId = 103085;
    private const int PlusImageId = 303;
    private const int LeaveImageId = 4008;
    private const int GreenButtonImageSet = 17;

    private const string Resource = "Sanctuary.Ps3.npc_talk.tsv";

    private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<TalkLine>>> Loaded = new(Load);

    /// <summary>The lines of every NPC that has something to say, by NPC id.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<TalkLine>> All => Loaded.Value;

    /// <summary>The NPC as the client gets it: one with something to say and no cursor of its own gets the quest cursor, as Sanctuary gives quest NPCs; a merchant without one gets cursor 17.</summary>
    public static NpcSpawn WithCursor(NpcSpawn npc)
    {
        if (npc.HasCursor || npc.CursorId != 0)
            return npc;

        if (All.ContainsKey(npc.NpcId))
            return npc with { HasCursor = true, CursorId = QuestCursorId };

        // A merchant without a cursor of its own gets the one Sanctuary's merchants and warpstones have.
        return Merchants.IsMerchant(npc) ? npc with { HasCursor = true, CursorId = MerchantCursorId } : npc;
    }

    /// <summary>The cursor of Sanctuary's merchants and warpstones (17, "use").</summary>
    public const byte MerchantCursorId = 17;

    /// <summary>
    /// Command (26) 3, ShowDialog, as the PS3 reads it (sub-opcode 3 of the Command handler <c>0x13eac8</c>, read by <c>0x13e480</c>):
    /// <c>i32</c> text, <c>i32</c> title, <c>u64</c> NPC guid, <c>u8</c> closable, <c>f32</c> camera focus, then the responses
    /// (<c>u32</c> count, per response <c>i32</c> id, action type, label text, image, image set). Sanctuary's packet goes on with
    /// camera and timing fields the PS3 does not read; they are left out.
    /// </summary>
    public static byte[] ShowDialog(TalkLine line, ulong npcGuid, bool last)
        => new PacketWriter()
            .U16(Warpstones.CommandOpcode).U16(ShowDialogSub)
            .I32(line.TextId)
            .I32(0)
            .U64(npcGuid)
            .U8(1)
            .F32(1)
            .U32(1)
            .I32(1).I32(0).I32(line.ResponseTextId != 0 ? line.ResponseTextId : YouGotItTextId)
            .I32(last ? LeaveImageId : PlusImageId).I32(GreenButtonImageSet)
            .ToArray();

    /// <summary>Command (26) 4, EndDialog: nothing after the sub-opcode (the PS3 reads no more, <c>0x13b6bc</c>).</summary>
    public static byte[] EndDialog() => new PacketWriter().U16(Warpstones.CommandOpcode).U16(EndDialogSub).ToArray();

    /// <summary>The response id of a Command (26) 6, DialogResponse (<c>i32</c> after the sub-opcode; the client builds it at <c>0x171ad8</c>), or null.</summary>
    public static int? FromResponse(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < 4
            || BinaryPrimitives.ReadUInt16LittleEndian(inner) != Warpstones.CommandOpcode
            || BinaryPrimitives.ReadUInt16LittleEndian(inner[2..]) != DialogResponseSub)
        {
            return null;
        }

        return inner.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(inner[4..]) : 0;
    }

    /// <summary>The NPC an InteractRequest (Command 26, sub 8, <c>u64</c> guid) is for, or null when it is not one of <see cref="Npcs.All"/>.</summary>
    public static NpcSpawn? FromInteract(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < 12
            || BinaryPrimitives.ReadUInt16LittleEndian(inner) != Warpstones.CommandOpcode
            || BinaryPrimitives.ReadUInt16LittleEndian(inner[2..]) != Warpstones.InteractRequestSub)
        {
            return null;
        }

        var guid = BinaryPrimitives.ReadUInt64LittleEndian(inner[4..]);

        return guid >= Npcs.GuidBase && guid - Npcs.GuidBase < (ulong)Npcs.All.Count ? Npcs.All[(int)(guid - Npcs.GuidBase)] : null;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<TalkLine>> Load()
    {
        using var stream = typeof(NpcTalk).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not in the assembly.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>The lines in the data file's text (see its header).</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<TalkLine>> Parse(string text)
    {
        var all = new Dictionary<int, IReadOnlyList<TalkLine>>();

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            var lines = c[3].Split(',').Select(pair =>
            {
                var parts = pair.Split('/');
                return new TalkLine(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            }).ToList();

            all[int.Parse(c[0], CultureInfo.InvariantCulture)] = lines;
        }

        return all;
    }
}

/// <summary>One conversation in progress: the NPC and the lines still to come.</summary>
public sealed class TalkConversation(NpcSpawn npc, IReadOnlyList<TalkLine> lines)
{
    private int _index;

    public NpcSpawn Npc => npc;

    /// <summary>ShowDialog for the line now being said.</summary>
    public byte[] Current() => NpcTalk.ShowDialog(lines[_index], npc.Guid, _index == lines.Count - 1);

    /// <summary>Moves on after the player answered; false when that was the last line.</summary>
    public bool Next() => ++_index < lines.Count;
}
