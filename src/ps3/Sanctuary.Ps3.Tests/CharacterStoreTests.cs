using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The saved characters: guids, picking one, deleting, and the JSON file that keeps them between runs.</summary>
[TestClass]
public sealed class CharacterStoreTests
{
    private static readonly DateTime Noon = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static (string, string)[] Character(string first, string last) =>
        [("Session", "secret"), ("FirstName", first), ("LastName", last), ("Head", "2"), ("ItemChest", "28216")];

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"fr-characters-{Guid.NewGuid():N}", "characters.json");

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
            Assert.IsFalse(File.Exists(file + ".tmp"));
        }
        finally
        {
            Remove(file);
        }
    }

    [TestMethod]
    public void AFileThatCannotBeReadIsSetAsideNotOverwritten()
    {
        var file = TempFile();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ this is not json");

            var store = new CharacterStore(file, () => Noon);

            Assert.IsNotNull(store.LoadProblem);
            Assert.AreEqual(0, store.Count);
            Assert.AreEqual("{ this is not json", File.ReadAllText($"{file}.unreadable-20261113-120000"));

            store.Create(Character("Aedon", "Amberarm"));

            Assert.AreEqual(1, new CharacterStore(file).Count, "the new list is saved in a fresh file");
        }
        finally
        {
            Remove(file);
        }
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
}
