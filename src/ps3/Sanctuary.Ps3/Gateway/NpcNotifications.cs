using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>An icon over an NPC's head (Sanctuary's NotificationInfo, without the guid): its type, icon, state and the rest.</summary>
public sealed record Notification(int Type, int IconId, int IconState, int Unknown, int NameId, int ReferenceId, bool Unknown3, int Unknown2);

/// <summary>
/// The icons over NPCs' heads. Each NPC's own come from Sanctuary's <c>Npcs.json</c> (<c>Data/npc_notifications.tsv</c>): its list, always
/// shown, and its badge (Sanctuary's <c>Notification</c>), shown unless a quest badge takes its place. Quest badges ("!" for a quest to take, "?" for one waiting) are the quest
/// log's (<see cref="QuestSession"/>).
/// <para>PlayerUpdate (35) 10, AddNotifications, as the PS3 reads it (<c>0xb06dcc</c> per entry, 42 bytes): <c>u32</c> count, then per
/// icon <c>u64</c> guid, <c>u8</c> compact (0), then five <c>i32</c> (type, icon, icon state, unknown, name id), a <c>u8</c> and an
/// <c>i32</c>. Sanctuary's PC entry has one <c>i32</c> more (the reference id) and an enabled flag at the end; this build reads neither.
/// PlayerUpdate 11, RemoveNotifications, is Sanctuary's: <c>u32</c> count, per icon guid, icon state and composite effect.</para>
/// </summary>
public static class NpcNotifications
{
    public const int AddSub = 10;
    public const int RemoveSub = 11;

    private static readonly Lazy<(IReadOnlyDictionary<int, IReadOnlyList<Notification>> Lists, IReadOnlyDictionary<int, Notification> Badges)> Loaded = new(Load);

    /// <summary>The icons each NPC always shows, by NPC id.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<Notification>> Lists => Loaded.Value.Lists;

    /// <summary>The NPC's own badge, by NPC id: shown unless a quest can be taken from it (then "!"), and in place of "?" while a quest waits on it.</summary>
    public static IReadOnlyDictionary<int, Notification> Badges => Loaded.Value.Badges;

    /// <summary>The icons <paramref name="npc"/> always shows.</summary>
    public static IReadOnlyList<Notification> For(NpcSpawn npc) => Lists.TryGetValue(npc.NpcId, out var list) ? list : [];

    public static byte[] Add(IEnumerable<(ulong Guid, Notification Icon)> icons)
    {
        var list = icons.ToList();
        var writer = new PacketWriter().U16(35).U16(AddSub).U32((uint)list.Count);

        foreach (var (guid, icon) in list)
        {
            writer.U64(guid).U8(0)
                .I32(icon.Type).I32(icon.IconId).I32(icon.IconState).I32(icon.Unknown).I32(icon.NameId)
                .U8(icon.Unknown3 ? 1 : 0).I32(icon.Unknown2);
        }

        return writer.ToArray();
    }

    public static byte[] Remove(IEnumerable<(ulong Guid, Notification Icon)> icons)
    {
        var list = icons.ToList();
        var writer = new PacketWriter().U16(35).U16(RemoveSub).U32((uint)list.Count);

        foreach (var (guid, icon) in list)
            writer.U64(guid).I32(icon.IconState).I32(icon.Unknown2);

        return writer.ToArray();
    }

    private static (IReadOnlyDictionary<int, IReadOnlyList<Notification>>, IReadOnlyDictionary<int, Notification>) Load()
    {
        using var stream = typeof(NpcNotifications).Assembly.GetManifestResourceStream("Sanctuary.Ps3.npc_notifications.tsv")
            ?? throw new InvalidOperationException("Sanctuary.Ps3.npc_notifications.tsv is not in the assembly.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>The icons in the data file's text (see its header).</summary>
    public static (IReadOnlyDictionary<int, IReadOnlyList<Notification>> Lists, IReadOnlyDictionary<int, Notification> Badges) Parse(string text)
    {
        var lists = new Dictionary<int, List<Notification>>();
        var badges = new Dictionary<int, Notification>();

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            int I(int i) => int.Parse(c[i], CultureInfo.InvariantCulture);

            var npcId = I(0);
            var icon = new Notification(I(2), I(3), I(4), I(5), I(6), I(7), c[8] == "1", I(9));

            if (c[1] == "badge")
                badges[npcId] = icon;
            else
                (lists.TryGetValue(npcId, out var list) ? list : lists[npcId] = []).Add(icon);
        }

        return (lists.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Notification>)pair.Value), badges);
    }
}
