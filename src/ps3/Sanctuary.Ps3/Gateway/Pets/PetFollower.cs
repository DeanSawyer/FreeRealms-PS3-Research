using System;
using System.Collections.Generic;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// Moves a summoned pet after its owner with position updates: it walks to a spot behind its owner and to its side while the owner
/// walks, is told to stop as soon as the owner stops (a pet last told it is moving carries on along its heading), and is put down
/// beside its owner again after a jump (a warpstone). Sanctuary's own following (trail, catching up, wandering) comes with its path
/// finding.
/// </summary>
public sealed class PetFollower
{
    /// <summary>How far behind and to the side of its owner the pet walks (Sanctuary's follow distances).</summary>
    public const float Behind = 2f, Beside = 1.5f;

    /// <summary>A jump further than this is a teleport: the pet is placed again, not walked (Sanctuary's OwnerTeleportDistance).</summary>
    public const float TeleportDistance = 30f;

    /// <summary>The pet's spot must move this far before it is sent again (or its owner stopped).</summary>
    public const float StepDistance = 0.75f;

    /// <summary>The slowest the pet is told to go, so it catches up after its owner stopped.</summary>
    public const float MinimumSpeed = 4f;

    /// <summary>Position updates for the pet are at most this often while it walks.</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);

    private (float X, float Y, float Z, float Heading)? _at;
    private (float X, float Z)? _owner;
    private bool _moving;
    private TimeSpan _sentAt = TimeSpan.MinValue;

    /// <summary>The side of its owner the pet keeps to: -1 left, 1 right.</summary>
    public float Side { get; private set; } = 1f;

    /// <summary>The heading of a placement (its rotation is the facing direction's sine and cosine).</summary>
    public static float HeadingOf(Placement at) => MathF.Atan2((float)at.RotationX, (float)at.RotationZ);

    public void PlacedAt(float x, float y, float z, float heading, float side, Placement owner)
    {
        _at = (x, y, z, heading);
        _owner = ((float)owner.X, (float)owner.Z);
        _moving = false;
        Side = side;
    }

    public void Stop()
    {
        _at = null;
        _owner = null;
        _moving = false;
    }

    /// <summary>The pet stands where it is (the pet window opened on it).</summary>
    public List<NamedPacket> Halt(ulong guid)
    {
        if (_at is not { } pet || !_moving)
            return [];

        _moving = false;

        return [new("PetPosition", PetNpc.Position(guid, pet.X, pet.Y, pet.Z, pet.Heading, moving: false, MinimumSpeed))];
    }

    /// <summary>Its owner moved: the packets that move the pet (none when its spot hardly moved).</summary>
    public List<NamedPacket> Moved(ulong guid, Placement owner, bool moving, float speed, TimeSpan now, Func<NamedPacket> place)
    {
        if (_at is not { } pet)
            return [];

        var before = _owner;
        _owner = ((float)owner.X, (float)owner.Z);

        if (before is { } last && Distance(last.X, last.Z, (float)owner.X, (float)owner.Z) > TeleportDistance)
            return [new("RemovePlayer pet", Npcs.RemoveNpc(guid)), place()];

        // Both standing: the pet stays where it is (beside its owner after a summon).
        if (!moving && !_moving)
            return [];

        var spot = Spot(owner);
        var far = Distance(pet.X, pet.Z, spot.X, spot.Z);

        // Nothing new: the spot hardly moved, or its owner walks on and the last update is recent. A stop is always sent.
        if (moving == _moving && (far < StepDistance || (moving && now - _sentAt < UpdateInterval)))
            return [];

        _moving = moving;
        _sentAt = now;
        _at = spot;

        return [new("PetPosition", PetNpc.Position(guid, spot.X, spot.Y, spot.Z, spot.Heading, moving, Math.Max(speed, MinimumSpeed)))];
    }

    /// <summary>Where the pet walks for its owner at <paramref name="owner"/>: behind it and to its side, facing the same way.</summary>
    private (float X, float Y, float Z, float Heading) Spot(Placement owner)
    {
        var heading = HeadingOf(owner);
        var (forwardX, forwardZ) = (MathF.Sin(heading), MathF.Cos(heading));
        var (sideX, sideZ) = (forwardZ, -forwardX);

        return ((float)owner.X - (forwardX * Behind) + (sideX * Beside * Side), (float)owner.Y,
            (float)owner.Z - (forwardZ * Behind) + (sideZ * Beside * Side), heading);
    }

    private static float Distance(float x1, float z1, float x2, float z2) => MathF.Sqrt(((x1 - x2) * (x1 - x2)) + ((z1 - z2) * (z1 - z2)));
}
