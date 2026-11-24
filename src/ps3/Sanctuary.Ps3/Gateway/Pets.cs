using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One trick a pet knows, as Sanctuary's PetTrickInfo (the PS3 reads the same 37 bytes with its key).</summary>
public sealed record PetTrick(int Id, int IconId, int NameId, int SkillLevel, int Unknown5, int Unknown6, int TooltipId, int Unknown8, bool Known);

/// <summary>
/// One starter pet (<c>Data/pets.tsv</c>, from Sanctuary's <c>ClientItemDefinitions.json</c>, <c>Pets.json</c> and <c>Models.txt</c>):
/// the pet item, the name it is given, its model, tint and texture, and the tricks and item classes Sanctuary's pet definition has.
/// </summary>
public sealed record PetDef(
    int ItemId,
    string Name,
    int NameId,
    int ImageSetId,
    int ModelId,
    string ModelFile,
    float Scale,
    byte Sex,
    int IconId,
    int TintId,
    string TextureAlias,
    string TintAlias,
    bool MembersOnly,
    bool Vanity,
    IReadOnlyList<PetTrick> Tricks,
    IReadOnlyList<(int Key, int Id, int Unknown)> ItemClasses);

/// <summary>
/// Pets (53): the PS3's Pet handler is <c>0x6949c</c> (opcode 53 in the switch at <c>0x1051f0</c>, through <c>0x59514</c>), a
/// <c>u16</c> 53 and a <c>u8</c> sub-opcode, Sanctuary's numbers. PetList (5) carries each pet as Sanctuary's PetClientInfo
/// (<c>0xa88634</c>) less three fields: one <c>i32</c> between the name and the texture alias (Sanctuary has NameId and TintId there;
/// the tint is sent), and no AttachmentItemGroupId or IsTutorialPet. A summoned pet is an NPC that follows the character with the
/// position update (125) Sanctuary moves NPCs with, in the PS3's 40-byte form. docs/protocol/gateway.md, Pets.
/// </summary>
public static class Pets
{
    public const int Opcode = 53;

    public const int ListSub = 5, SummonRecallSub = 4;

    /// <summary>Sanctuary's DbPet.MaxNeed: a new pet is fed, groomed, played with and happy.</summary>
    public const float MaxNeed = 100f;

    /// <summary>Pet NPC guids: this plus the pet's id.</summary>
    public const ulong GuidBase = 0x0500_0000_0000_0000;

    /// <summary>Sanctuary's PetNpcDisposition.</summary>
    public const int Disposition = 2;

    private static readonly Lazy<IReadOnlyList<PetDef>> Loaded = new(Load);

    /// <summary>The starter pets every character has, in the file's order (pet ids are 1, 2, ...).</summary>
    public static IReadOnlyList<PetDef> Starters => Loaded.Value;

    /// <summary>
    /// Pet 5, PetList: <c>u32</c> count, each pet (its id, then the PetClientInfo as the PS3 reads it), the active pet's id and
    /// guid.
    /// </summary>
    public static byte[] List(IReadOnlyList<PetDef> pets, int activeId, ulong activeGuid)
    {
        var writer = new PacketWriter().U16(Opcode).U8(ListSub).U32((uint)pets.Count);

        for (var index = 0; index < pets.Count; index++)
            Info(writer, index + 1, pets[index]);

        return writer.I32(activeId).U64(activeGuid).ToArray();
    }

    /// <summary>One pet as <c>0xa88634</c> reads it.</summary>
    private static void Info(PacketWriter writer, int id, PetDef pet)
    {
        // The pet window showed no pictures with Sanctuary's pet definition icon (652, 653) here, so the pet item's image set goes in
        // both icon fields.
        writer.I32(id).I32(id).U8(pet.Sex).I32(pet.ImageSetId)
            .F32(MaxNeed).F32(MaxNeed).F32(MaxNeed).F32(MaxNeed)
            .U8(pet.MembersOnly ? 1 : 0)
            .U32((uint)pet.Tricks.Count);

        foreach (var trick in pet.Tricks)
        {
            writer.I32(trick.Id).I32(trick.Id).I32(trick.IconId).I32(trick.NameId).I32(trick.SkillLevel).I32(trick.Unknown5)
                .I32(trick.Unknown6).I32(trick.TooltipId).I32(trick.Unknown8).U8(trick.Known ? 1 : 0);
        }

        writer.U32(0)                                                  // items the pet wears
            .U32((uint)pet.ItemClasses.Count);

        foreach (var (key, classId, unknown) in pet.ItemClasses)
            writer.I32(key).I32(classId).I32(unknown);

        writer.Str(pet.Name).I32(pet.TintId).Str(pet.TextureAlias).I32(pet.ImageSetId).U8(pet.Vanity ? 1 : 0);

        // The four favourite tricks, then Sanctuary's unknown item guid and the seven care items (shampoo to vitamins): none.
        for (var i = 0; i < 4 + 8; i++)
            writer.I32(0);
    }

