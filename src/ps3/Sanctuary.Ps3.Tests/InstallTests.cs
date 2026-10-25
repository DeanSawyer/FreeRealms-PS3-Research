using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

/// <summary>Runs the pack, zone and spawn code on the synthetic installs and compares with the Python reference.</summary>
[TestClass]
public sealed class InstallTests
{
    [TestMethod]
    public void PackIndexMatchesReference()
    {
        foreach (var expected in ReferenceVectors.Installs)
        {
            using var install = new TempInstall(expected.Files);

            var packs = PackIndex.Load(install.Root);

            CollectionAssert.AreEqual(
                expected.Packs,
                packs.Packs.Select(Path.GetFileName).ToArray(),
                expected.Label + ": packs");

            CollectionAssert.AreEqual(expected.Unreadable, packs.Unreadable.ToArray(), expected.Label + ": unreadable packs");

            CollectionAssert.AreEqual(
                expected.Entries,
                packs.Entries.Select(entry => (entry.Pack, entry.Name, entry.Offset, entry.Length, entry.Crc)).ToArray(),
                expected.Label + ": entries");
        }
    }

    [TestMethod]
    public void ZonePickMatchesReference()
    {
        foreach (var expected in ReferenceVectors.Installs)
        {
            using var install = new TempInstall(expected.Files);

            foreach (var (preferred, zone, note) in expected.Picks)
            {
                var choice = ZoneCatalog.Pick(install.Root, preferred);
                var label = $"{expected.Label}: preferred '{preferred}'";

                Assert.AreEqual(zone, choice.Zone, label + ": zone");
                Assert.AreEqual(note, choice.Note.Replace(install.Root, "{usr}"), label + ": note");
                CollectionAssert.AreEqual(expected.CompleteZones, choice.CompleteZones.ToArray(), label + ": complete zones");
            }
        }
    }

    [TestMethod]
    public void AssetsAreReadFromTheFirstPackThatListsThem()
    {
        foreach (var expected in ReferenceVectors.Installs)
        {
            using var install = new TempInstall(expected.Files);

            foreach (var (name, pack, storedCrc, crcMatches, length, dataCrc) in expected.Assets)
            {
                var label = $"{expected.Label}: '{name}'";
                var asset = PackIndex.ReadAsset(install.Root, name);

                Assert.IsNotNull(asset, label);
                Assert.AreEqual(pack, asset.Pack, label + ": pack");
                Assert.AreEqual(storedCrc, asset.StoredCrc, label + ": stored crc");
                Assert.AreEqual(crcMatches, asset.CrcMatches, label + ": crc check");
                Assert.AreEqual(length, asset.Data.Length, label + ": length");
                Assert.AreEqual(dataCrc, Crc32.Compute(asset.Data), label + ": data crc");
            }

            foreach (var name in expected.MissingAssets)
                Assert.IsNull(PackIndex.ReadAsset(install.Root, name), $"{expected.Label}: '{name}' must not exist");
        }
    }

    [TestMethod]
    public void SpawnMatchesReference()
    {
        foreach (var expected in ReferenceVectors.Installs)
        {
            using var install = new TempInstall(expected.Files);

            foreach (var (zone, found, x, z, tileX, tileZ, note) in expected.Spawns)
            {
                var label = $"{expected.Label}: spawn in '{zone}'";
                var spawn = Terrain.PickSpawn(install.Root, zone);

                if (!found)
                {
                    Assert.IsNull(spawn, label);
                    continue;
                }

                Assert.IsNotNull(spawn, label);
                Assert.AreEqual(x, spawn.X, 0.0, label + ": x");
                Assert.AreEqual(z, spawn.Z, 0.0, label + ": z");
                Assert.AreEqual(tileX, spawn.Tile.X, label + ": tile x");
                Assert.AreEqual(tileZ, spawn.Tile.Z, label + ": tile z");
                Assert.AreEqual(note, spawn.Note, label + ": note");
            }
        }
    }

    [TestMethod]
    public void MissingFolderHasNoPacks()
    {
        var packs = PackIndex.Load(Path.Combine(Path.GetTempPath(), "sanctuary-ps3-does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.AreEqual(0, packs.Packs.Count);
        Assert.AreEqual(0, packs.Entries.Count);
    }
}
