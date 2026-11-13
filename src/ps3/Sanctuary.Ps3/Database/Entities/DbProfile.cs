using System.Collections.Generic;

namespace Sanctuary.Ps3.Database.Entities;

/// <summary>A job of a character, as Sanctuary's <c>DbProfile</c> (the id is the job's id), with what it wears.</summary>
public sealed class DbProfile
{
    public int Id { get; set; }

    public int Level { get; set; }

    public int LevelXP { get; set; }

    public ICollection<DbProfileItem> Items { get; set; } = [];

    public ulong CharacterId { get; set; }

    public DbCharacter Character { get; set; } = null!;
}

/// <summary>
/// An item a job wears, in Sanctuary's <c>ProfileItems</c> table. Sanctuary keeps only which items a profile has; the PS3 server also
/// keeps the slot and the order the slots were filled in, which the game server sends them in.
/// </summary>
public sealed class DbProfileItem
{
    public int ProfileId { get; set; }

    public ulong CharacterId { get; set; }

    public int Slot { get; set; }

    public int ItemId { get; set; }

    public int Order { get; set; }

    public DbProfile Profile { get; set; } = null!;
}
