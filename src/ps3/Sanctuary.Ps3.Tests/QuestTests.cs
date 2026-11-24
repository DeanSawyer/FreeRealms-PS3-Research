using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The quests: the data, the Quest (49) packets as the PS3 reads them, and a quest from offer to reward.</summary>
[TestClass]
public sealed class QuestTests
{
    private static readonly Dictionary<string, string> Creator = new() { ["FirstName"] = "Aedon", ["Model"] = "1", ["Head"] = "1" };

    private static NpcSpawn Npc(int npcId) => Npcs.All.First(npc => npc.NpcId == npcId);

    [TestMethod]
    public void SanctuarysQuestsAndPickupsAreLoaded()
    {
        Assert.AreEqual(68, Quests.All.Count);
        Assert.IsTrue(Quests.All.Values.All(quest => quest.Goals.Count > 0));

        var keys = Quests.All.Values.SelectMany(quest => quest.Goals).Where(goal => goal.Type == QuestGoalDef.Collect).Select(goal => goal.CollectNodeType).Distinct();
        Assert.IsTrue(keys.All(key => Quests.Nodes.Count(node => node.Key == key) > 0), "every collect goal has pickups");
        Assert.AreEqual(Quests.Nodes.Count, Quests.Nodes.Select(node => node.Guid).Distinct().Count());
    }

