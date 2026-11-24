using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One line of quest dialogue: its text and the caption of the button that answers it (0 = "You got it!").</summary>
public sealed class QuestLineDef
{
    public int TextId { get; set; }

    public int ResponseTextId { get; set; }
}

/// <summary>An NPC a talk goal is done at, with its own line when it has one.</summary>
public sealed class QuestTargetDef
{
    public int NpcId { get; set; }

    public int DialogueId { get; set; }

    public int ResponseId { get; set; }
}

/// <summary>
/// One goal of a quest (Sanctuary's QuestGoal): type 0 talk to an NPC, 1 reach a place, 2 collect pickups. Goals complete in
/// order; a goal marked <see cref="WithPrevious"/> is open at the same time as the one before it.
/// </summary>
public sealed class QuestGoalDef
{
    public const int Talk = 0, Reach = 1, Collect = 2;

    public int Type { get; set; }

    public int NameId { get; set; }

    public int DescriptionId { get; set; }

    public int DialogueId { get; set; }

    public List<QuestLineDef> Dialogue { get; set; } = [];

    public List<QuestTargetDef> Targets { get; set; } = [];

    public int RequiredCount { get; set; }

    public string? CollectNodeType { get; set; }

    public float[]? ReachPosition { get; set; }

    public float ReachRadius { get; set; }

    public bool WithPrevious { get; set; }

    public bool CompleteAfterDialogue { get; set; }

    /// <summary>How many it takes: talks to different targets for a counted talk goal, pickups for a collect goal, else one.</summary>
    public int Needed => Type == Reach ? 1 : Math.Max(1, RequiredCount);

    /// <summary>Talk goals: the NPCs that count (a goal that names none is done at the quest's turn-in NPC).</summary>
    public IReadOnlyList<int> TalkTargets(QuestDef quest)
    {
        var ids = Targets.Select(target => target.NpcId).Where(id => id != 0).Distinct().ToList();

        return ids.Count > 0 ? ids : [quest.TargetNpcId];
    }

    /// <summary>What <paramref name="npcId"/> says when this goal is done there: the authored dialogue, its own line, or the goal's line.</summary>
    public IReadOnlyList<TalkLine> ConversationFor(int npcId)
    {
        if (Dialogue.Count > 0)
            return Dialogue.Select(line => new TalkLine(line.TextId, line.ResponseTextId)).ToList();

        if (Targets.FirstOrDefault(target => target.NpcId == npcId && target.DialogueId != 0) is { } own)
            return [new TalkLine(own.DialogueId, own.ResponseId)];

        return DialogueId != 0 ? [new TalkLine(DialogueId, 0)] : [];
    }
}

/// <summary>A quest as Sanctuary (OSFR) defines it in <c>Resources/Quests.json</c> (copied to <c>Data/quests.json</c>).</summary>
public sealed class QuestDef
{
    public int QuestId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int TitleId { get; set; }

    public int DescriptionId { get; set; }

    public int GiverDialogueId { get; set; }

    public int ObjectiveDescriptionId { get; set; }

    public int IconId { get; set; }

    public float? CameraDistance { get; set; }

    public List<QuestGoalDef> Goals { get; set; } = [];

    public int GiverNpcId { get; set; }

    public int TargetNpcId { get; set; }

    public int RewardCoins { get; set; }

    public int RewardExperience { get; set; }

    public List<int> RewardItems { get; set; } = [];

    public int? PrerequisiteQuestId { get; set; }

    public int? NextQuestId { get; set; }

    public bool HideObjectiveTarget { get; set; }

    public List<int> ExcludesQuestIds { get; set; } = [];

    /// <summary>The giver's badge while the quest can be taken ("!").</summary>
    public int NotificationAvailable { get; set; } = 2;

    /// <summary>The badge of the NPC the quest waits on ("?").</summary>
    public int NotificationActive { get; set; } = 6;

    /// <summary>The end of the step that starts at goal <paramref name="start"/>: the goals after it marked WithPrevious belong to it.</summary>
    public int StepEnd(int start)
    {
        if (start >= Goals.Count)
            return start;

        var end = start + 1;

        while (end < Goals.Count && Goals[end].WithPrevious)
            end++;

        return end;
    }

