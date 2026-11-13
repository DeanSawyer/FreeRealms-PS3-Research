using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Sanctuary.Ps3.Database;

namespace Sanctuary.Ps3.Web;

/// <summary>One item a character owns: its id in the character's inventory, its item definition and its tint.</summary>
public sealed record SavedItem(int Id, int Definition, int Tint, int Count = 1);

/// <summary>One filled equipment slot of a job: the slot number and the id of the item in it.</summary>
public sealed record SavedSlot(int Slot, int Item);

/// <summary>Where a character stood when it was last saved: the zone, the position and the rotation the client reported.</summary>
public sealed record SavedPosition(string Zone, double X, double Y, double Z, double RotationX, double RotationY, double RotationZ);

/// <summary>
/// What a character has gained by playing, like the item, profile, coin and position columns of Sanctuary's <c>DbCharacter</c>: the
/// active job, the items, each job's equipment (slots in the order they were filled), the coins and where it stood. Saved by the game
/// server whenever it changes.
/// </summary>
public sealed record CharacterProgress(
    int ActiveJob,
    int Coins,
    IReadOnlyList<SavedItem> Items,
    IReadOnlyDictionary<int, IReadOnlyList<SavedSlot>> Equipment,
    SavedPosition? Position = null,
    bool GotStartingCoins = false);

/// <summary>
/// A character made in the character creator: the guid the server gave it, the values of its <c>character/create</c> request
/// (FirstName, LastName, Head, ItemChest, ...; never the Session), and its progress once it has entered the world.
/// </summary>
public sealed record SavedCharacter(ulong Guid, IReadOnlyDictionary<string, string> Values, DateTime Created, DateTime? LastPlayed, CharacterProgress? Progress = null)
{
    public string FirstName => Values.TryGetValue("FirstName", out var first) ? first : string.Empty;

    public string LastName => Values.TryGetValue("LastName", out var last) ? last : string.Empty;

    /// <summary>"First Last", or only the first name when there is no last name.</summary>
    public string Name => string.Join(' ', new[] { FirstName, LastName }.Where(part => part.Length > 0));
}

