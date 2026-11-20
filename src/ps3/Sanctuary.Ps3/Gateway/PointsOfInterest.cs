using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>A point of interest: Sanctuary's PointOfInterestDefinition, without the fields the PS3 client does not read.</summary>
public sealed record PointOfInterest(
    int Id,
    int NameId,
    int LocationId,
    (float X, float Y, float Z) Position,
    (float X, float Y, float Z) SpawnPosition,
    float Heading,
    int IconId,
    int NotificationType,
    int SubNameId,
    int BreadcrumbQuestId,
    int TeleportLocationId,
    string Comment);

/// <summary>
/// The atlas's points of interest and teleporting to them. The server sends every point (PointOfInterestDefinitionReply, opcode 57);
/// the atlas movie's "teleportPlayerToPointOfInterest" event calls <c>Ui.TeleportToPointOfInterest(id)</c> (atlas.lua), which sends
/// ZoneTeleportRequest (opcode 90, <c>i32</c> id; <c>0x15a9ec</c>, only for a point the client knows) and the server answers with
/// ClientUpdate 12, UpdateLocation, as Sanctuary's PacketZoneTeleportRequestHandler does.
/// </summary>
public static class PointsOfInterest
{
    /// <summary>ZoneTeleportRequest.</summary>
    public const int TeleportOpcode = 90;

    /// <summary>PointOfInterestDefinitionReply.</summary>
    public const int DefinitionOpcode = 57;

    // Sanctuary's PointOfInterestNotificationType.Local and the launch pads' icon (CloverPoiSpawnOffset / CloverPoiDropOnTop).
    private const int LocalNotificationType = 6;
    private const int LaunchPadIconId = 28411;
    private const float Clearance = 1.5f;
    private const float DropHeight = 3.0f;

    private const string Resource = "Sanctuary.Ps3.PointsOfInterest.txt";

    private static readonly Lazy<IReadOnlyDictionary<int, PointOfInterest>> Loaded = new(Load);

    /// <summary>Every point, by id.</summary>
    public static IReadOnlyDictionary<int, PointOfInterest> All => Loaded.Value;

    /// <summary>
    /// PointOfInterestDefinitionReply: <c>u16</c> 57, <c>u32</c> payload length, then for each point <c>u8</c> 1 and its fields, and a
    /// closing <c>u8</c> 0. The PS3 client (<c>0x15aa8c</c>, entries read by <c>0xb45da4</c>) reads twelve 4-byte fields per point: id,
    /// name id, location id, position x, y, z, heading, icon id, notification type, sub name id, breadcrumb quest id and teleport
    /// location id (Sanctuary also writes an unknown field after the sub name, the atlas name and a second unknown, which this
    /// build does not read).
    /// </summary>
    public static byte[] DefinitionReply() => DefinitionReply(All.Values.OrderBy(point => point.Id));

    public static byte[] DefinitionReply(IEnumerable<PointOfInterest> points)
    {
        var payload = new PacketWriter();

        foreach (var point in points)
        {
            payload.U8(1)
                .I32(point.Id)
                .I32(point.NameId)
                .I32(point.LocationId)
                .F32(point.Position.X).F32(point.Position.Y).F32(point.Position.Z)
                .F32(point.Heading)
                .I32(point.IconId)
                .I32(point.NotificationType)
                .I32(point.SubNameId)
                .I32(point.BreadcrumbQuestId)
                .I32(point.TeleportLocationId);
        }

        payload.U8(0);

        var bytes = payload.ToArray();

        return new PacketWriter().U16(DefinitionOpcode).U32((uint)bytes.Length).Raw(bytes).ToArray();
    }

    /// <summary>The point a ZoneTeleportRequest asks for, or null when it is not one or names no known point.</summary>
    public static PointOfInterest? FromRequest(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(inner) != TeleportOpcode)
            return null;

        var id = BinaryPrimitives.ReadInt32LittleEndian(inner[2..]);

        return All.GetValueOrDefault(id);
    }

    /// <summary>
    /// Where a teleport to <paramref name="point"/> lands, and the way the character faces: the point's spawn position, but on top of
    /// the point when an activity point (a chess table, a cooking table...) has no spawn spot of its own, as Clover does.
    /// </summary>
    public static Placement Landing(PointOfInterest point)
    {
        var (x, y, z) = point.SpawnPosition;

        if (point.NotificationType == LocalNotificationType && point.IconId != LaunchPadIconId)
        {
            var dx = x - point.Position.X;
            var dz = z - point.Position.Z;

            if (dx * dx + dz * dz < Clearance * Clearance)
                (x, y, z) = (point.Position.X, point.Position.Y + DropHeight, point.Position.Z);
        }

        // Sanctuary's rotation for a heading: (sin, 0, cos, 0).
        return new Placement(x, y, z, MathF.Sin(point.Heading), 0, MathF.Cos(point.Heading));
    }

    /// <summary>ClientUpdate (38) 12, UpdateLocation: position x, y, z, w; rotation x, y, z, w; teleport; an unknown byte (<c>0xf9d68</c>).</summary>
    public static byte[] UpdateLocation(Placement placement) =>
        new PacketWriter().U16(38).U16(12)
            .F32(placement.X).F32(placement.Y).F32(placement.Z).F32(1)
            .F32(placement.RotationX).F32(placement.RotationY).F32(placement.RotationZ).F32(0)
            .U8(1).U8(0)
            .ToArray();

    private static IReadOnlyDictionary<int, PointOfInterest> Load()
    {
        using var stream = typeof(PointsOfInterest).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not in the assembly.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd()).ToDictionary(point => point.Id);
    }

    /// <summary>The points in the data file's text: tab-separated columns (see the file's header), lines starting with # left out.</summary>
    public static IEnumerable<PointOfInterest> Parse(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var columns = line.TrimEnd('\r').Split('\t');
            int Int(int index) => int.Parse(columns[index], CultureInfo.InvariantCulture);
            float Float(int index) => float.Parse(columns[index], CultureInfo.InvariantCulture);

            yield return new PointOfInterest(
                Int(0), Int(1), Int(2),
                (Float(3), Float(4), Float(5)),
                (Float(6), Float(7), Float(8)),
                Float(9), Int(10), Int(11), Int(12), Int(13), Int(14),
                columns.Length > 15 ? columns[15] : string.Empty);
        }
    }
}
