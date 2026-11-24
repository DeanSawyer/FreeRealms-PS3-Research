using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sanctuary.Ps3.Gateway;

/// <summary>Where a character is in one quest it has taken (Sanctuary's QuestProgress): the step it is on and each open goal's progress.</summary>
public sealed class QuestState
{
    public int QuestId { get; set; }

    /// <summary>Unix time the quest was taken.</summary>
    public long Started { get; set; }

    /// <summary>The first goal of the step being done; past the last goal when every goal is done and the quest waits to be handed in.</summary>
    public int StepStart { get; set; }

    /// <summary>Goals of the current step already done.</summary>
    public List<int> Done { get; set; } = [];

    /// <summary>Count so far per open goal (pickups, or talks for a goal that needs several NPCs).</summary>
    public Dictionary<int, int> Counts { get; set; } = [];

    /// <summary>NPCs already talked to per open counted talk goal.</summary>
    public Dictionary<int, List<int>> Talked { get; set; } = [];

    /// <summary>Pickups (node lines) already taken for this quest.</summary>
    public List<int> Taken { get; set; } = [];

    /// <summary>The goals open now: those of the current step not yet done.</summary>
    public IEnumerable<int> OpenGoals(QuestDef quest)
        => Enumerable.Range(StepStart, Math.Max(0, quest.StepEnd(StepStart) - StepStart)).Where(index => !Done.Contains(index));

    public bool AllGoalsDone(QuestDef quest) => StepStart >= quest.Goals.Count;

    /// <summary>The goals done, for the completion fraction.</summary>
    public int GoalsDone(QuestDef quest) => Math.Min(StepStart, quest.Goals.Count) + Done.Count;

    public float Completion(QuestDef quest) => quest.Goals.Count == 0 ? 1 : (float)GoalsDone(quest) / quest.Goals.Count;

    public bool IsGoalDone(int index) => index < StepStart || Done.Contains(index);

    /// <summary>Marks goal <paramref name="index"/> done; true when that finished the step (and the next one, if any, is now open).</summary>
    public bool FinishGoal(QuestDef quest, int index)
    {
        if (!Done.Contains(index))
            Done.Add(index);

        Counts.Remove(index);
        Talked.Remove(index);

        if (OpenGoals(quest).Any())
            return false;

        StepStart = quest.StepEnd(StepStart);
        Done.Clear();

        return true;
    }
}

/// <summary>A character's quests (Sanctuary's QuestLog): those taken and not yet handed in, those done, and the one being tracked.</summary>
public sealed class QuestLog
{
    public Dictionary<int, QuestState> InProgress { get; set; } = [];

    public List<int> Completed { get; set; } = [];

    public int ActiveQuestId { get; set; }

    public bool Has(int questId) => InProgress.ContainsKey(questId) || Completed.Contains(questId);
}

/// <summary>
/// The characters' quest logs, by character guid, in a JSON file next to the characters' database (<c>quests.json</c>), or only in
/// memory. Kept apart from the database so its tables stay as Sanctuary's.
/// </summary>
public sealed class QuestStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string? _file;
    private Dictionary<ulong, QuestLog> _logs = [];

    public QuestStore(string? file = null)
    {
        _file = file;

        if (file is null || !File.Exists(file))
            return;

        try
        {
            _logs = JsonSerializer.Deserialize<Dictionary<ulong, QuestLog>>(File.ReadAllText(file), Json) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Problem = $"{file} could not be read ({exception.Message}); quests start empty.";
        }
    }

    /// <summary>The store for a server whose characters are in <paramref name="characterFile"/> (a SQLite file): <c>quests.json</c> beside it.</summary>
    public static QuestStore Beside(string? characterFile)
        => new(characterFile is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(characterFile)) ?? ".", "quests.json"));

    /// <summary>The last problem reading or writing the file, for the log; null when there was none.</summary>
    public string? Problem { get; private set; }

    /// <summary>A copy of the log of character <paramref name="guid"/> (empty when it has none).</summary>
    public QuestLog Load(ulong guid)
    {
        lock (_gate)
            return _logs.TryGetValue(guid, out var log) ? Copy(log) : new QuestLog();
    }

    /// <summary>Keeps <paramref name="log"/> for character <paramref name="guid"/> and writes the file.</summary>
    public void Save(ulong guid, QuestLog log)
    {
        lock (_gate)
        {
            _logs[guid] = Copy(log);

            if (_file is null)
                return;

            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(_file));

                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllText(_file, JsonSerializer.Serialize(_logs, Json));
                Problem = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Problem = $"{_file} could not be written ({exception.Message})";
            }
        }
    }

    private static QuestLog Copy(QuestLog log) => JsonSerializer.Deserialize<QuestLog>(JsonSerializer.Serialize(log, Json), Json)!;
}