/// <summary>
/// The account's characters, kept the way Sanctuary keeps them: up to <see cref="MaxCharacters"/>, names unique (ignoring case), in a
/// database laid out like Sanctuary's (<see cref="CharacterDatabase"/>: SQLite by default, or MySQL).
/// <c>character/create</c> adds one, <c>account/info</c> lists them (so the client shows character select instead of the creator),
/// <c>character/login</c> picks the one the game server sends and <c>character/delete</c> removes one. The PS3 client only ever
/// shows the first character of the list and has no button to make another, so the list starts with <see cref="ShownNext"/> (the
/// one made, played or picked in the manager last) and <see cref="CreatorNext"/> sends the next login to the creator instead.
/// With a database the list is written to it after every change and read back when the server starts, so characters survive a
/// restart; a <c>characters.json</c> from before the database is imported once. Without one it lives only in memory (tests).
/// Shared between the web front and the gateway.
/// </summary>
public sealed class CharacterStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<SavedCharacter> _characters = [];
    private readonly Func<DateTime> _now;
    private readonly CharacterDatabase? _database;
    private ulong _nextGuid = 1;
    private ulong? _current;
    private ulong? _shownNext;
    private bool _creatorNext;
    private bool _saving = true;

    /// <param name="path">
    /// The SQLite file the characters are kept in (a <c>.json</c> name means the <c>.db</c> file next to it); null keeps them in memory
    /// only. A <c>characters.json</c> next to a new database is imported into it.
    /// </param>
    /// <param name="now">The clock (tests).</param>
    public CharacterStore(string? path = null, Func<DateTime>? now = null)
        : this(path is null ? null : CharacterDatabase.Sqlite(DatabaseFile(path)), now)
    {
    }

    /// <param name="database">The database the characters are kept in; null keeps them in memory only.</param>
    /// <param name="now">The clock (tests).</param>
    public CharacterStore(CharacterDatabase? database, Func<DateTime>? now = null)
    {
        _database = database;
        Path = database?.File;
        _now = now ?? (() => DateTime.UtcNow);
        LoadProblem = Load();
    }

    /// <summary>
    /// The store the server uses: MySQL when <c>FR_MYSQL</c> holds a connection string (and <c>FR_MYSQL_VERSION</c>, if set, the
    /// server's version, as Sanctuary's <c>VersionString</c>), else SQLite in <paramref name="file"/>. Null keeps them in memory only.
    /// </summary>
    public static CharacterStore ForServer(string? file)
    {
        if (file is null)
            return new CharacterStore();

        var mysql = Environment.GetEnvironmentVariable("FR_MYSQL");

        if (!string.IsNullOrWhiteSpace(mysql))
        {
            return new CharacterStore(new CharacterDatabase(new DatabaseOptions
            {
                Provider = DatabaseProvider.MySql,
                ConnectionString = mysql,
                VersionString = Environment.GetEnvironmentVariable("FR_MYSQL_VERSION"),
            }));
        }

        return new CharacterStore(file);
    }

    /// <summary>The SQLite file for <paramref name="path"/>: the path itself, or for a <c>.json</c> name (from before the database) the <c>.db</c> file next to it.</summary>
    public static string DatabaseFile(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.ChangeExtension(path, ".db") : path;

    /// <summary>The SQLite file the characters are kept in; null when they are only in memory or in MySQL.</summary>
    public string? Path { get; }

    /// <summary>Where the characters are kept, for the log; null when only in memory.</summary>
    public string? Location => _database?.Description;

    /// <summary>A note about <c>characters.json</c> being imported when the store was made; null when nothing was imported.</summary>
    public string? Imported { get; private set; }

    /// <summary>Why the database could not be read when the store was made (a SQLite file is then set aside and the list starts empty); null when it was fine.</summary>
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

    /// <summary>The character the game server sends: the one picked at login, else the one the PS3 shows next. Null when there is none.</summary>
    public SavedCharacter? Current()
    {
        lock (_gate)
            return (_current is { } guid ? _characters.Find(entry => entry.Guid == guid) : null) ?? Ordered_().FirstOrDefault();
    }

    /// <summary>Keeps <paramref name="progress"/> for <paramref name="guid"/> and saves. False when there is no such character.</summary>
    public bool SaveProgress(ulong guid, CharacterProgress progress)
    {
        lock (_gate)
        {
            var index = _characters.FindIndex(character => character.Guid == guid);

            if (index < 0)
                return false;

            _characters[index] = _characters[index] with { Progress = progress };
            Save();

            return true;
        }
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
        if (_database is null)
            return null;

        StoredAccount? account;

        try
        {
            account = _database.Load();
        }
        catch (Exception exception) when (IsDatabaseProblem(exception))
        {
            if (Path is null || !File.Exists(Path))
            {
                // MySQL (or a file that cannot be made): keep the characters in memory and never write over what could not be read.
                _saving = false;

                return $"{Location} could not be opened ({exception.Message}); characters are not saved this time.";
            }

            // Never write over a database that could not be read: keep it next to the new one.
            var aside = $"{Path}.unreadable-{_now():yyyyMMdd-HHmmss}";

            try
            {
                File.Move(Path, aside);
            }
            catch (Exception moveProblem) when (moveProblem is IOException or UnauthorizedAccessException)
            {
                _saving = false;

                return $"{Path} could not be read ({exception.Message}) or set aside ({moveProblem.Message}); characters are not saved this time.";
            }

            return $"{Path} could not be read ({exception.Message}); it was renamed to {aside} and the list starts empty.";
        }

        if (account is not null)
        {
            Use(account);

            return null;
        }

        // A new database: bring in the characters.json of before, once.
        return Path is null ? null : Import(System.IO.Path.ChangeExtension(Path, ".json"));
    }

    private void Use(StoredAccount account)
    {
        foreach (var character in account.Characters)
        {
            if (character.Guid != 0 && !_characters.Exists(entry => entry.Guid == character.Guid))
                _characters.Add(character);
        }

        _nextGuid = Math.Max(account.NextGuid, _characters.Count == 0 ? 1 : _characters.Max(character => character.Guid) + 1);
        _shownNext = account.ShowNext is { } shown && _characters.Exists(character => character.Guid == shown) ? shown : null;
        _creatorNext = account.CreatorNext;
    }

    /// <summary>Reads a <c>characters.json</c> (the store's file before the database), saves its characters in the database and renames it.</summary>
    private string? Import(string json)
    {
        if (!File.Exists(json))
            return null;

        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(json), Json) ?? new StoreFile();
            var characters = new List<SavedCharacter>();

            foreach (var entry in file.Characters ?? [])
            {
                if (entry.Guid == 0 || characters.Exists(character => character.Guid == entry.Guid))
                    continue;

                characters.Add(new SavedCharacter(
                    entry.Guid,
                    new Dictionary<string, string>(entry.Values ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                    entry.Created,
                    entry.LastPlayed,
                    entry.Progress?.ToProgress()));
            }

            Use(new StoredAccount(file.NextGuid, file.ShowNext, file.CreatorNext, characters));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"{json} could not be imported ({exception.Message}); it was left as it is and the list starts empty.";
        }

        Save();

        if (SaveProblem is not null)
            return $"{json} was read but could not be saved in the database ({SaveProblem}); it was left as it is.";

        var done = $"{json}.imported";

        try
        {
            File.Move(json, done, overwrite: true);
            Imported = $"{_characters.Count} character(s) imported from {json} (renamed to {done})";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Imported = $"{_characters.Count} character(s) imported from {json}, which could not be renamed ({exception.Message})";
        }

        return null;
    }

    /// <summary>Writes the list to the database (caller holds the lock).</summary>
    private void Save()
    {
        if (_database is null || !_saving)
            return;

        try
        {
            _database.Save(new StoredAccount(_nextGuid, _shownNext, _creatorNext, [.. _characters]));
            SaveProblem = null;
        }
        catch (Exception exception) when (IsDatabaseProblem(exception))
        {
            // The game keeps working; the characters stay in memory until a save succeeds.
            SaveProblem = $"{Location} could not be written: {exception.Message}";
        }
    }

    private static bool IsDatabaseProblem(Exception exception) =>
        exception is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException
            or IOException or UnauthorizedAccessException or ArgumentException;

    /// <summary>The <c>characters.json</c> of before the database, read once to import it.</summary>
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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public StoredProgress? Progress { get; set; }
    }

    private sealed class StoredProgress
    {
        public int ActiveJob { get; set; }

        public int Coins { get; set; }

        public List<SavedItem>? Items { get; set; }

        /// <summary>Job id (as text, JSON keys being text) to its filled slots in order.</summary>
        public Dictionary<string, List<SavedSlot>>? Equipment { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SavedPosition? Position { get; set; }

        /// <summary>Whether the character has had its starting coins (missing in files written before the coin store: no).</summary>
        public bool GotStartingCoins { get; set; }

        public CharacterProgress ToProgress()
        {
            var equipment = new Dictionary<int, IReadOnlyList<SavedSlot>>();

            foreach (var (job, slots) in Equipment ?? new Dictionary<string, List<SavedSlot>>())
            {
                if (int.TryParse(job, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var id))
                    equipment[id] = [.. slots ?? []];
            }

            // A count missing (files written before the coin store) or not positive is one.
            var items = (Items ?? new List<SavedItem>()).Select(item => item.Count > 0 ? item : item with { Count = 1 }).ToList();

            return new CharacterProgress(ActiveJob, Coins, items, equipment, Position, GotStartingCoins);
        }
    }
}
