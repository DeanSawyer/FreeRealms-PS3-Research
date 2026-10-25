using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

[TestClass]
public sealed class ManifestTests
{
    [TestMethod]
    public void ManifestsMatchReference()
    {
        foreach (var expected in ReferenceVectors.Manifests)
        {
            var manifest = new AssetManifest(expected.Raw);

            Assert.AreEqual(expected.Count, manifest.Count, expected.Label + ": count");
            Assert.AreEqual(expected.BigEndian, manifest.BigEndian, expected.Label + ": byte order");
            Assert.AreEqual(expected.SortedFraction, manifest.SortedFraction, 1e-12, expected.Label + ": sorted fraction");

            foreach (var (name, found, crc, size, compressed) in expected.Lookups)
            {
                var entry = manifest.Find(name);

                Assert.AreEqual(found, entry.HasValue, $"{expected.Label}: '{name}' present");

                if (!found || entry is null)
                    continue;

                Assert.AreEqual(crc, entry.Value.Crc32, $"{expected.Label}: '{name}' crc");
                Assert.AreEqual(size, entry.Value.Size, $"{expected.Label}: '{name}' size");
                Assert.AreEqual(compressed, entry.Value.Compressed, $"{expected.Label}: '{name}' compressed flag");
            }
        }
    }

    [TestMethod]
    public void RaggedManifestIsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new AssetManifest(new byte[17]));
    }
}