    /// <summary>The NPC the last goal ends at, where the quest is handed in.</summary>
    public int TurnInNpcId => Goals.Count > 0 && Goals[^1].Type == QuestGoalDef.Talk ? Goals[^1].TalkTargets(this)[0] : TargetNpcId;

    /// <summary>The line the turn-in window shows: the last line the final goal's NPC says.</summary>
    public int TurnInDialogueId => Goals.Count > 0 && Goals[^1].ConversationFor(TurnInNpcId) is { Count: > 0 } lines ? lines[^1].TextId : 0;

    /// <summary>Whether a player whose log is <paramref name="log"/> can be offered this quest.</summary>
    public bool IsOfferableFor(QuestLog log)
    {
        if (log.Has(QuestId) || ExcludesQuestIds.Any(log.Has))
            return false;

        return PrerequisiteQuestId is not { } prerequisite || log.Completed.Contains(prerequisite);
    }
}

/// <summary>One quest pickup (a collect goal's node): its node type, model and place.</summary>
public sealed record QuestNode(int Index, string Key, int ModelId, string ModelFile, string Name, float Scale, byte CursorId, float X, float Y, float Z, float Heading)
{
    /// <summary>Pickup guids are this plus the node's line, far from the NPCs' and warpstones'.</summary>
    public const ulong GuidBase = 0x0300_0000_0000_0000;

    public ulong Guid => GuidBase + (ulong)Index;

    /// <summary>The pickup as an NPC the client draws.</summary>
    public NpcSpawn AsNpc() => new(
        Guid, 0, ModelId, ModelFile, 0, Name, Scale, string.Empty, string.Empty, 0,
        HideNamePlate: false, SubTextNameId: 0, Disposition: 1, CompositeEffectId: 0, Animation: 1, VerticalOffset: 0, TerrainObjectId: 0,
        Tilted: false, Interactable: true, X, Y, Z, Heading, Pitch: 0, Roll: 0, CursorId: CursorId, HasCursor: true);
}

/// <summary>
/// The quests (Sanctuary's 68, <c>Data/quests.json</c>), their pickups (<c>Data/quest_nodes.tsv</c>) and the Quest (49) packets as the
/// PS3 reads them. The Quest handler is <c>0x76aec</c> (opcode 49 in the switch at <c>0x1051d0</c>, through <c>0x59674</c>): <c>u16</c> 49,
/// a <c>u32</c> sub-opcode (Sanctuary's is an <c>i32</c> too), then the packet; the sub-opcodes are Sanctuary's. The PS3 differs in the
/// reward bundle (no multiplier and two unknowns after Unknown5: <see cref="Reward"/>), in QuestInfo (one <c>u8</c> at the end, not
/// two) and in ObjectiveUpdate (it ends after the percentage). docs/protocol/gateway.md, Quests.
/// </summary>
public static class Quests
{
    public const int Opcode = 49;

    public const int InfoSub = 1, ReplySub = 2, AddSub = 3, CompleteSub = 4, ObjectiveAddedSub = 7, ObjectiveActivatedSub = 8,
        ObjectiveUpdateSub = 9, ObjectiveCompleteSub = 10, CompletedCountSub = 12, EndSub = 13, EndReplySub = 14;

    /// <summary>Sanctuary's QuestDialogue.TalkAnimationId: the NPC talks while it offers or hands in a quest.</summary>
    public const int TalkAnimationId = 3106;

    public const int StateInactive = 0, StateActive = 1, StateComplete = 2;

    // Sanctuary's QuestListRewardBundle: the quest list's reward line shows coins (icon 102, "Coins") or experience (icon 101).
    private const int QuestListRewardUnknown3 = 15, CoinsIconId = 102, CoinsTextId = 6513, ExperienceIconId = 101;

    private static readonly Lazy<IReadOnlyDictionary<int, QuestDef>> LoadedQuests = new(LoadQuests);
    private static readonly Lazy<IReadOnlyList<QuestNode>> LoadedNodes = new(LoadNodes);

    /// <summary>Every quest by id.</summary>
    public static IReadOnlyDictionary<int, QuestDef> All => LoadedQuests.Value;

    /// <summary>Every quest pickup.</summary>
    public static IReadOnlyList<QuestNode> Nodes => LoadedNodes.Value;

