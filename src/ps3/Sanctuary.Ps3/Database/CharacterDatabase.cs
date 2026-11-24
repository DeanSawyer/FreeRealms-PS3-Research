using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
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
            .Include(c => c.Pets)
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

        db.Pets.ExecuteDelete();
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

        if (Upgraded.TryAdd(Options.ConnectionString, true))
            AddMissingTables(db);

        return db;
    }

    /// <summary>The connection strings whose database has been checked for tables added after it was made.</summary>
    private static readonly ConcurrentDictionary<string, bool> Upgraded = new();

    /// <summary>The tables the model has gained since the first databases were made (EnsureCreated does nothing to a database that exists).</summary>
    private static readonly string[] LaterTables = ["Pets"];

    /// <summary>
    /// Makes the tables of <see cref="LaterTables"/> that an older database lacks, with the statements EF Core would make them with
    /// (its create script, so SQLite and MySQL each get their own).
    /// </summary>
    private static void AddMissingTables(Ps3DatabaseContext db)
    {
        string? script = null;

        foreach (var table in LaterTables)
        {
            try
            {
                var probe = "SELECT COUNT(*) FROM " + Quote(db, table);
                db.Database.ExecuteSqlRaw(probe);
                continue;
            }
            catch (DbException)
            {
                // Not there yet.
            }

            script ??= db.Database.GenerateCreateScript();

            foreach (var statement in script.Split(';'))
            {
                var text = statement.Trim();

                if (text.Contains($"TABLE {Quote(db, table)}", StringComparison.Ordinal) || text.Contains($"ON {Quote(db, table)}", StringComparison.Ordinal))
                    db.Database.ExecuteSqlRaw(text);
            }
        }
    }

    private static string Quote(Ps3DatabaseContext db, string table) => db.Database.IsSqlite() ? $"\"{table}\"" : $"`{table}`";

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

        foreach (var pet in (progress.Pets ?? []).DistinctBy(pet => pet.Id))
            entity.Pets.Add(ToEntity(pet));

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

    private static DbPet ToEntity(SavedPet pet)
    {
        int At(IReadOnlyList<int> values, int index) => index < values.Count ? values[index] : 0;

        return new DbPet
        {
            Id = pet.Id,
            Definition = pet.Definition,
            Tint = pet.Tint,
            Name = pet.Name,
            Hunger = pet.Hunger,
            Hygiene = pet.Hygiene,
            Play = pet.Play,
            Mood = pet.Mood,
            Trait = pet.Trait,
            TreasureFoundAt = pet.TreasureFoundAt,
            EquippedPetTrick1 = At(pet.FavoriteTricks, 0),
            EquippedPetTrick2 = At(pet.FavoriteTricks, 1),
            EquippedPetTrick3 = At(pet.FavoriteTricks, 2),
            EquippedPetTrick4 = At(pet.FavoriteTricks, 3),
            ShampooItemId = At(pet.CareItems, 0),
            ConditionerItemId = At(pet.CareItems, 1),
            ToyItemId = At(pet.CareItems, 2),
            BrushItemId = At(pet.CareItems, 3),
            FoodBowlItemId = At(pet.CareItems, 4),
            FoodItemId = At(pet.CareItems, 5),
            VitaminItemId = At(pet.CareItems, 6),
            Created = pet.Created,
        };
    }

    private static SavedPet ToSaved(DbPet pet) => new(
        pet.Id, pet.Definition, pet.Tint, pet.Name, pet.Hunger, pet.Hygiene, pet.Play, pet.Mood, pet.Trait, pet.TreasureFoundAt,
        [pet.EquippedPetTrick1, pet.EquippedPetTrick2, pet.EquippedPetTrick3, pet.EquippedPetTrick4],
        [pet.ShampooItemId, pet.ConditionerItemId, pet.ToyItemId, pet.BrushItemId, pet.FoodBowlItemId, pet.FoodItemId, pet.VitaminItemId],
        pet.Created);

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

            progress = new CharacterProgress(entity.ActiveProfileId, entity.Coins, items, equipment, position, entity.GotStartingCoins,
                [.. entity.Pets.OrderBy(pet => pet.Id).Select(ToSaved)]);
        }

        return new SavedCharacter(entity.Id, values, entity.Created.UtcDateTime, entity.LastLogin?.UtcDateTime, progress);
    }

    private static DateTimeOffset Utc(DateTime time) =>
        new(time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
