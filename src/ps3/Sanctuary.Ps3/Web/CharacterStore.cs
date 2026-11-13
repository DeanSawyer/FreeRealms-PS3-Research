using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanctuary.Ps3.Web;

/// <summary>
/// A character made in the character creator: the guid the server gave it and the values of its <c>character/create</c> request
/// (FirstName, LastName, Head, ItemChest, ...; never the Session).
/// </summary>
public sealed record SavedCharacter(ulong Guid, IReadOnlyDictionary<string, string> Values, DateTime Created, DateTime? LastPlayed)
{
    public string FirstName => Values.TryGetValue("FirstName", out var first) ? first : string.Empty;

    public string LastName => Values.TryGetValue("LastName", out var last) ? last : string.Empty;

    /// <summary>"First Last", or only the first name when there is no last name.</summary>
    public string Name => string.Join(' ', new[] { FirstName, LastName }.Where(part => part.Length > 0));
}

/// <summary>
/// The account's characters, kept the way Sanctuary keeps them: up to <see cref="MaxCharacters"/>, names unique (ignoring case).
/// <c>character/create</c> adds one, <c>account/info</c> lists them (so the client shows character select instead of the creator),
/// <c>character/login</c> picks the one the game server sends and <c>character/delete</c> removes one. The PS3 client only ever
/// shows the first character of the list and has no button to make another, so the list starts with <see cref="ShownNext"/> (the
/// one made, played or picked in the manager last) and <see cref="CreatorNext"/> sends the next login to the creator instead.
/// With a <see cref="Path"/> the list is saved to that JSON file after every change and read back when the server starts, so
/// characters survive a restart; without one it lives only in memory (tests). Shared between the web front and the gateway.
/// </summary>
public sealed class CharacterStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<SavedCharacter> _characters = [];
    private readonly Func<DateTime> _now;
    private ulong _nextGuid = 1;
    private ulong? _current;
    private ulong? _shownNext;
    private bool _creatorNext;

    /// <param name="path">The JSON file the characters are kept in; null keeps them in memory only.</param>
    /// <param name="now">The clock (tests).</param>
    public CharacterStore(string? path = null, Func<DateTime>? now = null)
    {
        Path = path;
        _now = now ?? (() => DateTime.UtcNow);
        LoadProblem = Load();
    }

    /// <summary>The JSON file the characters are kept in; null when they are only in memory.</summary>
    public string? Path { get; }

    /// <summary>Why the file could not be read when the store was made (it was then set aside and the list started empty); null when it was fine.</summary>
    public string? LoadProblem { get; }

    /// <summary>Why the last save failed; null when it worked (or nothing was saved yet).</summary>
    public string? SaveProblem { get; private set; }

    /// <summary>The number of characters.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _characters.Count;
        }
    }

    /// <summary>Sanctuary's default limit of characters per account.</summary>
    public const int MaxCharacters = 10;

    /// <summary>The character the PS3 shows in character select next time (first in <see cref="Ordered"/>); null when there is none.</summary>
    public ulong? ShownNext
    {
        get
        {
            lock (_gate)
                return Ordered_().FirstOrDefault()?.Guid;
        }
    }

    /// <summary>Whether the next login goes to the character creator although characters are saved (set in the manager).</summary>
    public bool CreatorNext
    {
        get
        {
            lock (_gate)
                return _creatorNext;
        }
    }

    /// <summary>
    /// The characters in the order <c>account/info</c> sends them: the one to show next first (picked in the manager, else the one
    /// made or played last), then the others by when they were last played, newest first.
    /// </summary>
    public IReadOnlyList<SavedCharacter> Ordered()
    {
        lock (_gate)
            return Ordered_();
    }

    /// <summary>Makes <paramref name="guid"/> the character the PS3 shows next and turns <see cref="CreatorNext"/> off. False when there is no such character.</summary>
    public bool ShowNext(ulong guid)
    {
        lock (_gate)
        {
            if (!_characters.Exists(character => character.Guid == guid))
                return false;

            _shownNext = guid;
            _creatorNext = false;
            Save();

            return true;
        }
    }

    /// <summary>Sends the next login to the creator (true) or back to character select (false). Making a character turns it off.</summary>
    public void SetCreatorNext(bool value)
    {
        lock (_gate)
        {
            _creatorNext = value;
            Save();
        }
    }

    /// <summary>Whether a saved character already has this first and last name (ignoring case), as Sanctuary refuses duplicates.</summary>
    public bool IsNameTaken(string? firstName, string? lastName)
    {
        var wanted = FullName(firstName, lastName);

        if (wanted.Length == 0)
            return false;

        lock (_gate)
            return _characters.Exists(character => string.Equals(character.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string FullName(string? firstName, string? lastName) =>
        string.Join(' ', new[] { firstName?.Trim() ?? string.Empty, lastName?.Trim() ?? string.Empty }.Where(part => part.Length > 0));

    private List<SavedCharacter> Ordered_()
    {
        var shown = _shownNext is { } guid ? _characters.Find(character => character.Guid == guid) : null;
        var rest = _characters
            .Where(character => !ReferenceEquals(character, shown))
            .OrderByDescending(character => character.LastPlayed ?? character.Created)
            .ThenByDescending(character => character.Guid);

        var ordered = new List<SavedCharacter>(_characters.Count);

        if (shown is not null)
            ordered.Add(shown);

        ordered.AddRange(rest);

        return ordered;
    }

    /// <summary>Every character, oldest first.</summary>
    public IReadOnlyList<SavedCharacter> All()
    {
        lock (_gate)
            return [.. _characters];
    }

    /// <summary>The character with <paramref name="guid"/>, or null.</summary>
    public SavedCharacter? Find(ulong guid)
    {
        lock (_gate)
            return _characters.Find(character => character.Guid == guid);
    }

    /// <summary>
    /// Adds a character made from <paramref name="values"/> (the first value of each name wins; Session is never kept), makes it the
    /// one the game server sends, saves, and returns its guid.
    /// </summary>
    public ulong Create(IEnumerable<(string Name, string Value)> values)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            if (name != "Session")
                kept.TryAdd(name, value);
        }

        lock (_gate)
        {
            var guid = _nextGuid++;

            _characters.Add(new SavedCharacter(guid, kept, _now(), null));
            _current = guid;
            _shownNext = guid;
            _creatorNext = false;
            Save();

            return guid;
        }
    }

    /// <summary>Adds a character, like <see cref="Create"/> (the tests' way of saying "this is the character").</summary>
    public void Replace(IEnumerable<(string Name, string Value)> values) => Create(values);

    /// <summary>Makes <paramref name="guid"/> the character the game server sends and notes when it was played. False when there is no such character.</summary>
    public bool Select(ulong guid)
    {
        lock (_gate)
        {
            var index = _characters.FindIndex(character => character.Guid == guid);

            if (index < 0)
                return false;

            _characters[index] = _characters[index] with { LastPlayed = _now() };
            _current = guid;
            _shownNext = guid;
            Save();

            return true;
        }
    }

    /// <summary>Removes <paramref name="guid"/>. False when there is no such character.</summary>
    public bool Delete(ulong guid)
    {
        lock (_gate)
        {
            if (_characters.RemoveAll(character => character.Guid == guid) == 0)
                return false;

            if (_current == guid)
                _current = null;

            if (_shownNext == guid)
                _shownNext = null;

            Save();

            return true;
        }
    }

    /// <summary>
    /// The values of the character the game server sends: the one picked at login, else the one the PS3 shows next. Empty when there is none.
    /// </summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            var character = (_current is { } guid ? _characters.Find(entry => entry.Guid == guid) : null) ?? Ordered_().FirstOrDefault();

            return character is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(character.Values, StringComparer.Ordinal);
        }
    }

    private string? Load()
    {
        if (Path is null || !File.Exists(Path))
            return null;

        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(Path), Json) ?? new StoreFile();

            foreach (var entry in file.Characters ?? [])
            {
                if (entry.Guid == 0 || _characters.Exists(character => character.Guid == entry.Guid))
                    continue;

                _characters.Add(new SavedCharacter(
                    entry.Guid,
                    new Dictionary<string, string>(entry.Values ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                    entry.Created,
                    entry.LastPlayed));
            }

            _nextGuid = Math.Max(file.NextGuid, _characters.Count == 0 ? 1 : _characters.Max(character => character.Guid) + 1);
            _shownNext = file.ShowNext is { } shown && _characters.Exists(character => character.Guid == shown) ? shown : null;
            _creatorNext = file.CreatorNext;

            return null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Never write over a file that could not be read: keep it next to the new one.
            var aside = $"{Path}.unreadable-{_now():yyyyMMdd-HHmmss}";

            try
            {
                File.Move(Path, aside);
            }
            catch (Exception moveProblem) when (moveProblem is IOException or UnauthorizedAccessException)
            {
                return $"{Path} could not be read ({exception.Message}) or set aside ({moveProblem.Message}); characters are not saved this time.";
            }

            return $"{Path} could not be read ({exception.Message}); it was renamed to {aside} and the list starts empty.";
        }
    }

    /// <summary>Writes the list (caller holds the lock): to a temporary file first, then over the old one, so a crash never leaves half a file.</summary>
    private void Save()
    {
        if (Path is null || (LoadProblem is not null && File.Exists(Path)))
            return;

        var file = new StoreFile
        {
            NextGuid = _nextGuid,
            ShowNext = _shownNext,
            CreatorNext = _creatorNext,
            Characters = [.. _characters.Select(character => new StoredCharacter
            {
                Guid = character.Guid,
                Name = character.Name,
                Created = character.Created,
                LastPlayed = character.LastPlayed,
                Values = Sorted(character.Values),
            })],
        };

        try
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            var temporary = Path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(file, Json));
            File.Move(temporary, Path, overwrite: true);
            SaveProblem = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The game keeps working; the characters stay in memory until a save succeeds.
            SaveProblem = $"{Path} could not be written: {exception.Message}";
        }
    }

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> values)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
            sorted[name] = value;

        return sorted;
    }

    private sealed class StoreFile
    {
        public ulong NextGuid { get; set; } = 1;

        /// <summary>The character the PS3 shows next.</summary>
        public ulong? ShowNext { get; set; }

        /// <summary>Send the next login to the creator.</summary>
        public bool CreatorNext { get; set; }

        public List<StoredCharacter>? Characters { get; set; } = [];
    }

    private sealed class StoredCharacter
    {
        public ulong Guid { get; set; }

        /// <summary>Only for people reading the file; the names come from <see cref="Values"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        public DateTime Created { get; set; }

        public DateTime? LastPlayed { get; set; }

        public IDictionary<string, string>? Values { get; set; }
    }
}