    /// <summary>The quests <paramref name="npcId"/> gives, in the file's order.</summary>
    public static IEnumerable<QuestDef> GivenBy(int npcId) => All.Values.Where(quest => quest.GiverNpcId == npcId);

    /// <summary>Whether <paramref name="npcId"/> has anything to do with a quest (gives one or is one's goal or turn-in).</summary>
    public static bool IsQuestNpc(int npcId) => QuestNpcIds.Value.Contains(npcId);

    private static readonly Lazy<HashSet<int>> QuestNpcIds = new(() => All.Values
        .SelectMany(quest => quest.Goals.Where(goal => goal.Type == QuestGoalDef.Talk).SelectMany(goal => goal.TalkTargets(quest)).Append(quest.GiverNpcId).Append(quest.TargetNpcId))
        .ToHashSet());

    /// <summary>
    /// The reward bundle as the PS3 reads it (<c>0xa8d2f4</c>): <c>u8</c> success, coins, experience and four more <c>i32</c>, source and
    /// player guid, icon, name, the entries (none here) and a description. Sanctuary's has a multiplier and two <c>i32</c> after the
    /// fourth unknown that the PS3 does not read.
    /// </summary>
    public static PacketWriter Reward(PacketWriter writer, int coins, int experience, int unknown3 = 0, ulong player = 0, int icon = -1, int name = -1,
        IReadOnlyList<int>? items = null)
    {
        var entries = (items ?? []).Where(Items.Definitions.ContainsKey).ToList();

        writer.U8(0).I32(coins).I32(experience).I32(0).I32(unknown3).I32(0).I32(0).U64(0).U64(player).I32(icon).I32(name).U32((uint)entries.Count);

        // Each item (Sanctuary's RewardBundleEntryItem; with success 0 the item guid after it is not sent): type 1, hidden, icon,
        // icon tint, name, quantity, definition, tint, description, text colour, members only. The PS3 reads the same (38 bytes).
        foreach (var definition in entries)
        {
            var row = Items.Definitions[definition];

            writer.I32(ItemEntryType).U8(0).I32(Items.Int(row, 3)).I32(Items.Int(row, 4)).I32(Items.Int(row, 1)).I32(1).I32(definition).I32(0)
                .Str(string.Empty).I32(0).U8(Items.Int(row, 18) != 0 ? 1 : 0);
        }

        return writer.I32(0);
    }

    /// <summary>Sanctuary's RewardBundleEntryType.Item.</summary>
    private const int ItemEntryType = 1;

    /// <summary>
    /// Reward (50) 1, RewardBundle: the reward celebration after a quest is handed in (Sanctuary's GrantReward sends it): <c>u16</c> 50,
    /// <c>u8</c> 1, then the reward bundle (<see cref="Reward"/>), read by <c>0x287b1c</c> and <c>0xa8d2f4</c>.
    /// </summary>
    public static byte[] Celebration(QuestDef quest, ulong player)
        => Reward(new PacketWriter().U16(RewardOpcode).U8(1), quest.RewardCoins, quest.RewardExperience, player: player, items: quest.RewardItems).ToArray();

    public const int RewardOpcode = 50;

    /// <summary>Quest 1, QuestInfo: the offer window (read by <c>0xa8f354</c>, 101 bytes).</summary>
    public static byte[] Info(QuestDef quest, ulong npcGuid)
    {
        var writer = Header(InfoSub)
            .I32(quest.QuestId).I32(quest.GiverDialogueId).I32(TalkAnimationId).I32(quest.TitleId).I32(quest.DescriptionId).I32(0).U8(0);

        return Reward(writer, quest.RewardCoins, quest.RewardExperience, items: quest.RewardItems).U64(npcGuid).F32(quest.CameraDistance ?? 0).U8(0).ToArray();
    }

