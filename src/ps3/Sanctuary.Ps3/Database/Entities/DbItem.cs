using System;

namespace Sanctuary.Ps3.Database.Entities;

/// <summary>An item a character owns, as Sanctuary's <c>DbItem</c>: its id in the character's inventory, definition, tint and count.</summary>
public sealed class DbItem
{
    public int Id { get; set; }

    public int Tint { get; set; }

    public int Count { get; set; }

    public int Definition { get; set; }

    public DateTimeOffset Created { get; set; }

    public ulong CharacterId { get; set; }

    public DbCharacter Character { get; set; } = null!;
}
