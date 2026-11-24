using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// The Pet (53) packets as the PS3 reads and writes them. Its handler is <c>0x6949c</c> (opcode 53 in the switch at <c>0x1051f0</c>,
/// through <c>0x59514</c>): <c>u16</c> 53, a <c>u8</c> sub-opcode with Sanctuary's numbers, then the packet. docs/protocol/gateway.md, Pets.
/// </summary>
public static class PetWire
{
    public const int Opcode = 53;

    public const int StatusSub = 3, SummonRecallSub = 4, ListSub = 5, RequestNameSub = 6, ActiveSub = 9, SetNameSub = 10,
        TactileSub = 22, UiModeSub = 24;

    /// <summary>Pet NPC guids: this plus the pet's id.</summary>
    public const ulong GuidBase = 0x0500_0000_0000_0000;

    /// <summary>
    /// Pet 5, PetList (<c>0xa88634</c> reads each pet): <c>u32</c> count, each pet's id then its PetClientInfo less the three fields the
    /// PS3 lacks, the active pet's id and guid.
    /// </summary>
    public static byte[] List(IReadOnlyList<OwnedPet> pets, int activeId, ulong activeGuid)
    {
        var writer = Header(ListSub).U32((uint)pets.Count);

        foreach (var pet in pets)
            Info(writer, pet);

        return writer.I32(activeId).U64(activeGuid).ToArray();
    }

    /// <summary>One pet: Sanctuary's PetClientInfo in the PS3's order (one <c>i32</c> where Sanctuary has NameId and TintId: the tint).</summary>
    private static void Info(PacketWriter writer, OwnedPet pet)
    {
        var kind = pet.Kind;

        writer.I32(pet.Id).I32(pet.Id).U8(pet.Sex).I32(kind?.IconId ?? 0)
            .F32(pet.Hunger).F32(pet.Hygiene).F32(pet.Play).F32(pet.Mood)
            .U8(pet.Item.MembersOnly ? 1 : 0)
            .U32((uint)(kind?.Tricks.Count ?? 0));

        foreach (var trick in kind?.Tricks ?? [])
        {
            writer.I32(trick.Id).I32(trick.Id).I32(trick.IconId).I32(trick.NameId).I32(trick.SkillLevel).I32(trick.Unknown5).I32(trick.Unknown6)
                .I32(trick.TooltipId).I32(trick.Unknown8).U8(trick.Known ? 1 : 0);
        }

        writer.U32(0)                                                  // items it wears (pet clothes come later)
            .U32((uint)(kind?.ItemClasses.Count ?? 0));

        foreach (var itemClass in kind?.ItemClasses ?? [])
            writer.I32(itemClass.Key).I32(itemClass.Id).I32(itemClass.Unknown);

        writer.Str(pet.Name).I32(pet.ShownTint).Str(pet.Item.TextureAlias).I32(pet.Item.ImageSetId).U8(pet.Vanity ? 1 : 0);

        foreach (var trick in pet.Favorites)
            writer.I32(trick);

        writer.I32(0);                                                 // Sanctuary's unknown item guid

        foreach (var item in pet.CareItems)
            writer.I32(item);
    }

    /// <summary>Pet 3, PetStatus: the pet's id and its four needs (23 bytes, read in full by the handler).</summary>
    public static byte[] Status(OwnedPet pet) => Header(StatusSub).I32(pet.Id).F32(pet.Hunger).F32(pet.Hygiene).F32(pet.Play).F32(pet.Mood).ToArray();

    /// <summary>Pet 9, PetActive: the pet that is out and its NPC's guid; both 0 when none is (15 bytes).</summary>
    public static byte[] Active(int petId, ulong guid) => Header(ActiveSub).I32(petId).U64(guid).ToArray();

    /// <summary>Pet 10, PetSetName: the pet NPC's guid and its name. The PS3 reads no more (Sanctuary's PC packet ends with an Accepted flag).</summary>
    public static byte[] SetName(ulong guid, string name) => Header(SetNameSub).U64(guid).Str(name).ToArray();

    /// <summary>Pet 4, PetSummonRecall (<c>Pet.Summon</c> and <c>Pet.Dismiss</c>, built at <c>0x690f8</c>): the pet id and whether to summon.</summary>
    public static (int PetId, bool Summon)? FromSummonRecall(ReadOnlySpan<byte> inner)
        => Is(inner, SummonRecallSub, 8) ? (BinaryPrimitives.ReadInt32LittleEndian(inner[3..]), inner[7] != 0) : null;

    /// <summary>Pet 6, PetRequestName: the name the player gave the pet that is out (a string), or null.</summary>
    public static string? FromRequestName(ReadOnlySpan<byte> inner)
    {
        if (!Is(inner, RequestNameSub, 7))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(inner[3..]);

        return length >= 0 && 7 + length <= inner.Length ? Encoding.UTF8.GetString(inner.Slice(7, length)) : null;
    }

    /// <summary>Pet 24, PetUiMode: whether the pet window is open on the pet that is out.</summary>
    public static bool? FromUiMode(ReadOnlySpan<byte> inner) => Is(inner, UiModeSub, 7) ? BinaryPrimitives.ReadInt32LittleEndian(inner[3..]) != 0 : null;

    /// <summary>Pet 22, PetTactileNotify: mode (0 petting, 2 grooming), an unknown, how long the stick moved and how far (squared sum).</summary>
    public static (int Mode, int DurationMs, int MovementSquaredSum)? FromTactile(ReadOnlySpan<byte> inner)
        => Is(inner, TactileSub, 19)
            ? (BinaryPrimitives.ReadInt32LittleEndian(inner[3..]), BinaryPrimitives.ReadInt32LittleEndian(inner[11..]), BinaryPrimitives.ReadInt32LittleEndian(inner[15..]))
            : null;

    /// <summary>Whether <paramref name="inner"/> is a Pet (53) packet.</summary>
    public static bool IsPet(ReadOnlySpan<byte> inner) => inner.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(inner) == Opcode;

    private static bool Is(ReadOnlySpan<byte> inner, int sub, int length) => inner.Length >= length && IsPet(inner) && inner[2] == sub;

    private static PacketWriter Header(int sub) => new PacketWriter().U16(Opcode).U8(sub);
}