    /// <summary>Quest 3, QuestAdd: the quest in the journal and, when <paramref name="tracker"/>, the tracker; its goals follow as ObjectiveAdded.</summary>
    public static byte[] Add(QuestDef quest, QuestState state, ulong player, bool tracker, bool suppressBanner)
    {
        var (icon, name) = quest.RewardCoins > 0 ? (CoinsIconId, CoinsTextId) : quest.RewardExperience > 0 ? (ExperienceIconId, -1) : (-1, -1);

        var writer = Header(AddSub)
            .I32(quest.QuestId).I32(quest.TitleId).I32(quest.DescriptionId).I32(quest.ObjectiveDescriptionId).U8(0)
            .I64(state.Started).I32(0).U8(suppressBanner ? 1 : 0).F32(state.Completion(quest));

        return Reward(writer, quest.RewardCoins, quest.RewardExperience, QuestListRewardUnknown3, player, icon, name)
            .U32(0)                                         // objectives: sent as ObjectiveAdded
            .I32(quest.IconId).U8(0).U8(0).U8(tracker ? 1 : 0)
            .ToArray();
    }

    /// <summary>One objective (Sanctuary's QuestObjectiveInfo, the PS3's reward bundle): id and name are the goal's name id.</summary>
    public static PacketWriter Objective(PacketWriter writer, QuestGoalDef goal, int state, int count)
    {
        writer.I32(goal.NameId).I32(goal.NameId).I32(goal.DescriptionId != 0 ? goal.DescriptionId : goal.NameId).U8(0);

        return Reward(writer, 0, 0).I32(state).I32(state == StateComplete ? goal.Needed : count).I32(goal.RequiredCount).I32(0).U8(0).I32(0);
    }

    /// <summary>Quest 7, ObjectiveAdded (101 bytes).</summary>
    public static byte[] ObjectiveAdded(int questId, QuestGoalDef goal, int state, int count = 0)
        => Objective(Header(ObjectiveAddedSub).I32(questId), goal, state, count).ToArray();

    /// <summary>Quest 8, ObjectiveActivated: quest, goal, the count to reach (0 for a single step), and whether it is a restore.</summary>
    public static byte[] ObjectiveActivated(int questId, QuestGoalDef goal, bool restored)
        => Header(ObjectiveActivatedSub).I32(questId).I32(goal.NameId).I32(restored || goal.RequiredCount > 1 ? goal.RequiredCount : 0).U8(restored ? 1 : 0).ToArray();

    /// <summary>Quest 9, ObjectiveUpdate: quest, goal, count, the quest's completion (the PS3 reads no more).</summary>
    public static byte[] ObjectiveUpdate(int questId, QuestGoalDef goal, int count, float completion)
        => Header(ObjectiveUpdateSub).I32(questId).I32(goal.NameId).I32(count).F32(completion).ToArray();

    /// <summary>Quest 10, ObjectiveComplete: quest, goal, completion, composite effect, silent (the last goal's is: the turn-in follows).</summary>
    public static byte[] ObjectiveComplete(int questId, QuestGoalDef goal, float completion, bool silent)
        => Header(ObjectiveCompleteSub).I32(questId).I32(goal.NameId).F32(completion).I32(0).U8(silent ? 1 : 0).ToArray();

    /// <summary>Quest 4, QuestComplete.</summary>
    public static byte[] Complete(int questId) => Header(CompleteSub).I32(questId).ToArray();

    /// <summary>Quest 12, CompletedQuestCountUpdate.</summary>
    public static byte[] CompletedCount(int count) => Header(CompletedCountSub).I32(count).ToArray();

    /// <summary>Quest 13, QuestEnd: the turn-in window (read to 87 bytes): NPC, quest, its line, animation, reward, camera distance.</summary>
    public static byte[] End(QuestDef quest, ulong npcGuid)
    {
        var writer = Header(EndSub).U64(npcGuid).I32(quest.QuestId).I32(quest.TurnInDialogueId).I32(TalkAnimationId);

        return Reward(writer, quest.RewardCoins, quest.RewardExperience, items: quest.RewardItems).F32(quest.CameraDistance ?? 0).ToArray();
    }

    /// <summary>
    /// Ui (47) 13, ObjectiveTargetUpdate (Sanctuary's Ui 14; the PS3's 13 is read by <c>0xb7bf64</c>, then <c>0x1daf80</c> fills the
    /// <c>BaseClient.ObjectiveTarget</c> datasource the minimap and atlas draw the quest marker from): <c>u8</c> active, <c>u64</c> task
    /// id, <c>i32</c> 2, <c>u64</c> target guid, <c>i32</c> name id, position (4 x <c>f32</c>), <c>i32</c> target type 2; 48 bytes.
    /// </summary>
    public static byte[] ObjectiveTarget(ulong taskId, ulong guid, int nameId, float x, float y, float z)
        => new PacketWriter().U16(47).U8(ObjectiveTargetSub).U8(1).U64(taskId).I32(2).U64(guid).I32(nameId).F32(x).F32(y).F32(z).F32(1).I32(2).ToArray();

