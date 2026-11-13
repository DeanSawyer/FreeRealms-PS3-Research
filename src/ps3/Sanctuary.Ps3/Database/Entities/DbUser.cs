using System;
using System.Collections.Generic;

namespace Sanctuary.Ps3.Database.Entities;

/// <summary>
/// An account, as Sanctuary's <c>DbUser</c> (Sanctuary.Database/Entities/DbUser.cs). The PS3 server has one local account; the last
/// three columns are the PS3 server's own: which character select shows next, whether the next login goes to the creator, and the
/// next character id to hand out (a deleted id is never given again).
/// </summary>
public sealed class DbUser
{
    public ulong Id { get; set; }

    public required string Username { get; set; }

    public required string Password { get; set; }

    public int MaxCharacters { get; set; }

    public bool IsMember { get; set; }

    public bool IsAdmin { get; set; }

    public bool IsMod { get; set; }

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset? LastLogin { get; set; }

    /// <summary>The character the PS3 shows next in character select (picked in the manager), or null for the one played last.</summary>
    public ulong? ShowNextCharacterId { get; set; }

    /// <summary>Whether the next login goes to the character creator although characters are saved.</summary>
    public bool CreatorNext { get; set; }

    /// <summary>The id the next character gets.</summary>
    public ulong NextCharacterId { get; set; } = 1;

    public ICollection<DbCharacter> Characters { get; set; } = [];
}
