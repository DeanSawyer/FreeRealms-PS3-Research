using System;
using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>What a quest action sends, and the conversation it starts (the NPC's lines when a talk goal is done), if any.</summary>
public sealed record QuestOutcome(List<NamedPacket> Packets, TalkConversation? Conversation = null);

/// <summary>
/// One character's quests while it plays, after Sanctuary's QuestManager: offering a quest at its giver (QuestInfo), taking it
/// (QuestReply, then QuestAdd and its objectives), the goals (talking to NPCs, reaching places, picking things up), handing it in
/// (QuestEnd, then QuestEndReply, QuestComplete and the reward) and the icons and cursors over the NPCs the client has: "!" over a
/// giver with a quest to take, "?" over an NPC a quest waits on, and the NPCs' own icons. Every change is saved through the
/// callback. The gateway passes on what the client sends and sends what comes back.
/// </summary>
public sealed class QuestSession
{
    /// <summary>Sanctuary's quest badge: notification type 2, icon 2 for a quest to take, icon 0 for one waiting.</summary>
    public const int BadgeType = 2;
    private const int AvailableIcon = 2;

    private readonly QuestLog _log;
    private readonly ulong _player;
    private readonly Action<QuestLog> _save;
    private readonly Func<NpcSpawn, bool> _placeable;
    private readonly Func<long> _now;

    private readonly Dictionary<ulong, NpcSpawn> _shown = [];
    private readonly Dictionary<ulong, Notification?> _badges = [];
    private readonly Dictionary<ulong, bool> _questCursor = [];
    private readonly Dictionary<ulong, QuestNode> _nodes = [];

    public QuestSession(QuestLog log, ulong player, Action<QuestLog> save, Func<NpcSpawn, bool>? placeable = null, Func<long>? now = null)
    {
        _log = log;
        _player = player;
        _save = save;
        _placeable = placeable ?? (_ => true);
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Quests no longer in the data (or with no goals) are dropped from the log.
        foreach (var id in _log.InProgress.Keys.Where(id => !Quests.All.ContainsKey(id)).ToList())
            _log.InProgress.Remove(id);
    }

    public QuestLog Log => _log;

    /// <summary>The quest last offered and not yet answered.</summary>
    public int? Offered { get; private set; }

    /// <summary>The pickups the client has now.</summary>
    public IReadOnlyCollection<QuestNode> NodesShown => _nodes.Values;

    /// <summary>At login: the journal (every quest taken, its goals so far), the completed count, and the pickups of open collect goals.</summary>
    public List<NamedPacket> Restore()
    {
        var packets = new List<NamedPacket>();

        foreach (var state in _log.InProgress.Values.OrderBy(state => state.Started))
        {
            var quest = Quests.All[state.QuestId];

            packets.Add(new($"QuestAdd {quest.QuestId}", Quests.Add(quest, state, _player, quest.QuestId == _log.ActiveQuestId, suppressBanner: true)));

            var stepEnd = quest.StepEnd(state.StepStart);

            for (var i = 0; i < Math.Min(stepEnd, quest.Goals.Count); i++)
            {
                packets.Add(new($"ObjectiveAdded {quest.QuestId}/{i}", state.IsGoalDone(i)
                    ? Quests.ObjectiveAdded(quest.QuestId, quest.Goals[i], Quests.StateComplete)
                    : Quests.ObjectiveAdded(quest.QuestId, quest.Goals[i], Quests.StateActive, state.Counts.GetValueOrDefault(i))));
            }

            foreach (var i in state.OpenGoals(quest))
                packets.Add(new($"ObjectiveActivated {quest.QuestId}/{i}", Quests.ObjectiveActivated(quest.QuestId, quest.Goals[i], restored: true)));

            packets.AddRange(ShowNodes(quest, state));
        }

        packets.Add(new("CompletedQuestCount", Quests.CompletedCount(_log.Completed.Count)));

        return packets;
    }

    /// <summary>The NPCs the client got and lost: their icons, and the quest cursor for those with quest business.</summary>
    public List<NamedPacket> NpcsChanged(IReadOnlyList<NpcSpawn> added, IReadOnlyList<NpcSpawn> removed)
    {
        foreach (var npc in removed)
        {
            _shown.Remove(npc.Guid);
            _badges.Remove(npc.Guid);
            _questCursor.Remove(npc.Guid);
        }

        var icons = new List<(ulong, Notification)>();
        var cursors = new List<RelevanceEntry>();

        foreach (var npc in added)
        {
            _shown[npc.Guid] = npc;

            icons.AddRange(NpcNotifications.For(npc).Select(icon => (npc.Guid, icon)));

            var (badge, questCursor) = Wanted(npc);

            _badges[npc.Guid] = badge;
            _questCursor[npc.Guid] = questCursor;

            if (badge is not null)
                icons.Add((npc.Guid, badge));

            if (questCursor)
                cursors.Add(new RelevanceEntry(npc.Guid, true, NpcTalk.QuestCursorId));
        }

        var packets = new List<NamedPacket>();

        if (icons.Count > 0)
            packets.Add(new($"AddNotifications {icons.Count}", NpcNotifications.Add(icons)));

        if (cursors.Count > 0)
            packets.Add(new($"NpcRelevance quest {cursors.Count}", Npcs.RelevanceEntries(cursors)));

        return packets;
    }

    /// <summary>X at <paramref name="npc"/>: hand in, a talk goal, or an offer; null when it has no quest business for this character.</summary>
    public QuestOutcome? Interact(NpcSpawn npc)
    {
        // A quest whose goals are all done is handed in at its turn-in NPC (again, if its window was closed).
        foreach (var state in InProgressInOrder())
        {
            var quest = Quests.All[state.QuestId];

            if (state.AllGoalsDone(quest) && quest.TurnInNpcId == npc.NpcId)
                return new QuestOutcome([new($"QuestEnd {quest.QuestId}", Quests.End(quest, npc.Guid))]);
        }

        foreach (var state in InProgressInOrder())
        {
            var quest = Quests.All[state.QuestId];

            foreach (var index in state.OpenGoals(quest).ToList())
            {
                var goal = quest.Goals[index];

                if (goal.Type != QuestGoalDef.Talk || !goal.TalkTargets(quest).Contains(npc.NpcId))
                    continue;

                if (goal.Needed > 1)
                {
                    var talked = state.Talked.TryGetValue(index, out var list) ? list : state.Talked[index] = [];

                    if (talked.Contains(npc.NpcId))
                        continue;

                    talked.Add(npc.NpcId);
                    state.Counts[index] = talked.Count;

                    if (talked.Count < goal.Needed)
                    {
                        Save();

                        var packets = new List<NamedPacket> { new($"ObjectiveUpdate {quest.QuestId}/{index}", Quests.ObjectiveUpdate(quest.QuestId, goal, talked.Count, state.Completion(quest))) };
                        packets.AddRange(Refresh());

                        return new QuestOutcome(packets, Conversation(npc, goal.ConversationFor(npc.NpcId)));
                    }
                }

                return CompleteGoal(quest, state, index, npc);
            }
        }

        if (Quests.GivenBy(npc.NpcId).FirstOrDefault(quest => quest.IsOfferableFor(_log)) is { } offer)
        {
            Offered = offer.QuestId;

            return new QuestOutcome([new($"QuestInfo {offer.QuestId}", Quests.Info(offer, npc.Guid))]);
        }

        return null;
    }

    /// <summary>The answer to an offer: taken, the quest goes into the journal with its first goals open.</summary>
    public List<NamedPacket> Reply(int questId, bool accepted)
    {
        if (Offered == questId)
            Offered = null;

        if (!accepted || !Quests.All.TryGetValue(questId, out var quest) || !quest.IsOfferableFor(_log))
            return [];

        var state = new QuestState { QuestId = questId, Started = _now(), StepStart = 0 };

        _log.InProgress[questId] = state;
        _log.ActiveQuestId = questId;
        Save();

        var packets = new List<NamedPacket> { new($"QuestAdd {questId}", Quests.Add(quest, state, _player, tracker: true, suppressBanner: false)) };

        packets.AddRange(OpenStep(quest, state));
        packets.Add(new("QuestDialogComplete", Quests.DialogComplete()));
        packets.AddRange(Refresh());

        return packets;
    }

    /// <summary>The turn-in window closed: the quest is done, its reward given (coins and items into <paramref name="inventory"/>).</summary>
    public List<NamedPacket> EndReply(int questId, Inventory? inventory)
    {
        if (!_log.InProgress.TryGetValue(questId, out var state) || !Quests.All.TryGetValue(questId, out var quest) || !state.AllGoalsDone(quest))
            return [];

        _log.InProgress.Remove(questId);

        if (!_log.Completed.Contains(questId))
            _log.Completed.Add(questId);

        if (_log.ActiveQuestId == questId)
            _log.ActiveQuestId = _log.InProgress.Keys.FirstOrDefault();

        Save();

        var packets = new List<NamedPacket>
        {
            new($"QuestComplete {questId}", Quests.Complete(questId)),
            new("CompletedQuestCount", Quests.CompletedCount(_log.Completed.Count)),
        };

        if (inventory is not null)
        {
            if (quest.RewardCoins > 0)
            {
                inventory.GiveCoins(quest.RewardCoins);
                packets.Add(new("CoinCount", CoinStore.CoinCount(inventory.Coins)));
            }

            foreach (var item in quest.RewardItems)
                packets.AddRange(inventory.GiveItem(item));
        }

        // The next quest is offered at once when the same NPC gives it (as Sanctuary's OfferNextQuest); otherwise the dialog camera
        // lets go of the NPC (QuestDialogComplete; without it the camera stayed on the NPC after the reward).
        var giver = _shown.Values.FirstOrDefault(shown => shown.NpcId == quest.TurnInNpcId);

        if (quest.NextQuestId is { } nextId && Quests.All.TryGetValue(nextId, out var next) && next.GiverNpcId == quest.TurnInNpcId
            && next.IsOfferableFor(_log) && giver is not null)
        {
            Offered = next.QuestId;
            NextOffer = new NamedPacket($"QuestInfo {next.QuestId}", Quests.Info(next, giver.Guid));
        }
        else
        {
            packets.Add(new("QuestDialogComplete", Quests.DialogComplete()));
        }

        packets.AddRange(Refresh());

        return packets;
    }

    /// <summary>The next quest's offer, to send a moment after the reward (Sanctuary waits 750 ms); taken once.</summary>
    public NamedPacket? NextOffer { get; private set; }

    /// <summary>Takes <see cref="NextOffer"/>.</summary>
    public NamedPacket? TakeNextOffer()
    {
        var offer = NextOffer;
        NextOffer = null;

        return offer;
    }

    /// <summary>The character moved to (x, y, z): reach goals within their radius are done.</summary>
    public QuestOutcome? Moved(double x, double y, double z)
    {
        foreach (var state in InProgressInOrder())
        {
            var quest = Quests.All[state.QuestId];

            foreach (var index in state.OpenGoals(quest).ToList())
            {
                var goal = quest.Goals[index];

                if (goal.Type != QuestGoalDef.Reach || goal.ReachPosition is not [var gx, _, var gz])
                    continue;

                var radius = goal.ReachRadius > 0 ? goal.ReachRadius : 10;

                if ((x - gx) * (x - gx) + (z - gz) * (z - gz) <= radius * radius)
                    return CompleteGoal(quest, state, index, null);
            }
        }

        return null;
    }

    /// <summary>X at a pickup: counted for its collect goal and taken away; null when <paramref name="guid"/> is not one of this character's pickups.</summary>
    public QuestOutcome? Pickup(ulong guid)
    {
        if (!_nodes.TryGetValue(guid, out var node))
            return null;

        foreach (var state in InProgressInOrder())
        {
            var quest = Quests.All[state.QuestId];

            foreach (var index in state.OpenGoals(quest).ToList())
            {
                var goal = quest.Goals[index];

                if (goal.Type != QuestGoalDef.Collect || goal.CollectNodeType != node.Key || state.Taken.Contains(node.Index))
                    continue;

                state.Taken.Add(node.Index);
                _nodes.Remove(guid);

                var count = state.Counts[index] = state.Counts.GetValueOrDefault(index) + 1;
                var packets = new List<NamedPacket> { new($"RemovePlayer pickup {node.Index}", Npcs.RemoveNpc(guid)) };

                if (count < goal.Needed)
                {
                    Save();
                    packets.Add(new($"ObjectiveUpdate {quest.QuestId}/{index}", Quests.ObjectiveUpdate(quest.QuestId, goal, count, state.Completion(quest))));

                    return new QuestOutcome(packets);
                }

                var done = CompleteGoal(quest, state, index, null);
                packets.AddRange(done.Packets);

                return done with { Packets = packets };
            }
        }

        return null;
    }

    private QuestOutcome CompleteGoal(QuestDef quest, QuestState state, int index, NpcSpawn? npc)
    {
        var goal = quest.Goals[index];
        var stepDone = state.FinishGoal(quest, index);
        var final = state.AllGoalsDone(quest);

        Save();

        var packets = new List<NamedPacket>
        {
            new($"ObjectiveComplete {quest.QuestId}/{index}", Quests.ObjectiveComplete(quest.QuestId, goal, state.Completion(quest), silent: final)),
        };

        if (goal.Type == QuestGoalDef.Collect)
            packets.AddRange(HideNodes(goal.CollectNodeType));

        TalkConversation? conversation = null;

        if (final)
        {
            // The turn-in window, at the NPC the quest ends with (the one just talked to, or the nearest one the client has).
            var turnIn = npc is not null && npc.NpcId == quest.TurnInNpcId
                ? npc.Guid
                : _shown.Values.FirstOrDefault(shown => shown.NpcId == quest.TurnInNpcId)?.Guid ?? 0;

            if (turnIn != 0)
                packets.Add(new($"QuestEnd {quest.QuestId}", Quests.End(quest, turnIn)));
        }
        else
        {
            if (stepDone)
                packets.AddRange(OpenStep(quest, state));

            if (npc is not null && goal.Type == QuestGoalDef.Talk)
                conversation = Conversation(npc, goal.ConversationFor(npc.NpcId));
        }

        packets.AddRange(Refresh());

        return new QuestOutcome(packets, conversation);
    }

    /// <summary>The goals of the step <paramref name="state"/> is now on: added to the journal, made active, and their pickups shown.</summary>
    private List<NamedPacket> OpenStep(QuestDef quest, QuestState state)
    {
        var packets = new List<NamedPacket>();

        foreach (var i in state.OpenGoals(quest))
        {
            packets.Add(new($"ObjectiveAdded {quest.QuestId}/{i}", Quests.ObjectiveAdded(quest.QuestId, quest.Goals[i], Quests.StateActive)));
            packets.Add(new($"ObjectiveActivated {quest.QuestId}/{i}", Quests.ObjectiveActivated(quest.QuestId, quest.Goals[i], restored: false)));
        }

        packets.AddRange(ShowNodes(quest, state));

        return packets;
    }

    /// <summary>The pickups of the open collect goals not yet taken: added as NPCs with their cursor.</summary>
    private List<NamedPacket> ShowNodes(QuestDef quest, QuestState state)
    {
        var keys = state.OpenGoals(quest).Select(i => quest.Goals[i]).Where(goal => goal.Type == QuestGoalDef.Collect).Select(goal => goal.CollectNodeType).ToHashSet();
        var nodes = Quests.Nodes
            .Where(node => keys.Contains(node.Key) && !state.Taken.Contains(node.Index) && !_nodes.ContainsKey(node.Guid) && _placeable(node.AsNpc()))
            .ToList();

        if (nodes.Count == 0)
            return [];

        foreach (var node in nodes)
            _nodes[node.Guid] = node;

        var packets = nodes.Select(node => new NamedPacket($"AddNpc pickup {node.Index} {node.Name}", Npcs.AddNpc(node.AsNpc()))).ToList();
        packets.Add(new($"NpcRelevance pickups {nodes.Count}", Npcs.Relevance(nodes.Select(node => node.AsNpc()))));

        return packets;
    }

    private List<NamedPacket> HideNodes(string? key)
    {
        var gone = _nodes.Values.Where(node => node.Key == key).ToList();

        foreach (var node in gone)
            _nodes.Remove(node.Guid);

        return gone.Select(node => new NamedPacket($"RemovePlayer pickup {node.Index}", Npcs.RemoveNpc(node.Guid))).ToList();
    }

    /// <summary>The icons and cursors that changed on the NPCs the client has, after the quest log changed.</summary>
    private List<NamedPacket> Refresh()
    {
        var removed = new List<(ulong, Notification)>();
        var added = new List<(ulong, Notification)>();
        var cursors = new List<RelevanceEntry>();

        foreach (var npc in _shown.Values)
        {
            var (badge, questCursor) = Wanted(npc);
            var old = _badges.GetValueOrDefault(npc.Guid);

            if (!Equals(old, badge))
            {
                if (old is not null)
                    removed.Add((npc.Guid, old));

                if (badge is not null)
                    added.Add((npc.Guid, badge));

                _badges[npc.Guid] = badge;
            }

            if (_questCursor.GetValueOrDefault(npc.Guid) != questCursor)
            {
                _questCursor[npc.Guid] = questCursor;

                // The quest cursor, or the NPC's own again (none, for most).
                cursors.Add(questCursor
                    ? new RelevanceEntry(npc.Guid, true, NpcTalk.QuestCursorId)
                    : new RelevanceEntry(npc.Guid, npc.HasCursor || npc.CursorId != 0, npc.CursorId, npc.RelevanceFlag));
            }
        }

        var packets = new List<NamedPacket>();

        if (removed.Count > 0)
            packets.Add(new($"RemoveNotifications {removed.Count}", NpcNotifications.Remove(removed)));

        if (added.Count > 0)
            packets.Add(new($"AddNotifications {added.Count}", NpcNotifications.Add(added)));

        if (cursors.Count > 0)
            packets.Add(new($"NpcRelevance quest {cursors.Count}", Npcs.RelevanceEntries(cursors)));

        return packets;
    }

    /// <summary>
    /// The badge <paramref name="npc"/> shows for this character, and whether it gets the quest cursor: "!" when it has a quest to
    /// give, "?" (or its own badge) when a quest waits on it, else its own badge, as Sanctuary's GetQuestBadge.
    /// </summary>
    public (Notification? Badge, bool QuestCursor) Wanted(NpcSpawn npc)
    {
        NpcNotifications.Badges.TryGetValue(npc.NpcId, out var own);
        var hasOwnCursor = npc.HasCursor || npc.CursorId != 0;

        if (Quests.GivenBy(npc.NpcId).FirstOrDefault(quest => quest.IsOfferableFor(_log)) is { } offer)
        {
            return (new Notification(BadgeType, AvailableIcon, offer.NotificationAvailable, 0, npc.NameId, npc.SubTextNameId, false, 0), !hasOwnCursor);
        }

        foreach (var state in _log.InProgress.Values)
        {
            var quest = Quests.All[state.QuestId];

            if (IsWaitingOn(quest, state, npc.NpcId))
                return (own ?? new Notification(BadgeType, 0, quest.NotificationActive, 0, npc.NameId, npc.SubTextNameId, false, 0), !hasOwnCursor);
        }

        return (own, false);
    }

    private static bool IsWaitingOn(QuestDef quest, QuestState state, int npcId)
    {
        if (state.AllGoalsDone(quest))
            return quest.TurnInNpcId == npcId;

        foreach (var index in state.OpenGoals(quest))
        {
            var goal = quest.Goals[index];

            if (goal.Type == QuestGoalDef.Talk && goal.TalkTargets(quest).Contains(npcId)
                && !(state.Talked.TryGetValue(index, out var talked) && talked.Contains(npcId)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tracked quest first, then the rest in the order they were taken.</summary>
    private IEnumerable<QuestState> InProgressInOrder()
        => _log.InProgress.Values.OrderBy(state => state.QuestId == _log.ActiveQuestId ? 0 : 1).ThenBy(state => state.Started).ToList();

    private static TalkConversation? Conversation(NpcSpawn npc, IReadOnlyList<TalkLine> lines) => lines.Count > 0 ? new TalkConversation(npc, lines) : null;

    private void Save() => _save(_log);
}
