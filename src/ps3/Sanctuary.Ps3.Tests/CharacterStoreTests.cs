using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The saved characters: guids, picking one, deleting, the database that keeps them between runs and the import of characters.json.</summary>
[TestClass]
public sealed class CharacterStoreTests
{
    private static readonly DateTime Noon = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static (string, string)[] Character(string first, string last) =>
        [("Session", "secret"), ("FirstName", first), ("LastName", last), ("Head", "2"), ("ItemChest", "28216")];

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"fr-characters-{Guid.NewGuid():N}", "characters.db");

    private static void Remove(string file)
    {
        var folder = Path.GetDirectoryName(file)!;

        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [TestMethod]
    public void CreateGivesEachCharacterItsOwnGuidAndNeverKeepsTheSession()
    {
        var store = new CharacterStore();

        Assert.AreEqual(1UL, store.Create(Character("Aedon", "Amberarm")));
        Assert.AreEqual(2UL, store.Create(Character("Mira", "Moonpetal")));

        Assert.AreEqual(2, store.Count);
        Assert.IsFalse(store.All().Any(character => character.Values.ContainsKey("Session")));
        Assert.AreEqual("Aedon Amberarm", store.Find(1)!.Name);
        Assert.AreEqual("Mira", store.Snapshot()["FirstName"], "the newest character is the one the game server sends");
    }

    [TestMethod]
    public void SelectPicksTheCharacterTheGameServerSends()
    {
        var store = new CharacterStore(now: () => Noon);

        store.Create(Character("Aedon", "Amberarm"));
        store.Create(Character("Mira", "Moonpetal"));

        Assert.IsTrue(store.Select(1));
        Assert.AreEqual("Aedon", store.Snapshot()["FirstName"]);
        Assert.AreEqual(Noon, store.Find(1)!.LastPlayed);

        Assert.IsFalse(store.Select(99));
        Assert.AreEqual("Aedon", store.Snapshot()["FirstName"], "an unknown guid changes nothing");
    }

    [TestMethod]
    public void DeleteRemovesOneCharacter()
    {
        var store = new CharacterStore();

        store.Create(Character("Aedon", "Amberarm"));
        store.Create(Character("Mira", "Moonpetal"));
        store.Select(2);

        Assert.IsTrue(store.Delete(2));
        Assert.IsFalse(store.Delete(2));
        Assert.AreEqual(1, store.Count);
        Assert.AreEqual("Aedon", store.Snapshot()["FirstName"], "with the picked one gone, the newest left is sent");

        Assert.IsTrue(store.Delete(1));
        Assert.AreEqual(0, store.Snapshot().Count);
    }

    [TestMethod]
    public void NameIsTheFirstNameAloneWithoutALastName()
    {
        var store = new CharacterStore();

        store.Create([("FirstName", "Solo"), ("LastName", "")]);

        Assert.AreEqual("Solo", store.Find(1)!.Name);
    }

    [TestMethod]
    public void CharactersSurviveARestart()
    {
        var file = TempFile();

        try
        {
            var first = new CharacterStore(file, () => Noon);

            first.Create(Character("Aedon", "Amberarm"));
            first.Create(Character("Mira", "Moonpetal"));
            first.Delete(1);
            first.Select(2);

            Assert.IsTrue(File.Exists(file));
            Assert.IsNull(first.SaveProblem);

            var second = new CharacterStore(file);

            Assert.IsNull(second.LoadProblem);
            Assert.AreEqual(1, second.Count);
            Assert.AreEqual("Mira Moonpetal", second.Find(2)!.Name);
            Assert.AreEqual("2", second.Find(2)!.Values["Head"]);
            Assert.AreEqual(Noon, second.Find(2)!.Created);
            Assert.AreEqual(Noon, second.Find(2)!.LastPlayed);

            // A deleted guid is never handed out again.
            Assert.AreEqual(3UL, second.Create(Character("Nox", "Nightbloom")));
        }
        finally
        {
            Remove(file);
        }
    }

    [TestMethod]
    public void ADatabaseThatCannotBeReadIsSetAsideNotOverwritten()
    {
        var file = TempFile();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ this is not a database, nor json, but a test of what happens when the file is not one");

            var store = new CharacterStore(file, () => Noon);

            Assert.IsNotNull(store.LoadProblem);
            Assert.AreEqual(0, store.Count);
            StringAssert.StartsWith(File.ReadAllText($"{file}.unreadable-20261113-120000"), "{ this is not a database");

            store.Create(Character("Aedon", "Amberarm"));

            Assert.AreEqual(1, new CharacterStore(file).Count, "the new list is saved in a fresh database");
        }
        finally
        {
            Remove(file);
        }
    }

    [TestMethod]
    public void TheListStartsWithTheCharacterThePs3ShowsNext()
    {
        var clock = Noon;
        var store = new CharacterStore(now: () => clock);

        store.Create(Character("Aedon", "Amberarm"));
        clock = clock.AddHours(1);
        store.Create(Character("Mira", "Moonpetal"));
        clock = clock.AddHours(1);
        store.Create(Character("Nox", "Nightbloom"));

        // The one made last is shown next.
        CollectionAssert.AreEqual(new[] { 3UL, 2UL, 1UL }, store.Ordered().Select(character => character.Guid).ToArray());

        // Picked in the manager.
        Assert.IsTrue(store.ShowNext(1));
        Assert.AreEqual(1UL, store.ShownNext);
        CollectionAssert.AreEqual(new[] { 1UL, 3UL, 2UL }, store.Ordered().Select(character => character.Guid).ToArray());

        // Played: that one comes first.
        clock = clock.AddHours(1);
        store.Select(2);
        Assert.AreEqual(2UL, store.ShownNext);
        Assert.IsFalse(store.ShowNext(99));

        // Deleting the one shown next falls back to the most recently played or made.
        store.Delete(2);
        Assert.AreEqual(3UL, store.ShownNext);
    }

    [TestMethod]
    public void CreatorNextIsSavedAndEndsWithTheNextCharacter()
    {
        var file = TempFile();

        try
        {
            var store = new CharacterStore(file);

            store.Create(Character("Aedon", "Amberarm"));
            store.SetCreatorNext(true);

            Assert.IsTrue(new CharacterStore(file).CreatorNext, "kept in the database");

            store.Create(Character("Mira", "Moonpetal"));

            Assert.IsFalse(store.CreatorNext);
            Assert.AreEqual(2UL, new CharacterStore(file).ShownNext);

            store.SetCreatorNext(true);
            store.ShowNext(1);

            Assert.IsFalse(store.CreatorNext, "picking a character to show turns the creator off");
        }
        finally
        {
            Remove(file);
        }
    }

    [TestMethod]
    public void NamesAreUniqueIgnoringCase()
    {
        var store = new CharacterStore();

        store.Create(Character("Aedon", "Amberarm"));
        store.Create([("FirstName", "Solo"), ("LastName", "")]);

        Assert.IsTrue(store.IsNameTaken("aedon", "AMBERARM"));
        Assert.IsTrue(store.IsNameTaken(" Solo ", null));
        Assert.IsFalse(store.IsNameTaken("Aedon", "Brightwing"));
        Assert.IsFalse(store.IsNameTaken("", ""));
    }

    [TestMethod]
    public void WithoutAFileNothingIsWritten()
    {
        var store = new CharacterStore();

        store.Create(Character("Aedon", "Amberarm"));

        Assert.IsNull(store.Path);
        Assert.IsNull(store.SaveProblem);
        Assert.IsNull(store.LoadProblem);
    }

    [TestMethod]
    public void TheCharactersJsonOfBeforeIsImportedOnce()
    {
        var file = TempFile();
        var json = Path.ChangeExtension(file, ".json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(json, """
                {
                  "NextGuid": 5,
                  "ShowNext": 4,
                  "CreatorNext": false,
                  "Characters": [
                    {
                      "Guid": 4,
                      "Name": "Mira Moonpetal",
                      "Created": "2026-11-13T12:00:00Z",
                      "LastPlayed": "2026-11-13T13:00:00Z",
                      "Values": { "FirstName": "Mira", "LastName": "Moonpetal", "Head": "2", "ItemChest": "28216" },
                      "Progress": {
                        "ActiveJob": 2,
                        "Coins": 99,
                        "Items": [ { "Id": 1, "Definition": 28216, "Tint": 234 }, { "Id": 2, "Definition": 392, "Tint": 262, "Count": 3 } ],
                        "Equipment": { "2": [ { "Slot": 3, "Item": 1 }, { "Slot": 4, "Item": 2 } ] },
                        "Position": { "Zone": "FabledRealms", "X": 1.5, "Y": 2.5, "Z": 3.5, "RotationX": 0, "RotationY": 1, "RotationZ": 0 },
                        "GotStartingCoins": true
                      }
                    }
                  ]
                }
                """);

            var store = new CharacterStore(file);

            Assert.IsNull(store.LoadProblem);
            Assert.IsNotNull(store.Imported);
            Assert.IsFalse(File.Exists(json), "renamed once imported");
            Assert.IsTrue(File.Exists(json + ".imported"));

            // Read back from the database, not the file.
            var read = new CharacterStore(file);
            var mira = read.Find(4)!;

            Assert.IsNull(read.Imported);
            Assert.AreEqual("Mira Moonpetal", mira.Name);
            Assert.AreEqual("28216", mira.Values["ItemChest"]);
            Assert.AreEqual(new DateTime(2026, 11, 13, 13, 0, 0, DateTimeKind.Utc), mira.LastPlayed);
            Assert.AreEqual(4UL, read.ShownNext);

            var progress = mira.Progress!;

            Assert.AreEqual(2, progress.ActiveJob);
            Assert.AreEqual(99, progress.Coins);
            Assert.IsTrue(progress.GotStartingCoins);
            CollectionAssert.AreEqual(new[] { new SavedItem(1, 28216, 234), new SavedItem(2, 392, 262, 3) }, progress.Items.ToArray());
            CollectionAssert.AreEqual(new[] { new SavedSlot(3, 1), new SavedSlot(4, 2) }, progress.Equipment[2].ToArray());
            Assert.AreEqual(new SavedPosition("FabledRealms", 1.5, 2.5, 3.5, 0, 1, 0), progress.Position);

            // The next id carries on from the file's.
            Assert.AreEqual(5UL, read.Create(Character("Nox", "Nightbloom")));
        }
        finally
        {
            Remove(file);
        }
    }

    [TestMethod]
    public void AJsonNameMeansTheDatabaseNextToIt()
    {
        Assert.AreEqual(Path.Combine("data", "characters.db"), CharacterStore.DatabaseFile(Path.Combine("data", "characters.json")));
        Assert.AreEqual("mine.db", CharacterStore.DatabaseFile("mine.db"));
    }
}
