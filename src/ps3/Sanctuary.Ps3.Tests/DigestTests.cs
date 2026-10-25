using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Install;

namespace Sanctuary.Ps3.Tests;

[TestClass]
public sealed class DigestTests
{
    private static DigestVector Digest => ReferenceVectors.Digest;

    [TestMethod]
    public void UneditedDigestSerialisesToTheSameBytes()
    {
        var tree = DigestTree.Parse(Digest.Body);

        CollectionAssert.AreEqual(Digest.Body, DigestTree.Serialize(tree));
    }

    [TestMethod]
    public void FilesAndDownloadableRecordsMatchReference()
    {
        var tree = DigestTree.Parse(Digest.Body);

        CollectionAssert.AreEqual(Digest.AllFiles, DigestTree.Files(tree).Select(item => item.Path).ToArray());

        var downloadable = DigestTree.Downloadable(tree)
            .Select(item => (
                item.Path,
                DigestTree.GetField(item.File, DigestTree.TagCompressedLength)!.Value,
                DigestTree.GetField(item.File, DigestTree.TagSize)!.Value,
                DigestTree.GetField(item.File, DigestTree.TagCrc32)!.Value))
            .ToArray();

        CollectionAssert.AreEqual(Digest.Downloadables, downloadable);
    }

    [TestMethod]
    public void EditingKeepsWidthsUnlessTheValueNoLongerFits()
    {
        var tree = DigestTree.Parse(Digest.Body);
        var files = DigestTree.Files(tree).ToDictionary(item => item.Path, item => item.File);

        DigestTree.SetField(files["art/small.bin"], DigestTree.TagSize, 70000);
        DigestTree.SetField(files["art/small.bin"], DigestTree.TagCompressedLength, 0x1FFFF);
        DigestTree.SetField(files["art/match.bin"], DigestTree.TagCrc32, 0xFFFFFFFF);

        Assert.IsTrue(DigestTree.SetHeaderNumber(tree, DigestTree.HeaderTotalKiB, 0x123456789));
        Assert.IsFalse(DigestTree.SetHeaderNumber(tree, 99, 1));

        CollectionAssert.AreEqual(Digest.EditedBody, DigestTree.Serialize(tree));
    }

    [TestMethod]
    public void EditingAMissingFieldFails()
    {
        var tree = DigestTree.Parse(Digest.Body);
        var info = DigestTree.Files(tree).Single(item => item.Path == "text/info.txt").File;

        Assert.ThrowsExactly<System.Collections.Generic.KeyNotFoundException>(() => DigestTree.SetField(info, DigestTree.TagSize, 1));
    }

    [TestMethod]
    public void TruncatedDigestIsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => DigestTree.Parse(Digest.Body[..^1]));
    }

    [TestMethod]
    public void AdaptingToTheInstalledFilesMatchesReference()
    {
        using var install = new TempInstall(Digest.LocalFiles);

        var (body, changes) = DigestEditor.AdaptToLocal(Digest.Body, install.Root);

        var actual = changes
            .Select(change => (change.Path, change.OldSize, change.NewSize, change.OldCrc32, change.NewCrc32, change.OldLength, change.NewLength))
            .ToArray();

        CollectionAssert.AreEqual(Digest.Changes, actual);
        CollectionAssert.AreEqual(Digest.AdaptedBody, body);
    }

    [TestMethod]
    public void AdaptingACompressibleFileDescribesItsOwnContainerLength()
    {
        var text = new byte[4000];

        for (var index = 0; index < text.Length; index++)
            text[index] = (byte)"free realms "[index % 12];

        using var install = new TempInstall([("art/changed.bin", text)]);

        var (body, changes) = DigestEditor.AdaptToLocal(Digest.Body, install.Root);

        var change = changes.Single();

        Assert.AreEqual("art/changed.bin", change.Path);
        Assert.AreEqual((ulong)text.Length, (ulong)change.NewSize);
        Assert.AreEqual((ulong)(ZsContainer.PayloadLength(text) + ZsContainer.TrailerLength), change.NewLength);
        Assert.IsTrue(change.NewLength < (ulong)text.Length, "repetitive text must compress");

        var record = DigestTree.Files(DigestTree.Parse(body)).Single(item => item.Path == "art/changed.bin").File;

        Assert.AreEqual(change.NewLength, DigestTree.GetField(record, DigestTree.TagCompressedLength));
        Assert.AreEqual((ulong)change.NewCrc32, DigestTree.GetField(record, DigestTree.TagCrc32));
    }

    [TestMethod]
    public void LineEndingsFollowTheReplaceRules()
    {
        foreach (var (input, expected) in ReferenceVectors.Crlf)
            CollectionAssert.AreEqual(expected, DigestEditor.ToCrlf(input), BitConverter.ToString(input));
    }

    [TestMethod]
    public void DigestFileSplitsAndPacks()
    {
        var trailer = new byte[ZsContainer.TrailerLength];

        for (var index = 0; index < trailer.Length; index++)
            trailer[index] = (byte)(index * 3);

        var blob = DigestFile.Pack(Digest.Body, trailer);

        var (body, split) = DigestFile.Split(blob);

        CollectionAssert.AreEqual(Digest.Body, body);
        CollectionAssert.AreEqual(trailer, split);
    }

    [TestMethod]
    public void DigestFileRejectsGarbage()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => DigestFile.Split(new byte[10]));
        Assert.ThrowsExactly<InvalidDataException>(() => DigestFile.Split(new byte[300]));
    }
}
