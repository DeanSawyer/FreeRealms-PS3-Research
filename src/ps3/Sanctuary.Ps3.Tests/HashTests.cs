using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Tests;

[TestClass]
public sealed class HashTests
{
    [TestMethod]
    public void NameHashMatchesReference()
    {
        foreach (var (name, hash) in ReferenceVectors.NameHashes)
            Assert.AreEqual(hash, NameHash.Compute(name), $"name hash of '{name}'");
    }

    [TestMethod]
    public void NameHashFoldsOnlyAsciiLetters()
    {
        Assert.AreEqual(NameHash.Compute("ABC"), NameHash.Compute("abc"));
        Assert.AreNotEqual(NameHash.Compute("{"), NameHash.Compute("["), "'{' must not fold to '['");
    }

    [TestMethod]
    public void NameHashRejectsNonLatinNames()
    {
        Assert.ThrowsExactly<ArgumentException>(() => NameHash.Compute("Ā"));
    }

    [TestMethod]
    public void Crc32MatchesReference()
    {
        foreach (var (data, crc) in ReferenceVectors.Crc32s)
            Assert.AreEqual(crc, Crc32.Compute(data), $"crc32 of {data.Length} bytes");
    }
}
