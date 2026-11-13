using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>A character's progress (items, each job's equipment, the active job, coins): kept by the inventory, saved, and restored.</summary>
[TestClass]
public sealed class ProgressTests
{
    private static readonly Dictionary<string, string> Creator = new()
    {
        ["FirstName"] = "Aedon", ["LastName"] = "Amberarm", ["Model"] = "1", ["Head"] = "1",
        ["ItemChest"] = "28216", ["ItemChestTint"] = "234", ["ItemLegs"] = "392", ["ItemLegsTint"] = "262",
        ["ItemFeet"] = "17397", ["ItemFeetTint"] = "234",
    };

    private static readonly GatewayOptions Options = new() { Sanctuary = true };

    [TestMethod]
    public void ANewCharactersLoadoutIsWhatTheLoginPacketsAlwaysHad()
    {
        var world = new GatewayWorld(Options);
        var loadout = Loadout.ForNewCharacter(Creator, Options);

        Assert.AreEqual(Ps3Packets.ProfileId, loadout.ActiveJob);
        Assert.AreEqual(0, loadout.Coins);
        CollectionAssert.AreEqual(Ps3Packets.PlayerBlob(Creator, world, 1234), Ps3Packets.PlayerBlob(Creator, world, 1234, loadout));
        CollectionAssert.AreEqual(Ps3Packets.ProfileList(Creator, Options), Ps3Packets.ProfileList(loadout, Options));
    }

    [TestMethod]
    public void TheInventoryReportsWhatChangedAndComesBackTheSame()
    {
        var inventory = new Inventory(Creator, Options);
        var start = inventory.Snapshot();
        var otherJob = start.Equipment.Keys.First(job => job != Jobs.StarterJob && start.Equipment[job].Count > 0);

        Assert.IsTrue(inventory.SetProfile(otherJob).Count > 0);

        var firstSlot = start.Equipment[Jobs.StarterJob].Keys.First();

        Assert.IsTrue(inventory.Unequip(firstSlot, Jobs.StarterJob).Count > 0);

        var changed = inventory.Snapshot();

        Assert.AreEqual(otherJob, changed.ActiveJob);
        Assert.IsFalse(changed.Equipment[Jobs.StarterJob].ContainsKey(firstSlot));
        CollectionAssert.AreEqual(start.Items.ToList(), changed.Items.ToList(), "nothing is gained or lost by changing clothes");

        // Saved and read back: same items, same slots in the same order, same job.
        var back = SavedProgress.ToLoadout(SavedProgress.ToProgress(changed));
        var restored = new Inventory(back, Options).Snapshot();

        Assert.AreEqual(otherJob, restored.ActiveJob);
        CollectionAssert.AreEqual(changed.Items.ToList(), restored.Items.ToList());

        foreach (var (job, slots) in changed.Equipment)
            CollectionAssert.AreEqual(slots.ToList(), restored.Equipment[job].ToList(), $"job {job}");

        // The player data a restored character logs in with starts on that job.
        var blob = Ps3Packets.PlayerBlob(Creator, new GatewayWorld(Options), 1234, back);

        CollectionAssert.AreNotEqual(Ps3Packets.PlayerBlob(Creator, new GatewayWorld(Options), 1234), blob);
    }

    [TestMethod]
    public void AJobNoLongerChosenFallsBackToTheFirst()
    {
        var loadout = Loadout.ForNewCharacter(Creator, Options) with { ActiveJob = 9999 };

        Assert.AreEqual(Jobs.StarterJob, new Inventory(loadout, Options).Active);
    }

    [TestMethod]
    public void ProgressIsKeptInTheCharacterFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fr-progress-{Guid.NewGuid():N}", "characters.json");

        try
        {
            var store = new CharacterStore(file);
            var guid = store.Create(Creator.Select(pair => (pair.Key, pair.Value)));
            var inventory = new Inventory(Creator, Options);

            inventory.SetProfile(inventory.Snapshot().Equipment.Keys.Last());

            var progress = SavedProgress.ToProgress(inventory.Snapshot() with { Coins = 250 });

            Assert.IsTrue(store.SaveProgress(guid, progress));
            Assert.IsFalse(store.SaveProgress(99, progress));

            var read = new CharacterStore(file).Find(guid)!.Progress!;

            Assert.AreEqual(progress.ActiveJob, read.ActiveJob);
            Assert.AreEqual(250, read.Coins);
            CollectionAssert.AreEqual(progress.Items.ToList(), read.Items.ToList());
            CollectionAssert.AreEquivalent(progress.Equipment.Keys.ToList(), read.Equipment.Keys.ToList());

            foreach (var (job, slots) in progress.Equipment)
                CollectionAssert.AreEqual(slots.ToList(), read.Equipment[job].ToList(), $"job {job}");

            Assert.AreEqual(guid, new CharacterStore(file).Current()!.Guid);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
