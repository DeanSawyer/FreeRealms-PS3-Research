using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// One character's pets and the one that is out, behaving as Sanctuary's: the pet list, summoning (the pet appears beside its owner)
/// and dismissing, the pet coming back after a zone load, its care while it is out (<see cref="PetCare"/>), the pet window's petting
/// and naming. It returns the packets to send; the gateway sends them and saves <see cref="Snapshot"/> with the character.
/// </summary>
public sealed class PetCompanion
{
    /// <summary>Sanctuary's pet name limit.</summary>
    public const int LongestName = 32;

    /// <summary>How far to the side of its owner a summoned pet appears.</summary>
    public const float SummonSide = 2f;

    /// <summary>
    /// The pets a character without any is given (the PS3 has no pet shop yet): pet items whose models and textures the PS3 packs
    /// have, with names.
    /// </summary>
    public static readonly IReadOnlyList<(int Item, string Name)> Starters =
    [
        (2644, "Shadow"), (2684, "Pugsley"), (24037, "Tigger"), (24052, "Zoey"), (10998, "Dot"), (17852, "Vivi"), (17868, "Chibi"), (17846, "Char"),
    ];

    private readonly List<OwnedPet> _pets;
    private readonly Random _random;
    private TimeSpan _caredAt;
    private float _scratched;
    private int _remembered;

    public PetCompanion(IEnumerable<OwnedPet> pets, Random random)
    {
        _pets = [.. pets.OrderBy(pet => pet.Id)];
        _random = random;
        Follower = new PetFollower();
    }

    /// <summary>
    /// The pets saved with the character, or, when it has none and <paramref name="giveStarters"/>, the <see cref="Starters"/> whose
    /// models <paramref name="hasAsset"/> says the packs have.
    /// </summary>
    public static PetCompanion Load(IReadOnlyList<SavedPet>? saved, bool giveStarters, Func<string, bool> hasAsset, Random random, DateTimeOffset now)
    {
        var pets = (saved ?? []).Select(pet => OwnedPet.FromSaved(pet, random)).OfType<OwnedPet>().ToList();

        if (pets.Count == 0 && giveStarters)
        {
            foreach (var (item, name) in Starters)
            {
                if (PetCatalog.Items.TryGetValue(item, out var petItem) && hasAsset(petItem.ModelFile))
                    pets.Add(OwnedPet.Adopt(pets.Count + 1, petItem, petItem.Tintable ? petItem.IconTint : 0, name, random, now));
            }
        }

        return new PetCompanion(pets, random);
    }

    public IReadOnlyList<OwnedPet> Pets => _pets;

    /// <summary>The pet that is out, or null.</summary>
    public OwnedPet? Out { get; private set; }

    public int ActiveId => Out?.Id ?? 0;

    public ulong ActiveGuid => Out is { } pet ? PetWire.GuidBase + (ulong)pet.Id : 0;

    /// <summary>Whether the pet window is open on the pet: it stands still for it.</summary>
    public bool WindowOpen { get; private set; }

    public PetFollower Follower { get; }

    /// <summary>What to save: every pet as it is now.</summary>
    public IReadOnlyList<SavedPet> Snapshot() => [.. _pets.Select(pet => pet.ToSaved())];

    /// <summary>The pet window's list.</summary>
    public NamedPacket List() => new($"PetList {_pets.Count}", PetWire.List(_pets, ActiveId, ActiveGuid));

    /// <summary>
    /// Pet <paramref name="id"/> comes out beside its owner, to one side picked at random and facing the way its owner faces; one already
    /// out goes away first.
    /// </summary>
    public List<NamedPacket> Summon(int id, Placement owner, TimeSpan now)
    {
        if (_pets.FirstOrDefault(pet => pet.Id == id) is not { } pet)
            return [];

        var packets = Recall();
        var side = _random.NextSingle() < 0.5f ? -1f : 1f;

        Out = pet;
        _remembered = pet.Id;
        _caredAt = now;
        _scratched = 0;
        WindowOpen = false;

        packets.Add(Place(owner, side));
        packets.Add(List());

        return packets;
    }

    /// <summary>
    /// The pet that is out goes away (none: nothing). With <paramref name="remember"/> it comes back with <see cref="Restore"/>, as
    /// when its owner changes zone; dismissed in the pet window it does not.
    /// </summary>
    public List<NamedPacket> Recall(bool remember = false)
    {
        if (!remember)
            _remembered = 0;

        if (Out is not { } pet)
            return [];

        var guid = ActiveGuid;

        Out = null;
        Follower.Stop();

        return [new($"RemovePlayer pet {pet.Id}", Npcs.RemoveNpc(guid)), new("PetActive", PetWire.Active(0, 0))];
    }

    /// <summary>After a zone load: the pet that was out (or was put away to be remembered) comes out again beside its owner.</summary>
    public List<NamedPacket> Restore(Placement owner, TimeSpan now)
    {
        var id = Out?.Id ?? _remembered;

        if (id == 0)
            return [];

        var packets = Recall(remember: true);
        packets.AddRange(Summon(id, owner, now));

        return packets;
    }

    /// <summary>The pet's care, every <see cref="PetCare.Period"/> while it is out: its status when play or mood changed.</summary>
    public List<NamedPacket> Tick(TimeSpan now)
    {
        if (Out is not { } pet || now - _caredAt < PetCare.Period)
            return [];

        var seconds = (float)(now - _caredAt).TotalSeconds;
        var scratched = _scratched;

        _caredAt = now;
        _scratched = 0;

        return PetCare.Advance(pet, seconds, scratched) ? [new("PetStatus", PetWire.Status(pet))] : [];
    }

