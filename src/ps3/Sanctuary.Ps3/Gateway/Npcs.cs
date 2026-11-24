using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One NpcRelevance entry: the NPC, whether it has a cursor, which, and the flag after it.</summary>
public readonly record struct RelevanceEntry(ulong Guid, bool HasCursor, byte CursorId, bool Flag = false);

/// <summary>One NPC placed in the world: Sanctuary's definition fields the PS3 AddNpc carries, and where it stands.</summary>
public sealed record NpcSpawn(
    ulong Guid,
    int NpcId,
    int ModelId,
    string ModelFile,
    int NameId,
    string Name,
    float Scale,
    string TextureAlias,
    string TintAlias,
    int TintId,
    bool HideNamePlate,
    int SubTextNameId,
    int Disposition,
    int CompositeEffectId,
    int Animation,
    float VerticalOffset,
    int TerrainObjectId,
    bool Tilted,
    bool Interactable,
    float X,
    float Y,
    float Z,
    float Heading,
    float Pitch,
    float Roll,
    byte CursorId = 0,
    bool HasCursor = false,
    bool RelevanceFlag = false);

/// <summary>
/// The open world's NPCs: Sanctuary's (OSFR) definitions placed where its <c>Scripts/Zone/FabledRealms.lua</c> spawns them
/// (<c>Data/npcs.tsv.gz</c>), and the PlayerUpdate packets that put them into the PS3 client's world and take them out again.
/// There are over 5,000, so a client only gets those near its character (<see cref="NpcRelevance"/>).
/// </summary>
public static class Npcs
{
    /// <summary>NPC guids are this plus the spawn's line number, far from the characters' and the warpstones' guids.</summary>
    public const ulong GuidBase = 0x0200_0000_0000_0000;

    /// <summary>Sanctuary's hostile disposition: only a hostile NPC shows its health bar.</summary>
    private const int HostileDisposition = 0;

    private const string Resource = "Sanctuary.Ps3.npcs.tsv.gz";

    private static readonly Lazy<IReadOnlyList<NpcSpawn>> Loaded = new(Load);

    /// <summary>Every NPC of the open world, in the order Sanctuary spawns them.</summary>
    public static IReadOnlyList<NpcSpawn> All => Loaded.Value;

    private static readonly Lazy<ILookup<int, NpcSpawn>> ByNpcId = new(() => All.ToLookup(npc => npc.NpcId));

    /// <summary>The spawns of NPC <paramref name="npcId"/>.</summary>
    public static IEnumerable<NpcSpawn> ById(int npcId) => ByNpcId.Value[npcId];

    /// <summary>
    /// PlayerUpdate (35) 2, AddNpc, as the PS3 client reads it (<c>0xb0c010</c>, from sub-opcode 2 of PlayerUpdate at <c>0x1115fc</c>).
    /// It follows Sanctuary's PlayerUpdatePacketAddNpc with these differences: two <c>i32</c> before the chat bubble colours, two
    /// strings after the attachments, no stand animation id, no collision flag, no head, hair or model customization, and nothing
    /// after the rider guid but a <c>u8</c>, two <c>u64</c> and an <c>i32</c> (docs/protocol/gateway.md, Warpstones). The model id and
    /// scale are what the client builds the actor from (<c>0x11b63c</c>, <c>0x11b798</c>).
    /// </summary>
    public static byte[] AddNpc(NpcSpawn npc)
    {
        var tilt = npc.Tilted ? (npc.Heading, npc.Pitch, npc.Roll) : (0f, 0f, 0f);

        return new PacketWriter()
            .U16(35).U16(2)
            .U64(npc.Guid)
            .I32(npc.NameId)
            .I32(npc.ModelId)
            .I32(0).I32(0)
            .I32(0x063C67).I32(0xD4E2F0).I32(1)            // chat bubble colours and size
            .F32(npc.Scale)
            .F32(npc.X).F32(npc.Y).F32(npc.Z).F32(1)
            .F32(MathF.Sin(npc.Heading)).F32(0).F32(MathF.Cos(npc.Heading)).F32(0)
            .I32(npc.Animation)
            .U32(0)                                        // attachments
            .Str(string.Empty).Str(string.Empty)
            .I32(npc.Disposition)
            .Str(npc.TextureAlias).Str(npc.TintAlias)
            .I32(npc.TintId)
            .U8(npc.Disposition == HostileDisposition ? 0 : 1) // hide the health bar
            .F32(npc.VerticalOffset)
            .I32(npc.CompositeEffectId)
            .I32(0)                                        // wield type
            .Str(npc.Name)
            .U8(npc.HideNamePlate ? 1 : 0)
            .F32(0).F32(0).F32(0)
            .I32(npc.TerrainObjectId)
            .U8(0)                                         // has attachments
            .F32(0)                                        // speed
            .U8(0)
            .I32(100)                                      // interact range (Sanctuary's default)
            .I32(0).I32(0)
            .U8(0).U8(npc.Tilted ? 1 : 0)
            .I32(npc.SubTextNameId).I32(0).I32(0)          // sub text name id, animation event, temporary appearance
            .U32(0)                                        // effect tags
            .U8(0).I32(0).U8(0).U8(0)
            .I32(0).Str(string.Empty).Str(string.Empty).I32(0).Str(string.Empty) // customization
            .U8(npc.Tilted ? 1 : 0)
            .F32(tilt.Item1).F32(tilt.Item2).F32(tilt.Item3).F32(0)
            .I32(0).I32(0).I32(0)                          // name colour, area definition, image set
            .U8(npc.Interactable ? 1 : 0)
            .U64(0)                                        // rider
            .U8(0).U64(0).U64(0).I32(0)
            .ToArray();
    }

