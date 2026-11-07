using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One job (profile) from Sanctuary's <c>Resources/Profiles.json</c>, with the fields the PS3 profile entry has room for.</summary>
public readonly record struct Job(
    int Id,
    int NameId,
    int DescriptionId,
    int Type,
    int Icon,
    int AbilityBgImageSet,
    int BadgeImageSet,
    int ButtonImageSet,
    bool MembersOnly);

/// <summary>
/// The jobs a character has, as Sanctuary gives them, like <c>bootstrap/jobs.py</c>. Sanctuary's <c>login.json</c> sets
/// <c>UnlockAllProfiles</c>, so a new character gets every job at level 20, the starter job (Adventurer, id 1) first.
/// </summary>
public static class Jobs
{
    public const int StarterJob = 1;

    /// <summary>Sanctuary's <c>DbProfile</c> level for every job of a new character; the client reads it as the rank.</summary>
    public const int Rank = 20;

    /// <summary>Sanctuary's <c>Resources/Profiles.json</c>, in its order.</summary>
    public static readonly IReadOnlyList<Job> All =
    [
        new(1, 2836, 2837, 1, 1334, 4, 19, 21, false), // Adventurer
        new(2, 3100, 3101, 2, 1342, 3, 10, 1, false), // Ninja
        new(4, 3237, 3238, 3, 1339, 4, 11, 2, false), // Postman
        new(11, 2278, 2279, 2, 1340, 15, 13, 9, false), // Medic
        new(12, 2280, 2281, 2, 1343, 14, 12, 8, false), // Wizard
        new(14, 2470, 2471, 9, 1341, 4, 11, 2, false), // Miner
        new(16, 2614, 2615, 12, 1336, 4, 11, 2, false), // Blacksmith
        new(32, 4664, 4665, 2, 21594, 31, 10, 1, false), // Warrior
        new(35, 4822, 4823, 2, 1335, 7, 32, 33, false), // Archer
        new(43, 2873, 3234, 2, 1337, 4, 10, 1, false), // Brawler
        new(45, 2856, 2857, 10, 156, 4, 11, 2, false), // Chef
        new(48, 2917, 2918, 5, 20725, 4, 46, 7494, false), // Kart Driver
        new(49, 2919, 2920, 7, 8059, 4, 46, 7494, false), // Demo Derby Driver
        new(52, 4650, 2920, 8, 20743, 4, 4842, 21, false), // Soccer Star
        new(120, 2410, 2411, 17, 396, 4, 1783, 1782, false), // Card Duelist
        new(137, 20178, 407842, 21, 20740, 4, 4843, 5885, false), // Fisherman
        new(58, 23268, 2302, 9, 258, 4, 19, 21, false), // Referee
        new(20, 2301, 2302, 9, 258, 4, 19, 21, false), // Enforcer
    ];

    private static readonly Dictionary<int, Job> ById = All.ToDictionary(job => job.Id);

    public static bool TryGet(int id, out Job job) => ById.TryGetValue(id, out job);

    /// <summary>
    /// The jobs to put in the player data, starter first: all of them when <paramref name="wanted"/> is null (<c>FR_JOBS</c> unset or
    /// <c>all</c>), otherwise those ids plus the starter job, which is the active profile.
    /// </summary>
    public static List<Job> Selected(IReadOnlyCollection<int>? wanted) =>
        wanted is null ? [.. All] : All.Where(job => job.Id == StarterJob || wanted.Contains(job.Id)).ToList();
}