    /// <summary>ObjectiveTargetUpdate with nothing to point at: the marker goes away.</summary>
    public static byte[] NoObjectiveTarget() => new PacketWriter().U16(47).U8(ObjectiveTargetSub).U8(0).ToArray();

    public const int ObjectiveTargetSub = 13;

    /// <summary>Guids of reach goals' places (Sanctuary's LocationTarget guids are its own): this plus the goal's name id.</summary>
    public const ulong LocationGuidBase = 0x0400_0000_0000_0000;

    /// <summary>Command (26) 29, QuestDialogComplete: closes the offer's camera after an accept (Sanctuary sends it; the PS3 has the sub-opcode).</summary>
    public static byte[] DialogComplete() => new PacketWriter().U16(26).U16(29).ToArray();

    /// <summary>Quest 2, QuestReply (the client's answer to an offer, built at <c>0x1d3c80</c>): quest id and accepted, or null.</summary>
    public static (int QuestId, bool Accepted)? FromReply(ReadOnlySpan<byte> inner)
        => Sub(inner) == ReplySub && inner.Length >= 11 ? (BinaryPrimitives.ReadInt32LittleEndian(inner[6..]), inner[10] != 0) : null;

    /// <summary>Quest 14, QuestEndReply (the turn-in window closed, <c>0x1d3bb8</c>): the quest id, or null.</summary>
    public static int? FromEndReply(ReadOnlySpan<byte> inner)
        => Sub(inner) == EndReplySub && inner.Length >= 10 ? BinaryPrimitives.ReadInt32LittleEndian(inner[6..]) : null;

    /// <summary>
    /// Ui (47) 11, SelectQuest (Sanctuary's Ui 12; seen from the PS3 as <c>2f 00 0b</c> and an <c>i32</c>): the player picked a quest in
    /// the journal to follow. The quest id, or null.
    /// </summary>
    public static int? FromSelect(ReadOnlySpan<byte> inner)
        => inner.Length >= 7 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == 47 && inner[2] == SelectQuestSub
            ? BinaryPrimitives.ReadInt32LittleEndian(inner[3..]) : null;

    public const int SelectQuestSub = 11;

    private static int Sub(ReadOnlySpan<byte> inner)
        => inner.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == Opcode ? (int)BinaryPrimitives.ReadUInt32LittleEndian(inner[2..]) : -1;

    private static PacketWriter Header(int sub) => new PacketWriter().U16(Opcode).U32((uint)sub);

    private static IReadOnlyDictionary<int, QuestDef> LoadQuests()
    {
        using var stream = typeof(Quests).Assembly.GetManifestResourceStream("Sanctuary.Ps3.quests.json")
            ?? throw new InvalidOperationException("Sanctuary.Ps3.quests.json is not in the assembly.");

        var quests = JsonSerializer.Deserialize<List<QuestDef>>(stream) ?? [];

        return quests.Where(quest => quest.Goals.Count > 0).ToDictionary(quest => quest.QuestId);
    }

    private static IReadOnlyList<QuestNode> LoadNodes()
    {
        using var stream = typeof(Quests).Assembly.GetManifestResourceStream("Sanctuary.Ps3.quest_nodes.tsv")
            ?? throw new InvalidOperationException("Sanctuary.Ps3.quest_nodes.tsv is not in the assembly.");
        using var reader = new StreamReader(stream);

        return ParseNodes(reader.ReadToEnd()).ToList();
    }

    /// <summary>The pickups in the data file's text (see its header).</summary>
    public static IEnumerable<QuestNode> ParseNodes(string text)
    {
        var index = 0;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            float F(int i) => float.Parse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture);

            yield return new QuestNode(index++, c[0], int.Parse(c[1], CultureInfo.InvariantCulture), c[2], c[3], F(4),
                byte.Parse(c[5], CultureInfo.InvariantCulture), F(6), F(7), F(8), F(9));
        }
    }
}
