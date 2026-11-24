using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Sanctuary.Ps3.Gateway;

/// <summary>A path the client asked for: who asks, the kind and id it gives the request, and the two ends.</summary>
public sealed record PathRequest(ulong Guid, int Type, int Id, float FromX, float FromY, float FromZ, float ToX, float ToY, float ToZ);

/// <summary>
/// ClientPath (98): the client asks the server for a path, and walks or draws it. Once a quest is tracked the PS3 asks over and over
/// for a path from the character to the quest marker (sub-opcode 1, ClientPathRequest, type 1: the objective helper's trail), and
/// Take Me There (R2) asks for one too (type 2, built at <c>0x162824</c>); with no answer neither does anything (<c>fr_test_20261124_032434</c>:
/// 40 requests in 17 seconds). Sanctuary answers from the zone's navigation mesh (ClientPathRequestPacketHandler); the PS3 server has
/// none, so it answers with the straight line between the two ends, a point every <see cref="Spacing"/> units, the first raised a
/// unit as Sanctuary raises it. The packets are Sanctuary's: the request is <c>u8</c> 1, <c>u64</c> guid, <c>i32</c> type and id, the
/// two positions (4 x <c>f32</c> each) and an <c>i32</c> (55 bytes, as the PS3 sends it); the reply <c>u8</c> 2, type, id, <c>u32</c> count and
/// the points.
/// </summary>
public static class ClientPath
{
    public const int Opcode = 98;
    public const int RequestSub = 1;
    public const int ReplySub = 2;

    /// <summary>Units between the straight path's points.</summary>
    public const float Spacing = 8;

    /// <summary>The most points a reply has (a longer path gets wider spacing).</summary>
    public const int MaxPoints = 128;

    public static PathRequest? FromRequest(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < 51 || BinaryPrimitives.ReadUInt16LittleEndian(inner) != Opcode || inner[2] != RequestSub)
            return null;

        static float F(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadSingleLittleEndian(data[at..]);

        return new PathRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(inner[3..]),
            BinaryPrimitives.ReadInt32LittleEndian(inner[11..]),
            BinaryPrimitives.ReadInt32LittleEndian(inner[15..]),
            F(inner, 19), F(inner, 23), F(inner, 27), F(inner, 35), F(inner, 39), F(inner, 43));
    }

    /// <summary>The straight path between the request's ends.</summary>
    public static List<(float X, float Y, float Z)> Straight(PathRequest request)
    {
        var (dx, dy, dz) = (request.ToX - request.FromX, request.ToY - request.FromY, request.ToZ - request.FromZ);
        var length = MathF.Sqrt((dx * dx) + (dz * dz));
        var steps = Math.Clamp((int)MathF.Ceiling(length / Spacing), 1, MaxPoints - 1);
        var points = new List<(float, float, float)> { (request.FromX, request.FromY + 1, request.FromZ) };

        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            points.Add((request.FromX + (dx * t), request.FromY + (dy * t), request.FromZ + (dz * t)));
        }

        return points;
    }

    /// <summary>ClientPathReply (98, 2) for <paramref name="request"/> with <paramref name="points"/>.</summary>
    public static byte[] Reply(PathRequest request, IReadOnlyList<(float X, float Y, float Z)> points)
    {
        var writer = new PacketWriter().U16(Opcode).U8(ReplySub).I32(request.Type).I32(request.Id).U32((uint)points.Count);

        foreach (var (x, y, z) in points)
            writer.F32(x).F32(y).F32(z).F32(1);

        return writer.ToArray();
    }
}