    /// <summary>The pet window was opened (the pet stops and stands for it) or closed.</summary>
    public List<NamedPacket> SetWindowOpen(bool open)
    {
        WindowOpen = open;

        return open && Out is not null ? Follower.Halt(ActiveGuid) : [];
    }

    /// <summary>Petting in the pet window: play for the next care step.</summary>
    public void Scratch(int movementSquaredSum, int durationMs)
    {
        if (Out is not null)
            _scratched += PetCare.ScratchPlay(movementSquaredSum, durationMs);
    }

    /// <summary>
    /// The player named the pet that is out: a name of 1 to <see cref="LongestName"/> characters, not blank and without control
    /// characters, is taken (its owner sees the new name and the list); another gets the pet's own name back.
    /// </summary>
    public List<NamedPacket> Rename(string requested)
    {
        if (Out is not { } pet)
            return [];

        if (string.IsNullOrWhiteSpace(requested) || requested.Length > LongestName || requested.Any(char.IsControl))
            return [new("PetSetName refused", PetWire.SetName(ActiveGuid, pet.Name))];

        pet.Name = requested;

        return [new("PetSetName", PetWire.SetName(ActiveGuid, pet.Name)), List()];
    }

    /// <summary>The owner moved: the pet follows (unless the pet window holds it).</summary>
    public List<NamedPacket> Moved(Placement owner, bool moving, float speed, TimeSpan now)
        => Out is null || WindowOpen ? [] : Follower.Moved(ActiveGuid, owner, moving, speed, now, place: () => Place(owner, Follower.Side));

    private NamedPacket Place(Placement owner, float side)
    {
        var pet = Out!;
        var heading = PetFollower.HeadingOf(owner);
        var (sideX, sideZ) = (MathF.Cos(heading), -MathF.Sin(heading));
        var (x, z) = ((float)owner.X + (sideX * SummonSide * side), (float)owner.Z + (sideZ * SummonSide * side));

        Follower.PlacedAt(x, (float)owner.Y, z, heading, side, owner);

        return new($"AddNpc pet {pet.Id}", Npcs.AddNpc(PetNpc.Spawn(ActiveGuid, pet, x, (float)owner.Y, z, heading)));
    }
}

/// <summary>A summoned pet as an NPC: AddNpc and the position updates that move it.</summary>
public static class PetNpc
{
    /// <summary>Sanctuary's PetNpcDisposition.</summary>
    public const int Disposition = 2;

    /// <summary>
    /// AddNpc's last <c>i32</c> (obj+448 of <c>0xb0c010</c>'s record, the actor's +3784): Sanctuary's MovementType. The PS3's position
    /// update (<c>0xcde04</c>) moves an actor only when it is 1, 2 or 4; 2 is Sanctuary's for every NPC that is not a placed object.
    /// </summary>
    public const int MovementType = 2;

    /// <summary>The pet's NPC: its name, model, scale, texture and tint; not interactable.</summary>
    public static NpcSpawn Spawn(ulong guid, OwnedPet pet, float x, float y, float z, float heading) => new(
        guid, 0, pet.Item.ModelId, pet.Item.ModelFile, pet.Item.NameId, pet.Name, pet.Item.ModelScale > 0 ? pet.Item.ModelScale : 1f,
        pet.Item.TextureAlias, pet.DrawnTintAlias, pet.ShownTint, HideNamePlate: false, SubTextNameId: 0, Disposition, CompositeEffectId: 0,
        Animation: 1, VerticalOffset: 0, TerrainObjectId: 0, Tilted: false, Interactable: false, x, y, z, heading, Pitch: 0, Roll: 0,
        MovementType: MovementType);

    /// <summary>
    /// PlayerUpdate 125, UpdatePosition, as the PS3 reads it from the server (<c>0xcde04</c>, 40 bytes, the same as it sends):
    /// <c>u16</c> 125, <c>u64</c> guid, position and rotation (3 x <c>f32</c> each), state (Sanctuary's 0 moving, 1 stopped), a byte and
    /// the speed.
    /// </summary>
    public static byte[] Position(ulong guid, float x, float y, float z, float heading, bool moving, float speed)
        => new PacketWriter().U16(Placement.Opcode).U64(guid).F32(x).F32(y).F32(z)
            .F32(MathF.Sin(heading)).F32(0).F32(MathF.Cos(heading))
            .U8(moving ? 0 : 1).U8(0).F32(speed).ToArray();

    /// <summary>
    /// Whether a client position update says the character is moving: its state byte (offset 34) is 0 while it moves and 1 once it
    /// stops. The speed after it is the character's top speed (6 throughout a recorded walk), not how fast it goes.
    /// </summary>
    public static bool IsMoving(ReadOnlySpan<byte> inner) => inner.Length >= 35 && inner[34] == 0;

    /// <summary>The speed in a client position update (its last <c>f32</c>), or 0.</summary>
    public static float SpeedOf(ReadOnlySpan<byte> inner)
        => inner.Length >= 40 && BinaryPrimitives.ReadSingleLittleEndian(inner[36..]) is var speed && float.IsFinite(speed) && speed is > 0 and < 100
            ? speed
            : 0;
}