    [TestMethod]
    public void QuestPacketsAreAsLongAsThePs3Reads()
    {
        var quest = Quests.All[2563];
        var goal = quest.Goals[0];
        var state = new QuestState { QuestId = quest.QuestId };

        // Lengths from tools/ppc_trace.py on the Quest handler 0x76aec.
        Assert.AreEqual(101, Quests.Info(quest, 5).Length);
        Assert.AreEqual(108, Quests.Add(quest, state, 9, true, false).Length);
        Assert.AreEqual(101, Quests.ObjectiveAdded(quest.QuestId, goal, Quests.StateActive).Length);
        Assert.AreEqual(19, Quests.ObjectiveActivated(quest.QuestId, goal, false).Length);
        Assert.AreEqual(22, Quests.ObjectiveUpdate(quest.QuestId, goal, 1, 0.5f).Length);
        Assert.AreEqual(23, Quests.ObjectiveComplete(quest.QuestId, goal, 1, true).Length);
        Assert.AreEqual(10, Quests.Complete(quest.QuestId).Length);
        Assert.AreEqual(87, Quests.End(quest, 5).Length);

        var info = Quests.Info(quest, 0x0200_0000_0000_0005);
        Assert.AreEqual(49, BinaryPrimitives.ReadUInt16LittleEndian(info));
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(2)));
        Assert.AreEqual(quest.QuestId, BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(6)));
        Assert.AreEqual(quest.RewardCoins, BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(0x20)));
        Assert.AreEqual(0x0200_0000_0000_0005UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(0x58)));
    }

    [TestMethod]
    public void TheClientsAnswersAreRead()
    {
        byte[] reply = [49, 0, 2, 0, 0, 0, 0x03, 0x0a, 0, 0, 1];
        Assert.AreEqual((2563, true), Quests.FromReply(reply));

        byte[] end = [49, 0, 14, 0, 0, 0, 0x03, 0x0a, 0, 0];
        Assert.AreEqual(2563, Quests.FromEndReply(end));
        Assert.IsNull(Quests.FromEndReply(reply));
    }

    [TestMethod]
    public void AQuestFromOfferToReward()
    {
        var saved = 0;
        var session = new QuestSession(new QuestLog(), 9, _ => saved++);
        var samantha = Npc(2045);
        var ricky = Npc(2049);
        var inventory = new Inventory(Creator, new GatewayOptions { Sanctuary = true });
        var coins = inventory.Coins;

        // The giver shows "!" and gets the quest cursor; the target nothing yet.
        session.NpcsChanged([samantha, ricky], []);
        Assert.AreEqual(Quests.All[2563].NotificationAvailable, session.Wanted(samantha).Badge?.IconState);
        Assert.IsTrue(session.Wanted(samantha).QuestCursor);

        var offer = session.Interact(samantha);
        Assert.IsNotNull(offer);
        Assert.AreEqual("QuestInfo 2563", offer.Packets.Single().Name);

        var taken = session.Reply(2563, accepted: true);
        CollectionAssert.IsSubsetOf(new[] { "QuestAdd 2563", "ObjectiveAdded 2563/0", "ObjectiveActivated 2563/0", "QuestDialogComplete" }, taken.Select(packet => packet.Name).ToList());
        Assert.IsTrue(session.Log.InProgress.ContainsKey(2563));
        Assert.AreEqual(Quests.All[2563].NotificationActive, session.Wanted(ricky).Badge?.IconState, "\"?\" over the NPC the quest waits on");
        Assert.IsTrue(taken.Any(packet => packet.Name.StartsWith("RemoveNotifications", StringComparison.Ordinal)), "the giver's \"!\" goes");

        var talked = session.Interact(ricky);
        Assert.IsNotNull(talked);
        CollectionAssert.IsSubsetOf(new[] { "ObjectiveComplete 2563/0", "QuestEnd 2563" }, talked.Packets.Select(packet => packet.Name).ToList());

        var done = session.EndReply(2563, inventory);
        CollectionAssert.IsSubsetOf(new[] { "QuestComplete 2563", "CompletedQuestCount", "CoinCount" }, done.Select(packet => packet.Name).ToList());
        Assert.AreEqual(coins + Quests.All[2563].RewardCoins, inventory.Coins);
        CollectionAssert.Contains(session.Log.Completed, 2563);
        Assert.IsTrue(saved >= 3);

        Assert.AreEqual("QuestInfo 1801", session.Interact(ricky)?.Packets.Single().Name, "the next quest, which needed this one");
        Assert.IsFalse(Quests.All[2564].IsOfferableFor(session.Log), "the other race's introduction is excluded");
    }

    [TestMethod]
    public void PickupsCountForTheirCollectGoal()
    {
        var quest = Quests.All[3042];
        var log = new QuestLog();
        log.InProgress[3042] = new QuestState { QuestId = 3042 };
        var session = new QuestSession(log, 9, _ => { });

        session.Restore();
        var hammers = session.NodesShown.Where(node => node.Key == "smashing-hammers").ToList();
        Assert.IsTrue(hammers.Count >= quest.Goals[0].Needed);

        for (var i = 0; i < quest.Goals[0].Needed - 1; i++)
            Assert.IsTrue(session.Pickup(hammers[i].Guid)!.Packets.Any(packet => packet.Name.StartsWith("ObjectiveUpdate", StringComparison.Ordinal)));

        var last = session.Pickup(hammers[quest.Goals[0].Needed - 1].Guid);
        Assert.IsTrue(last!.Packets.Any(packet => packet.Name == "ObjectiveComplete 3042/0"));
        Assert.IsTrue(log.InProgress[3042].IsGoalDone(0));
        Assert.IsFalse(session.NodesShown.Any(node => node.Key == "smashing-hammers"), "the rest are taken away");
        Assert.IsNull(session.Pickup(hammers[0].Guid), "already taken");
    }

    [TestMethod]
    public void ReachingAPlaceDoesAReachGoal()
    {
        var quest = Quests.All.Values.First(q => q.Goals[0].Type == QuestGoalDef.Reach);
        var log = new QuestLog();
        log.InProgress[quest.QuestId] = new QuestState { QuestId = quest.QuestId };
        var session = new QuestSession(log, 9, _ => { });
        var at = quest.Goals[0].ReachPosition!;

        Assert.IsNull(session.Moved(at[0] + 500, at[1], at[2]));
        Assert.IsNotNull(session.Moved(at[0] + 1, at[1], at[2]));
        Assert.IsTrue(log.InProgress.TryGetValue(quest.QuestId, out var state) && state.IsGoalDone(0));
    }

    [TestMethod]
    public void TheLogIsKeptPerCharacter()
    {
        var file = Path.Combine(Path.GetTempPath(), $"quests-{Guid.NewGuid():N}.json");

        try
        {
            var log = new QuestLog { ActiveQuestId = 2563, Completed = [3002] };
            log.InProgress[2563] = new QuestState { QuestId = 2563, StepStart = 1, Counts = { [1] = 2 } };

            new QuestStore(file).Save(7, log);
            var read = new QuestStore(file).Load(7);

            Assert.AreEqual(2563, read.ActiveQuestId);
            CollectionAssert.AreEqual(new[] { 3002 }, read.Completed);
            Assert.AreEqual(2, read.InProgress[2563].Counts[1]);
            Assert.AreEqual(0, new QuestStore(file).Load(8).InProgress.Count, "another character");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void NotificationsAreAsLongAsThePs3Reads()
    {
        var icon = new Notification(2, 2, 2, 0, 1, 2, false, 0);
        var add = NpcNotifications.Add([(5UL, icon)]);

        // 0xb06dcc reads 42 bytes per icon after the count.
        Assert.AreEqual(4 + 4 + 34, add.Length);
        Assert.AreEqual(10, BinaryPrimitives.ReadUInt16LittleEndian(add.AsSpan(2)));
        Assert.AreEqual(4 + 4 + 16, NpcNotifications.Remove([(5UL, icon)]).Length);
        Assert.IsTrue(NpcNotifications.Lists.Count > 100);
    }
}
