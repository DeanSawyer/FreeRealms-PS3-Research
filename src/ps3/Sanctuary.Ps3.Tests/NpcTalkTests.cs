using System;
using System.Buffers.Binary;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>Talking to NPCs: the data, ShowDialog as the PS3 reads it, and a conversation from first line to EndDialog.</summary>
[TestClass]
public sealed class NpcTalkTests
{
    [TestMethod]
    public void QuestNpcsAndGreetersHaveLines()
    {
        Assert.IsTrue(NpcTalk.All.Count > 100, $"{NpcTalk.All.Count} NPCs");
        Assert.AreEqual(429535, NpcTalk.All[33605][0].TextId, "Nikki's greeting");
        Assert.IsTrue(NpcTalk.All.Keys.All(id => Npcs.All.Any(npc => npc.NpcId == id)), "only NPCs that are placed");
    }

    [TestMethod]
    public void ATalkerWithoutACursorGetsTheQuestCursor()
    {
        var talker = Npcs.All.First(npc => npc.NpcId == 33609) with { HasCursor = false, CursorId = 0 };
        var cow = Npcs.All.First(npc => npc.CursorId == 17 && !NpcTalk.All.ContainsKey(npc.NpcId));
        var silent = Npcs.All.First(npc => !npc.HasCursor && npc.CursorId == 0 && !NpcTalk.All.ContainsKey(npc.NpcId));

        Assert.AreEqual(NpcTalk.QuestCursorId, NpcTalk.WithCursor(talker).CursorId);
        Assert.AreEqual(cow, NpcTalk.WithCursor(cow));
        Assert.AreEqual(silent, NpcTalk.WithCursor(silent));
    }

    [TestMethod]
    public void ShowDialogIsWhatThePs3Reads()
    {
        var packet = NpcTalk.ShowDialog(new TalkLine(111, 0), 0x0200_0000_0000_0005, last: true);

        // 0x13e480 reads 49 bytes: header, text, title, guid, closable, camera focus, one response of five i32.
        Assert.AreEqual(4 + 4 + 4 + 8 + 1 + 4 + 4 + 5 * 4, packet.Length);
        Assert.AreEqual(26, BinaryPrimitives.ReadUInt16LittleEndian(packet));
        Assert.AreEqual(3, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.AreEqual(111, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)));
        Assert.AreEqual(0x0200_0000_0000_0005UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(12)));
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(25)), "one response");
        Assert.AreEqual(103085, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(37)), "\"You got it!\"");
        Assert.AreEqual(4008, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(41)), "the last line's leave image");
    }

    [TestMethod]
    public void AConversationEndsAfterItsLastLine()
    {
        var npc = Npcs.All[0];
        var conversation = new TalkConversation(npc, [new TalkLine(1, 0), new TalkLine(2, 5)]);

        Assert.AreEqual(1, BinaryPrimitives.ReadInt32LittleEndian(conversation.Current().AsSpan(4)));
        Assert.IsTrue(conversation.Next());
        Assert.AreEqual(2, BinaryPrimitives.ReadInt32LittleEndian(conversation.Current().AsSpan(4)));
        Assert.IsFalse(conversation.Next());

        CollectionAssert.AreEqual(new byte[] { 26, 0, 4, 0 }, NpcTalk.EndDialog());
        Assert.AreEqual(1, NpcTalk.FromResponse(new byte[] { 26, 0, 6, 0, 1, 0, 0, 0 }));
        Assert.IsNull(NpcTalk.FromResponse(new byte[] { 26, 0, 8, 0, 1, 0, 0, 0 }));
    }

    [TestMethod]
    public void AnInteractRequestNamesTheNpc()
    {
        var npc = Npcs.All[7];
        var request = new byte[13];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 26);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(4), npc.Guid);

        Assert.AreEqual(npc, NpcTalk.FromInteract(request));

        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(4), Warpstones.All[0].Guid);
        Assert.IsNull(NpcTalk.FromInteract(request), "a warpstone");
    }
}
