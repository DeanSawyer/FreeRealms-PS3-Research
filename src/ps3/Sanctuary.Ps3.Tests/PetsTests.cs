using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The pets: Sanctuary's definitions, the Pet (53) packets as the PS3 reads them, care, naming, following and saving.</summary>
[TestClass]
public sealed class PetsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static PetCompanion Starters(int seed = 1) => PetCompanion.Load(null, giveStarters: true, _ => true, new Random(seed), Now);

    private static OwnedPet Bare()
    {
        // A pet item whose model has no Sanctuary definition: no tricks or item classes; empty names and texture.
        var item = new PetItem(1, 0, 0, 0, 0, -1, "dog.adr", 1, 0, string.Empty, string.Empty, false, false, 1, 0, string.Empty);

        return new OwnedPet(1, item, 0, string.Empty, Temperament.Friendly, Now);
    }

    [TestMethod]
    public void SanctuarysPetDataIsLoaded()
    {
        Assert.AreEqual(160, PetCatalog.Kinds.Count);
        Assert.IsTrue(PetCatalog.Items.Count > 500);

        var lab = PetCatalog.Items[2644];
        var kind = PetCatalog.KindOf(lab)!;
        Assert.IsTrue(kind.Tricks.Count > 0 && kind.Idles.Count > 0 && kind.RestPostures.Count > 0);
        Assert.IsTrue(PetCatalog.Kinds.Values.Any(k => k.TrickAnimations.Count > 0));
    }

    [TestMethod]
    public void ACharacterWithoutPetsGetsTheStarters()
    {
        var companion = Starters();

        Assert.AreEqual(PetCompanion.Starters.Count, companion.Pets.Count);
        CollectionAssert.AreEqual(Enumerable.Range(1, companion.Pets.Count).ToList(), companion.Pets.Select(pet => pet.Id).ToList());
        Assert.IsTrue(companion.Pets.All(pet => pet.Temperament != Temperament.Unset && pet.Mood == OwnedPet.FullNeed));
        Assert.AreEqual(0, PetCompanion.Load(null, giveStarters: false, _ => true, new Random(1), Now).Pets.Count);
        Assert.AreEqual(0, PetCompanion.Load(null, giveStarters: true, _ => false, new Random(1), Now).Pets.Count, "no models, no pets");
    }

    [TestMethod]
    public void ThePetListIsAsLongAsThePs3Reads()
    {
        // 0xa88634 read 107 bytes for a pet with no tricks, items or classes and empty strings; the list adds its header, count and
        // the active pet (tools/ppc_trace.py on the Pet handler 0x6949c).
        Assert.AreEqual(3 + 4 + 107 + 12, PetWire.List([Bare()], 0, 0).Length);

        // A trick is 37 bytes with its key, an item class 12; the name and texture are strings.
        var pet = Starters().Pets[0];
        var kind = pet.Kind!;
        var expected = 3 + 4 + 107 + (37 * kind.Tricks.Count) + (12 * kind.ItemClasses.Count) + pet.Name.Length + pet.Item.TextureAlias.Length + 12;
        var list = PetWire.List([pet], 1, PetWire.GuidBase + 1);

        Assert.AreEqual(expected, list.Length);
        Assert.AreEqual(PetWire.Opcode, BinaryPrimitives.ReadUInt16LittleEndian(list));
        Assert.AreEqual(PetWire.ListSub, list[2]);
        Assert.AreEqual(kind.IconId, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(3 + 4 + 9)), "Sanctuary's icon");
        Assert.AreEqual(PetWire.GuidBase + 1, BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(list.Length - 8)));
    }

    [TestMethod]
    public void TheOtherPetPacketsAreAsLongAsThePs3Reads()
    {
        var pet = Bare();

        // The handler read 23, 15 and 19 bytes (with a four-letter name) of these.
        Assert.AreEqual(23, PetWire.Status(pet).Length);
        Assert.AreEqual(15, PetWire.Active(1, 5).Length);
        Assert.AreEqual(19, PetWire.SetName(5, "Rexy").Length);
        Assert.AreEqual(40, PetNpc.Position(5, 1, 2, 3, 0, true, 6).Length);
    }

    [TestMethod]
    public void TheClientsPetPacketsAreRead()
    {
        Assert.AreEqual((2, true), PetWire.FromSummonRecall([53, 0, 4, 2, 0, 0, 0, 1]));
        Assert.AreEqual((2, false), PetWire.FromSummonRecall([53, 0, 4, 2, 0, 0, 0, 0]));
        Assert.IsNull(PetWire.FromSummonRecall([53, 0, 5, 2, 0, 0, 0, 1]));

        Assert.AreEqual("Rex", PetWire.FromRequestName([53, 0, 6, 3, 0, 0, 0, (byte)'R', (byte)'e', (byte)'x']));
        Assert.IsNull(PetWire.FromRequestName([53, 0, 6, 9, 0, 0, 0, (byte)'R']), "longer than the packet");

        Assert.AreEqual(true, PetWire.FromUiMode([53, 0, 24, 1, 0, 0, 0]));
        Assert.AreEqual((0, 300, 9000), PetWire.FromTactile([53, 0, 22, 0, 0, 0, 0, 0, 0, 0, 0, 44, 1, 0, 0, 40, 35, 0, 0]));

        var update = new byte[40];
        Assert.IsTrue(PetNpc.IsMoving(update));
        update[34] = 1;
        Assert.IsFalse(PetNpc.IsMoving(update));
    }

    [TestMethod]
    public void CareRaisesPlayAndMoodAsSanctuarysDoes()
    {
        var pet = Bare();
        pet.Hunger = 50;
        pet.Hygiene = 50;
        pet.Play = 50;
        pet.Mood = 10;

        Assert.IsTrue(PetCare.Advance(pet, 6, scratched: 0));
        Assert.AreEqual(51f, pet.Play, 1e-4, "a sixth a second");
        Assert.AreEqual(16f, pet.Mood, 1e-4, "a point a second, below the target");

        pet.Mood = 90;
        PetCare.Advance(pet, 1.5f, scratched: 0);
        Assert.AreEqual(PetCare.MoodTarget(50, 50, pet.Play), pet.Mood, 1e-4, "falls to the target at once");

        Assert.AreEqual(2f, PetCare.ScratchPlay(1_000_000, 10), "at most 2");
        Assert.AreEqual(0.5f, PetCare.ScratchPlay(5000, 100), 1e-4);
        Assert.AreEqual(3f / 1.02f, PetCare.MoodTarget(0, 100, 100), 0.001f, "a need at 0 counts as 1, and drags the mean down");
    }

    [TestMethod]
    public void SummonCareRenameAndDismiss()
    {
        var companion = Starters();
        var owner = new Placement(100, 5, 100, 0, 0, 1);
        var start = TimeSpan.FromSeconds(10);

        var summoned = companion.Summon(1, owner, start);
        CollectionAssert.AreEqual(new[] { "AddNpc pet 1", $"PetList {companion.Pets.Count}" }, summoned.Select(packet => packet.Name).ToList());
        Assert.AreEqual(1, companion.ActiveId);

        // Beside its owner (facing +z), two units to one side.
        var spawn = summoned[0].Inner;
        var x = BinaryPrimitives.ReadSingleLittleEndian(spawn.AsSpan(44));
        Assert.AreEqual(2f, Math.Abs(x - 100), 1e-3);
        Assert.AreEqual(PetNpc.MovementType, BinaryPrimitives.ReadInt32LittleEndian(spawn.AsSpan(spawn.Length - 4)), "moved by position updates");

        // Another pet: the first goes away first.
        var second = companion.Summon(2, owner, start);
        CollectionAssert.AreEqual(new[] { "RemovePlayer pet 1", "PetActive", "AddNpc pet 2", $"PetList {companion.Pets.Count}" }, second.Select(packet => packet.Name).ToList());

        // Care: nothing before 1.5 s; full needs keep play and mood where they are.
        Assert.AreEqual(0, companion.Tick(start + TimeSpan.FromSeconds(1)).Count);
        companion.Out!.Play = 40;
        Assert.AreEqual("PetStatus", companion.Tick(start + TimeSpan.FromSeconds(2)).Single().Name);

        // Petting adds play at the next care step.
        var play = companion.Out.Play;
        companion.Scratch(5000, 100);
        companion.Tick(start + TimeSpan.FromSeconds(3.5));
        Assert.AreEqual(play + (1.5f / 6f) + 0.5f, companion.Out.Play, 1e-3);

        CollectionAssert.AreEqual(new[] { "PetSetName", $"PetList {companion.Pets.Count}" }, companion.Rename("Biscuit").Select(packet => packet.Name).ToList());
        Assert.AreEqual("Biscuit", companion.Out.Name);
        Assert.AreEqual("PetSetName refused", companion.Rename("   ").Single().Name);
        Assert.AreEqual("PetSetName refused", companion.Rename(new string('a', 33)).Single().Name);
        Assert.AreEqual("Biscuit", companion.Out.Name);

        CollectionAssert.AreEqual(new[] { "RemovePlayer pet 2", "PetActive" }, companion.Recall().Select(packet => packet.Name).ToList());
        Assert.AreEqual(0, companion.Restore(owner, start).Count, "dismissed: it does not come back");
    }

    [TestMethod]
    public void APetPutAwayForAZoneLoadComesBack()
    {
        var companion = Starters();
        var owner = new Placement(0, 0, 0, 0, 0, 1);

        companion.Summon(3, owner, TimeSpan.Zero);
        var back = companion.Restore(owner, TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "RemovePlayer pet 3", "PetActive", "AddNpc pet 3", $"PetList {companion.Pets.Count}" }, back.Select(packet => packet.Name).ToList());
        Assert.AreEqual(3, companion.ActiveId);
    }

    [TestMethod]
    public void ASummonedPetFollowsItsOwner()
    {
        var companion = Starters();
        var start = new Placement(100, 5, 100, 0, 0, 1);
        var now = TimeSpan.FromSeconds(10);

        companion.Summon(1, start, now);

        Assert.AreEqual(0, companion.Moved(start with { Z = 100.2 }, false, 6, now).Count, "hardly moved");

        var walked = companion.Moved(start with { Z = 105 }, true, 6, now + TimeSpan.FromSeconds(1));
        Assert.AreEqual("PetPosition", walked.Single().Name);
        Assert.AreEqual(0, walked[0].Inner[34], "moving");
        Assert.AreEqual(0, companion.Moved(start with { Z = 106 }, true, 6, now + TimeSpan.FromSeconds(1.05)).Count, "too soon after the last one");

        var stopped = companion.Moved(start with { Z = 106.2 }, false, 6, now + TimeSpan.FromSeconds(1.1));
        Assert.AreEqual(1, stopped.Single().Inner[34], "told to stop at once");

        // The pet window holds it still.
        companion.SetWindowOpen(true);
        Assert.AreEqual(0, companion.Moved(start with { Z = 120 }, true, 6, now + TimeSpan.FromSeconds(2)).Count);
        companion.SetWindowOpen(false);

        // A warpstone: it is put down beside its owner again.
        var teleported = companion.Moved(start with { X = 900 }, false, 6, now + TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(new[] { "RemovePlayer pet", "AddNpc pet 1" }, teleported.Select(packet => packet.Name).ToList());
    }

    [TestMethod]
    public void PetsAreSavedWithTheCharacter()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fr-pets-{Guid.NewGuid():N}", "characters.json");

        try
        {
            var store = new CharacterStore(file);
            var guid = store.Create([("FirstName", "Aedon"), ("Head", "1")]);
            var companion = Starters();

            companion.Pets[1].Name = "Biscuit";
            companion.Pets[1].Hunger = 42;
            companion.Pets[1].Favorites[2] = 11;
            companion.Pets[1].CareItems[4] = 777;

            Assert.IsTrue(store.SaveProgress(guid, new CharacterProgress(0, 0, [], new Dictionary<int, IReadOnlyList<SavedSlot>>(), Pets: companion.Snapshot())));

            var pets = new CharacterStore(file).Find(guid)!.Progress!.Pets!;
            var again = PetCompanion.Load(pets, giveStarters: true, _ => true, new Random(2), Now);

            Assert.AreEqual(companion.Pets.Count, again.Pets.Count, "saved pets: no starters added");
            Assert.AreEqual("Biscuit", again.Pets[1].Name);
            Assert.AreEqual(42f, again.Pets[1].Hunger);
            Assert.AreEqual(11, again.Pets[1].Favorites[2]);
            Assert.AreEqual(777, again.Pets[1].CareItems[4]);
            Assert.AreEqual(companion.Pets[1].Temperament, again.Pets[1].Temperament);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [TestMethod]
    public void ADatabaseMadeBeforePetsGetsThePetsTable()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"fr-pets-old-{Guid.NewGuid():N}");
        var file = Path.Combine(folder, "characters.json");

        try
        {
            var store = new CharacterStore(file);
            var guid = store.Create([("FirstName", "Aedon"), ("Head", "1")]);
            var database = store.Path!;

            SqliteConnection.ClearAllPools();

            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE \"Pets\"";
                drop.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            // A copy is a database this run has not seen yet, as an older one would be.
            var older = Path.Combine(folder, "older", "characters.json");
            Directory.CreateDirectory(Path.GetDirectoryName(older)!);
            File.Copy(database, Path.Combine(Path.GetDirectoryName(older)!, Path.GetFileName(database)));

            var reopened = new CharacterStore(older);
            Assert.IsTrue(reopened.SaveProgress(guid, new CharacterProgress(0, 0, [], new Dictionary<int, IReadOnlyList<SavedSlot>>(), Pets: Starters().Snapshot())));
            Assert.AreEqual(PetCompanion.Starters.Count, new CharacterStore(older).Find(guid)!.Progress!.Pets!.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