    /// <summary>PlayerUpdate (35) 3, RemovePlayer: <c>u16</c> 0 and the guid (the PS3 reads the same, <c>0x111b38</c>).</summary>
    public static byte[] RemoveNpc(ulong guid) => new PacketWriter().U16(35).U16(3).U16(0).U64(guid).ToArray();

    /// <summary>
    /// PlayerUpdate (35) 12, NpcRelevance: a <c>u32</c> count, then per NPC its <c>u64</c> guid, a <c>u8</c> has-cursor, the
    /// <c>u8</c> cursor id when it has one, and a <c>u8</c> flag. It is what makes an NPC a target the player can press X at, with
    /// the cursor that says what pressing does (Sanctuary's InteractionMenuHelper.GetNpcRelevancePacket; the PS3 reads it the same
    /// way). Only NPCs with a cursor of their own are listed, as Sanctuary does.
    /// </summary>
    public static byte[] Relevance(IEnumerable<NpcSpawn> npcs)
        => RelevanceEntries(npcs.Where(npc => npc.HasCursor || npc.CursorId != 0).Select(npc => new RelevanceEntry(npc.Guid, true, npc.CursorId, npc.RelevanceFlag)));

    /// <summary>NpcRelevance with the given entries (an entry without a cursor takes the NPC's cursor away).</summary>
    public static byte[] RelevanceEntries(IEnumerable<RelevanceEntry> entries)
    {
        var list = entries.ToList();
        var writer = new PacketWriter().U16(35).U16(12).U32((uint)list.Count);

        foreach (var entry in list)
        {
            writer.U64(entry.Guid).U8(entry.HasCursor ? 1 : 0);

            if (entry.HasCursor)
                writer.U8(entry.CursorId);

            writer.U8(entry.Flag ? 1 : 0);
        }

        return writer.ToArray();
    }

    private static IReadOnlyList<NpcSpawn> Load()
    {
        using var stream = typeof(Npcs).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not in the assembly.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return Parse(reader.ReadToEnd()).ToList();
    }

    /// <summary>The spawns in the data file's text (see its header); each gets a guid from its line among the spawns.</summary>
    public static IEnumerable<NpcSpawn> Parse(string text)
    {
        ulong index = 0;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            int Int(int i) => int.Parse(c[i], CultureInfo.InvariantCulture);
            float Float(int i) => float.Parse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture);

            yield return new NpcSpawn(
                GuidBase + index++,
                Int(0), Int(1), c[2], Int(3), c[4], Float(5), c[6], c[7], Int(8),
                c[9] == "1", Int(10), Int(11), Int(12), Int(13), Float(14), Int(15), c[16] == "1", c[17] == "1",
                Float(18), Float(19), Float(20), Float(21), Float(22), Float(23),
                byte.Parse(c[24], CultureInfo.InvariantCulture), c[25] == "1", c[26] == "1");
        }
    }
}

/// <summary>
/// Which NPCs one client has in its world. As the character moves, those that came within <see cref="AddRange"/> are added and
/// those beyond <see cref="RemoveRange"/> taken out (the gap keeps an NPC at the edge from coming and going), at most
/// <see cref="Limit"/> at a time, the nearest first.
/// </summary>
public sealed class NpcRelevance(IReadOnlyList<NpcSpawn> npcs)
{
    public const double AddRange = 160;
    public const double RemoveRange = 200;
    public const int Limit = 150;

    /// <summary>The character moves this far before the NPCs around it are looked at again.</summary>
    public const double Step = 16;

    private readonly HashSet<int> _shown = [];
    private (double X, double Z)? _lastAt;

    public int Shown => _shown.Count;

    /// <summary>The NPCs to add and to remove now that the character stands at (x, z); nothing when it has not moved far enough.</summary>
    public (List<NpcSpawn> Add, List<NpcSpawn> Remove) Update(double x, double z, bool force = false)
    {
        if (!force && _lastAt is { } last && Square(last.X - x, last.Z - z) < Step * Step)
            return ([], []);

        _lastAt = (x, z);

        var remove = new List<NpcSpawn>();

        foreach (var index in _shown.ToList())
        {
            var npc = npcs[index];

            if (Square(npc.X - x, npc.Z - z) > RemoveRange * RemoveRange)
            {
                _shown.Remove(index);
                remove.Add(npc);
            }
        }

        var room = Limit - _shown.Count;
        var add = new List<NpcSpawn>();

        if (room > 0)
        {
            var near = Enumerable.Range(0, npcs.Count)
                .Where(index => !_shown.Contains(index) && Square(npcs[index].X - x, npcs[index].Z - z) <= AddRange * AddRange)
                .OrderBy(index => Square(npcs[index].X - x, npcs[index].Z - z))
                .Take(room);

            foreach (var index in near)
            {
                _shown.Add(index);
                add.Add(npcs[index]);
            }
        }

        return (add, remove);
    }

    private static double Square(double dx, double dz) => (dx * dx) + (dz * dz);
}
