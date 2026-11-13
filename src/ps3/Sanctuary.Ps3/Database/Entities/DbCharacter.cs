using System;
using System.Collections.Generic;

namespace Sanctuary.Ps3.Database.Entities;

/// <summary>
/// A character, with the columns of Sanctuary's <c>DbCharacter</c> (Sanctuary.Database/Entities/DbCharacter.cs) that the PS3 server
/// has: the names, the look (asset names and creator choices), the position, the active job (profile) and the coins. The PS3 server's
/// own columns: the zone the position is in, the third rotation, whether the starting coins were given, whether it has entered the world
/// yet, and the creator's request as it came (<see cref="CreateValues"/>), which the game server builds the character from.
/// </summary>
public sealed class DbCharacter
{
    public ulong Id { get; set; }

    public required string FirstName { get; set; }

    public string? LastName { get; set; }

    public int Model { get; set; }

    public required string Head { get; set; }

    public int HeadId { get; set; }

    public required string Hair { get; set; }

    public int HairId { get; set; }

    public string? ModelCustomization { get; set; }

    public int? ModelCustomizationId { get; set; }

    public string? FacePaint { get; set; }

    public int? FacePaintId { get; set; }

    public required string SkinTone { get; set; }

    public int SkinToneId { get; set; }

    public int EyeColor { get; set; }

    public int HairColor { get; set; }

    public double? PositionX { get; set; }

    public double? PositionY { get; set; }

    public double? PositionZ { get; set; }

    public double? RotationX { get; set; }

    public double? RotationZ { get; set; }

    /// <summary>The active job (Sanctuary's profile id).</summary>
    public int ActiveProfileId { get; set; }

    public int Coins { get; set; }

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset? LastLogin { get; set; }

    /// <summary>The zone the position was saved in; null when no position is saved.</summary>
    public string? Zone { get; set; }

    public double? RotationY { get; set; }

    /// <summary>Whether the character has had its starting coins.</summary>
    public bool GotStartingCoins { get; set; }

    /// <summary>Whether the character has entered the world (and has items, jobs and coins saved).</summary>
    public bool HasProgress { get; set; }

    /// <summary>The <c>character/create</c> request's values (never the Session), as a JSON object of names to values.</summary>
    public required string CreateValues { get; set; }

    public ICollection<DbItem> Items { get; set; } = [];

    public ICollection<DbProfile> Profiles { get; set; } = [];

    public ulong UserId { get; set; }

    public DbUser User { get; set; } = null!;
}
