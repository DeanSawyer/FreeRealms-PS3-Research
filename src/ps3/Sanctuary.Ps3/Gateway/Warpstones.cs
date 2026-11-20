using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>A warpstone: its NPC id and name, and where it stands.</summary>
public sealed record Warpstone(int NpcId, int NameId, string Name, float X, float Y, float Z, float Heading)
{
    /// <summary>The guid the warpstone has in the world (not a character's).</summary>
    public ulong Guid => Warpstones.GuidBase + (ulong)NpcId;
}

/// <summary>
/// The warpstones in the towns. The PS3 client has no warpstones of its own in the terrain: the server places them, as Sanctuary
/// does (its <c>Npcs.json</c> entries with <c>OpensAtlas</c>, placed by <c>Scripts/Zone/FabledRealms.lua</c>). Each is an NPC
/// (PlayerUpdate AddNpc, <see cref="AddNpc"/>) with the warpstone model. Pressing X at one sends Command (26) 8, InteractRequest,
/// with its guid (the client's <c>0xceab4</c>), and the server opens the atlas (<see cref="OpenAtlas"/>), where a point of
/// interest can be picked to teleport to (<see cref="PointsOfInterest"/>). Sunstone Valley's two warpstones are left out.
/// </summary>
public static class Warpstones
{
    /// <summary>Warpstone guids are this plus the NPC id, far from the characters' guids.</summary>
    public const ulong GuidBase = 0x0100_0000_0000_0000;

    /// <summary>The warpstone model (Sanctuary's model 280, <c>sg_warpstone_01.adr</c>).</summary>
    public const int ModelId = 280;

    /// <summary>Command (26), InteractRequest (8).</summary>
    public const int CommandOpcode = 26;
    public const int InteractRequestSub = 8;

    /// <summary>Sanctuary's Npcs.json (OpensAtlas) and their places in Scripts/Zone/FabledRealms.lua: id, name id, name, x, y, z, heading.</summary>
    public static readonly IReadOnlyList<Warpstone> All =
    [
        new(2163, 437140, "Shrouded Glade Warpstone", 4.17402363f, 42.0455513f, 1117.74487f, 1.567078f),
        new(2164, 41302, "Seaside Warpstone", -634.359375f, 10.5545654f, -1036.08398f, 1.547676f),
        new(2165, 41301, "Wugachug Warpstone", -53.259697f, 37.7917557f, -442.739716f, 1.873988f),
        new(2166, 41303, "Merry Vale Warpstone", -1180.13965f, -33.6685867f, -438.300171f, 1.639779f),
        new(2254, 20247, "Cobblestone Village Warpstone", -1912.33398f, -38.4770813f, 408.314484f, 1.112677f),
        new(2552, 20832, "Briarwood Warpstone", -799.232666f, -19.4099617f, 1633.10559f, -1.662178f),
        new(2649, 390361, "Bristlewood Warpstone", -540.943176f, -64.6324158f, 2221.55884f, 0.179653f),
        new(2853, 24730, "Briarheart Warpstone", 50.8313141f, -29.6819096f, 2801.12354f, 2.215511f),
        new(2886, 104037, "Highroad Junction Warpstone", -444.593475f, -2.99191213f, 190.414597f, -0.608f),
        new(3422, 41304, "Lavender Coast Warpstone", -2131.12988f, -21.3744545f, -284.786316f, 2.019331f),
        new(4611, 429122, "Greenwood Warpstone", -1587.42883f, -44.3671875f, -547.693665f, -1.951218f),
        new(33167, 20831, "Sanctuary Warpstone", -1418.3218f, -27.002932f, 347.82056f, -0.0622950028f),
        new(33558, 41298, "Lakeshore Warpstone", -721.7169f, 9.560479f, 948.71606f, -1.81613004f),
        new(33602, 20830, "Snowhill Warpstone", 50.92852f, 32.425617f, 370.30267f, 1.21874107f),
        new(36307, 116623, "Stillwater Crossing Warpstone", -1270.01453f, -49.5988922f, 785.111572f, -0.383972555f),
        new(36308, 93509, "Blackspore Warpstone", -2128.67554f, -32.8866158f, 1044.89343f, -1.83259571f),
    ];