    /// <summary>Pet 4, PetSummonRecall from the client (<c>Pet.Summon</c> and <c>Pet.Dismiss</c> build it at <c>0x690f8</c>): the pet id and whether to summon.</summary>
    public static (int PetId, bool Summon)? FromSummonRecall(ReadOnlySpan<byte> inner)
        => inner.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == Opcode && inner[2] == SummonRecallSub
            ? (BinaryPrimitives.ReadInt32LittleEndian(inner[3..]), inner[7] != 0)
            : null;

    /// <summary>Whether <paramref name="inner"/> is a Pet (53) packet.</summary>
    public static bool IsPet(ReadOnlySpan<byte> inner) => inner.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == Opcode;

    /// <summary>The pet's NPC standing at the given place, facing <paramref name="heading"/>; it cannot be talked to.</summary>
    public static NpcSpawn Npc(int id, PetDef pet, float x, float y, float z, float heading) => new(
        GuidBase + (ulong)id, 0, pet.ModelId, pet.ModelFile, pet.NameId, pet.Name, pet.Scale, pet.TextureAlias, pet.TintAlias, pet.TintId,
        HideNamePlate: false, SubTextNameId: 0, Disposition, CompositeEffectId: 0, Animation: 1, VerticalOffset: 0, TerrainObjectId: 0,
        Tilted: false, Interactable: false, x, y, z, heading, Pitch: 0, Roll: 0, MovementType: MovementType);

    /// <summary>
    /// AddNpc's last <c>i32</c> (obj+448 of <c>0xb0c010</c>'s record, the actor's +3784): Sanctuary's MovementType. The PS3's
    /// position update (<c>0xcde04</c>) moves an actor only when it is 1, 2 or 4; 2 is Sanctuary's for every NPC that is not a placed object.
    /// </summary>
    public const int MovementType = 2;

    /// <summary>
    /// PlayerUpdate 125, UpdatePosition, as the PS3 reads it from the server (<c>0xcde04</c>, 40 bytes, the same as it sends):
    /// <c>u16</c> 125, <c>u64</c> guid, position and rotation (3 x <c>f32</c> each), state (Sanctuary's 0 moving, 1 stopped), a byte and
    /// the speed.
    /// </summary>
    public static byte[] Position(ulong guid, float x, float y, float z, float heading, bool moving, float speed)
        => new PacketWriter().U16(Placement.Opcode).U64(guid).F32(x).F32(y).F32(z)
            .F32(MathF.Sin(heading)).F32(0).F32(MathF.Cos(heading))
            .U8(moving ? 0 : 1).U8(0).F32(speed).ToArray();

    /// <summary>The speed in a client position update (its last <c>f32</c>), or 0.</summary>
    public static float SpeedOf(ReadOnlySpan<byte> inner)
        => inner.Length >= 40 && BinaryPrimitives.ReadSingleLittleEndian(inner[36..]) is var speed && float.IsFinite(speed) && speed is > 0 and < 100
            ? speed
            : 0;

    private static IReadOnlyList<PetDef> Load()
    {
        using var stream = typeof(Pets).Assembly.GetManifestResourceStream("Sanctuary.Ps3.pets.tsv")
            ?? throw new InvalidOperationException("Sanctuary.Ps3.pets.tsv is not in the assembly.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd()).ToList();
    }

    /// <summary>The pets in the data file's text (see its header).</summary>
    public static IEnumerable<PetDef> Parse(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

            var tricks = c[14].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split(':').Select(I).ToArray())
                .Select(t => new PetTrick(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8] != 0)).ToList();
            var classes = c[15].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split(':').Select(I).ToArray())
                .Select(k => (k[0], k[1], k[2])).ToList();

            yield return new PetDef(I(c[0]), c[1], I(c[2]), I(c[3]), I(c[4]), c[5], float.Parse(c[6], CultureInfo.InvariantCulture),
                byte.Parse(c[7], CultureInfo.InvariantCulture), I(c[8]), I(c[9]), c[10], c[11], c[12] == "1", c[13] == "1", tricks, classes);
        }
    }
}

/// <summary>
/// One character's pets: the starter pets, which one is out and where its NPC is. A summoned pet appears beside the character and
/// keeps to its side, a couple of units behind; when the character jumps far (a warpstone) the pet is put down beside it again.
/// </summary>
public sealed class PetKeeper
{
    /// <summary>How far behind and to the side of the character the pet walks.</summary>
    public const float Behind = 2f, Side = 1.5f;

