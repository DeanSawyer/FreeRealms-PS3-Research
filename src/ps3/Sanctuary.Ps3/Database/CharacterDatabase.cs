using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sanctuary.Ps3.Database.Entities;
using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Database;

/// <summary>Everything the character store keeps: the next id to hand out, who select shows next, the creator switch and the characters.</summary>
public sealed record StoredAccount(ulong NextGuid, ulong? ShowNext, bool CreatorNext, IReadOnlyList<SavedCharacter> Characters);

/// <summary>
/// The characters' database (SQLite by default, or MySQL, as Sanctuary): <see cref="Load"/> reads the account and its characters,
/// <see cref="Save"/> writes them. The tables are made the first time the database is opened. Each call opens its own context, as
/// Sanctuary's handlers do with their <c>IDbContextFactory</c>.
/// </summary>
public sealed class CharacterDatabase
{
    /// <summary>The name of the PS3 server's one local account.</summary>
    public const string LocalAccount = "ps3";

    private static readonly JsonSerializerOptions Json = new();

    private readonly DbContextOptions<Ps3DatabaseContext> _options;

    public CharacterDatabase(DatabaseOptions options)
    {
        Options = options;

        var builder = new DbContextOptionsBuilder<Ps3DatabaseContext>();

        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(options.ConnectionString);
                break;

            case DatabaseProvider.MySql:
                builder.UseMySql(
                    options.ConnectionString,
                    string.IsNullOrWhiteSpace(options.VersionString) ? ServerVersion.AutoDetect(options.ConnectionString) : ServerVersion.Parse(options.VersionString));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "unknown database provider");
        }

        _options = builder.Options;
    }

    /// <summary>A SQLite database in <paramref name="file"/> (made, with its folder, when it is first opened).</summary>
    public static CharacterDatabase Sqlite(string file) => new(new DatabaseOptions
    {
        Provider = DatabaseProvider.Sqlite,

        // No pooling: the file is closed after each call, so it can be moved or deleted while the server runs.
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString(),
    });

    public DatabaseOptions Options { get; }

    /// <summary>The SQLite file, or null for MySQL.</summary>
    public string? File => Options.Provider == DatabaseProvider.Sqlite ? new SqliteConnectionStringBuilder(Options.ConnectionString).DataSource : null;

    /// <summary>Where the characters are, for the log: the SQLite file, or "MySQL".</summary>
    public string Description => File ?? "MySQL";

    /// <summary>The account and its characters; an empty account (and no row yet) when the database is new. <see cref="StoredAccount"/> is null then.</summary>
    public StoredAccount? Load()
    {
        using var db = Open();

        // SQLite cannot sort by an unsigned id in the query, so the (few) rows are sorted here.
        var user = db.Users.AsNoTracking().AsEnumerable().MinBy(u => u.Id);

        if (user is null)
            return null;

        var characters = db.Characters
            .AsNoTracking()
            .Include(c => c.Items)
            .Include(c => c.Profiles)
            .ThenInclude(p => p.Items)
            .Where(c => c.UserId == user.Id)
            .AsSplitQuery()
            .AsEnumerable()
            .OrderBy(c => c.Id)
            .ToList();

        return new StoredAccount(user.NextCharacterId, user.ShowNextCharacterId, user.CreatorNext, [.. characters.Select(ToSaved)]);
    }

    /// <summary>Writes the account and its characters in one transaction: what is in the database afterwards is exactly <paramref name="account"/>.</summary>
    public void Save(StoredAccount account)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();

        db.ProfileItems.ExecuteDelete();
        db.Profiles.ExecuteDelete();
        db.Items.ExecuteDelete();
        db.Characters.ExecuteDelete();

        var user = db.Users.AsEnumerable().MinBy(u => u.Id);

        if (user is null)
        {
            user = new DbUser
            {
                Username = LocalAccount,
                Password = string.Empty,
                MaxCharacters = CharacterStore.MaxCharacters,
                IsMember = true,
                Created = DateTimeOffset.UtcNow,
            };

            db.Users.Add(user);
        }

        user.NextCharacterId = account.NextGuid;
        user.ShowNextCharacterId = account.ShowNext;
        user.CreatorNext = account.CreatorNext;
        user.LastLogin = account.Characters.Select(c => c.LastPlayed).Max() is { } played ? Utc(played) : user.LastLogin;

        foreach (var character in account.Characters)
            db.Characters.Add(ToEntity(character, user));

        db.SaveChanges();
        transaction.Commit();
    }

    private Ps3DatabaseContext Open()
    {
        if (File is { Length: > 0 } file && file != ":memory:")
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file));

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
        }

        var db = new Ps3DatabaseContext(_options);

        db.Database.EnsureCreated();

        return db;
    }

    private static DbCharacter ToEntity(SavedCharacter character, DbUser user)
    {
        var values = character.Values;
        var lastName = character.LastName.Trim();
        var customization = CharacterMappings.ModelCustomization(values);

        var entity = new DbCharacter
        {
            Id = character.Guid,
            FirstName = character.FirstName,
            LastName = lastName.Length > 0 ? lastName : null,
            Model = CharacterMappings.Number(values, "Model", 0),
            Head = CharacterMappings.Head(values),
            HeadId = CharacterMappings.Number(values, "Head", 1),
            Hair = CharacterMappings.Hair(values),
            HairId = CharacterMappings.Number(values, "Hair", 0),
            ModelCustomization = customization.Length > 0 ? customization : null,
            ModelCustomizationId = CharacterMappings.Number(values, "ModelCustomization", 0),
            FacePaint = CharacterMappings.FacePaint(values),
            FacePaintId = CharacterMappings.Number(values, "FacePaint", 0),
            SkinTone = CharacterMappings.SkinTone(values),
            SkinToneId = CharacterMappings.Number(values, "SkinTone", 1),
            EyeColor = CharacterMappings.Number(values, "EyeColor", 0),
            HairColor = CharacterMappings.Number(values, "HairColor", 0),
            Created = Utc(character.Created),
            LastLogin = character.LastPlayed is { } played ? Utc(played) : null,
            CreateValues = JsonSerializer.Serialize(new SortedDictionary<string, string>(values.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal), Json),
            User = user,
        };

        if (character.Progress is not { } progress)
            return entity;

        entity.HasProgress = true;
        entity.ActiveProfileId = progress.ActiveJob;
        entity.Coins = progress.Coins;
        entity.GotStartingCoins = progress.GotStartingCoins;

        if (progress.Position is { } position)
        {
            entity.Zone = position.Zone;
            entity.PositionX = position.X;
            entity.PositionY = position.Y;
            entity.PositionZ = position.Z;
            entity.RotationX = position.RotationX;
            entity.RotationY = position.RotationY;
            entity.RotationZ = position.RotationZ;
        }

        foreach (var item in progress.Items.DistinctBy(item => item.Id))
        {
            entity.Items.Add(new DbItem
            {
                Id = item.Id,
                Definition = item.Definition,
                Tint = item.Tint,
                Count = item.Count > 0 ? item.Count : 1,
                Created = entity.Created,
            });
        }

        foreach (var (job, slots) in progress.Equipment)
        {
            var profile = new DbProfile { Id = job, Level = 1 };
            var order = 0;

            foreach (var slot in slots.DistinctBy(slot => slot.Slot))
                profile.Items.Add(new DbProfileItem { ProfileId = job, CharacterId = character.Guid, Slot = slot.Slot, ItemId = slot.Item, Order = order++ });

            entity.Profiles.Add(profile);
        }

        return entity;
    }

    private static SavedCharacter ToSaved(DbCharacter entity)
    {
        Dictionary<string, string>? stored = null;

        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.CreateValues, Json);
        }
        catch (JsonException)
        {
            // A broken request keeps the names, so the character can still be picked and deleted.
        }

        var values = stored is null
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["FirstName"] = entity.FirstName, ["LastName"] = entity.LastName ?? string.Empty }
            : new Dictionary<string, string>(stored, StringComparer.Ordinal);

        CharacterProgress? progress = null;

        if (entity.HasProgress)
        {
            var items = entity.Items
                .OrderBy(item => item.Id)
                .Select(item => new SavedItem(item.Id, item.Definition, item.Tint, item.Count > 0 ? item.Count : 1))
                .ToList();

            var equipment = entity.Profiles
                .OrderBy(profile => profile.Id)
                .ToDictionary(
                    profile => profile.Id,
                    profile => (IReadOnlyList<SavedSlot>)profile.Items.OrderBy(worn => worn.Order).Select(worn => new SavedSlot(worn.Slot, worn.ItemId)).ToList());

            var position = entity.Zone is { } zone && entity.PositionX is { } x && entity.PositionY is { } y && entity.PositionZ is { } z
                ? new SavedPosition(zone, x, y, z, entity.RotationX ?? 0, entity.RotationY ?? 0, entity.RotationZ ?? 0)
                : null;

            progress = new CharacterProgress(entity.ActiveProfileId, entity.Coins, items, equipment, position, entity.GotStartingCoins);
        }

        return new SavedCharacter(entity.Id, values, entity.Created.UtcDateTime, entity.LastLogin?.UtcDateTime, progress);
    }

    private static DateTimeOffset Utc(DateTime time) =>
        new(time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