    /// <summary>
    /// PlayerUpdate (35) 2, AddNpc, as the PS3 client reads it (<c>0xb0c010</c>, from sub-opcode 2 of PlayerUpdate at <c>0x1115fc</c>).
    /// It follows Sanctuary's PlayerUpdatePacketAddNpc with these differences: two <c>i32</c> before the chat bubble colours, two
    /// strings after the attachments, no stand animation id, one byte fewer before the customization and one after it, and
    /// nothing after the rider guid but a <c>u8</c>, two <c>u64</c> and an <c>i32</c>. The model id and scale are what the client
    /// uses to build it (<c>0x11b63c</c>, <c>0x11b798</c>); only the warpstone's fields are set, the rest are zero.
    /// </summary>
    public static byte[] AddNpc(Warpstone stone)
    {
        var writer = new PacketWriter()
            .U16(35).U16(2)
            .U64(stone.Guid)
            .I32(stone.NameId)
            .I32(ModelId)
            .I32(0).I32(0)
            .I32(0x063C67).I32(0xD4E2F0).I32(1)            // chat bubble colours and size
            .F32(1)                                        // scale
            .F32(stone.X).F32(stone.Y).F32(stone.Z).F32(1)
            .F32(MathF.Sin(stone.Heading)).F32(0).F32(MathF.Cos(stone.Heading)).F32(0)
            .I32(0)                                        // animation
            .U32(0)                                        // attachments
            .Str(string.Empty).Str(string.Empty)
            .I32(0)                                        // disposition
            .Str(string.Empty).Str(string.Empty)           // texture alias, tint alias
            .I32(0)                                        // tint id
            .U8(1)                                         // hide the health bar
            .F32(0)                                        // vertical offset
            .I32(0).I32(0)                                 // composite effect, wield type
            .Str(stone.Name)
            .U8(0)                                         // hide the name plate
            .F32(0).F32(0).F32(0)
            .I32(0)                                        // terrain object
            .U8(0)                                         // has attachments
            .F32(0)                                        // speed
            .U8(0)
            .I32(100)                                      // interact range (Sanctuary's default)
            .I32(0).I32(0)
            .U8(0).U8(0)
            .I32(0).I32(0).I32(0)                          // sub text name id, animation event, temporary appearance
            .U32(0)                                        // effect tags
            .U8(0).I32(0).U8(0).U8(0)
            .I32(0).Str(string.Empty).Str(string.Empty).I32(0).Str(string.Empty) // customization
            .U8(0)                                         // tilt
            .F32(0).F32(0).F32(0).F32(0)
            .I32(0).I32(0).I32(0)                          // name colour, area definition, image set
            .U8(1)                                         // interactable
            .U64(0)                                        // rider
            .U8(0).U64(0).U64(0).I32(0);

        return writer.ToArray();
    }

    /// <summary>The warpstone an InteractRequest (Command 26, sub 8, <c>u64</c> guid) is for, or null.</summary>
    public static Warpstone? FromInteract(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < 12
            || BinaryPrimitives.ReadUInt16LittleEndian(inner) != CommandOpcode
            || BinaryPrimitives.ReadUInt16LittleEndian(inner[2..]) != InteractRequestSub)
        {
            return null;
        }

        var guid = BinaryPrimitives.ReadUInt64LittleEndian(inner[4..]);

        return All.FirstOrDefault(stone => stone.Guid == guid);
    }

    /// <summary>
    /// Ui (47) 7, ExecuteScript: a script name and an <c>i32</c> list (none). The PS3 client passes it on as a UI event
    /// ("Handler:Event", as <c>0x1f282c</c> dispatches them), so <c>Atlas:Show</c> runs atlas.lua's Atlas.Show with the Atlas table
    /// as self, as using a warpstone does in Sanctuary (its OpenAtlasInteraction sends "Atlas.Show").
    /// </summary>
    public static byte[] OpenAtlas() => new PacketWriter().U16(47).U8(7).Str("Atlas:Show").U32(0).ToArray();
}