    /// <summary>A jump further than this is a teleport: the pet is placed again, not walked.</summary>
    public const float TeleportDistance = 40f;

    /// <summary>The pet's goal must move this far before it is sent again (or the character stopped).</summary>
    public const float StepDistance = 0.75f;

    /// <summary>Position updates for the pet are at most this often while it walks.</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The slowest the pet is told to go, so it catches up after the character stopped.</summary>
    public const float MinimumSpeed = 4f;

    private readonly IReadOnlyList<PetDef> _pets;
    private (float X, float Y, float Z, float Heading)? _at;
    private (float X, float Z)? _owner;
    private bool _moving;
    private TimeSpan _sentAt = TimeSpan.MinValue;

    public PetKeeper(IReadOnlyList<PetDef> pets) => _pets = pets;

    /// <summary>The pet that is out (its id, 1-based), or 0.</summary>
    public int Active { get; private set; }

    public ulong ActiveGuid => Active == 0 ? 0 : Pets.GuidBase + (ulong)Active;

    public IReadOnlyList<PetDef> All => _pets;

    /// <summary>The pet window's list.</summary>
    public NamedPacket List() => new($"PetList {_pets.Count}", Pets.List(_pets, Active, ActiveGuid));

    /// <summary>Pet <paramref name="id"/> comes out beside the character (another one out goes away first).</summary>
    public List<NamedPacket> Summon(int id, Placement at)
    {
        if (id < 1 || id > _pets.Count)
            return [];

        var packets = new List<NamedPacket>();

        if (Active != 0)
            packets.Add(new($"RemovePlayer pet {Active}", Npcs.RemoveNpc(ActiveGuid)));

        Active = id;
        packets.Add(Place(at));
        packets.Add(List());

        return packets;
    }

    /// <summary>The pet goes away.</summary>
    public List<NamedPacket> Recall()
    {
        if (Active == 0)
            return [List()];

        var packets = new List<NamedPacket> { new($"RemovePlayer pet {Active}", Npcs.RemoveNpc(ActiveGuid)) };

        Active = 0;
        _at = null;
        packets.Add(List());

        return packets;
    }

    /// <summary>The pet that is out, put down beside the character again (after a zone load).</summary>
    public List<NamedPacket> Respawn(Placement at) => Active == 0 ? [] : [Place(at)];

    /// <summary>The character moved: the pet follows (none when it is not out or its goal hardly moved).</summary>
    public List<NamedPacket> Moved(Placement at, float speed, TimeSpan now)
    {
        if (Active == 0 || _at is not { } pet)
            return [];

        var owner = _owner;
        _owner = ((float)at.X, (float)at.Z);

        if (owner is { } before && Distance(before.X, before.Z, (float)at.X, (float)at.Z) > TeleportDistance)
            return [new($"RemovePlayer pet {Active}", Npcs.RemoveNpc(ActiveGuid)), Place(at)];

        var goal = Goal(at);
        var moving = speed > 0;
        var far = Distance(pet.X, pet.Z, goal.X, goal.Z);

        // Nothing new: the goal hardly moved, or the character walks on and the last update is recent.
        if (moving == _moving && (far < StepDistance || now - _sentAt < UpdateInterval))
            return [];

        _moving = moving;
        _sentAt = now;
        _at = goal;

        // The pet walks to its goal at the character's pace (at least a walk) and stands once it is there.
        return [new("PetPosition", Pets.Position(ActiveGuid, goal.X, goal.Y, goal.Z, goal.Heading, far >= StepDistance, Math.Max(speed, MinimumSpeed)))];
    }

    private NamedPacket Place(Placement at)
    {
        var spot = Goal(at);
        _at = spot;
        _owner = ((float)at.X, (float)at.Z);
        _moving = false;

        return new($"AddNpc pet {Active}", Npcs.AddNpc(Pets.Npc(Active, _pets[Active - 1], spot.X, spot.Y, spot.Z, spot.Heading)));
    }

    /// <summary>Where the pet stands for the character at <paramref name="at"/>: behind it and to its side, facing the same way.</summary>
    private static (float X, float Y, float Z, float Heading) Goal(Placement at)
    {
        var heading = MathF.Atan2((float)at.RotationX, (float)at.RotationZ);
        var (forwardX, forwardZ) = (MathF.Sin(heading), MathF.Cos(heading));
        var (sideX, sideZ) = (forwardZ, -forwardX);

        return ((float)at.X - (forwardX * Behind) + (sideX * Side), (float)at.Y, (float)at.Z - (forwardZ * Behind) + (sideZ * Side), heading);
    }

    private static float Distance(float x1, float z1, float x2, float z2) => MathF.Sqrt(((x1 - x2) * (x1 - x2)) + ((z1 - z2) * (z1 - z2)));
}
