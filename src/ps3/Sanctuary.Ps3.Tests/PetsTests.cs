using System;
using System.Buffers.Binary;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The starter pets, the Pet (53) packets as the PS3 reads them and a pet following its character.</summary>
[TestClass]
public sealed class PetsTests
{
    private static readonly PetDef Bare = new(1, string.Empty, 0, 0, 1, "dog.adr", 1, 0, 0, 0, string.Empty, string.Empty, false, false, [], []);

    [TestMethod]
    public void TheStarterPetsAreLoaded()
    {
        Assert.AreEqual(8, Pets.Starters.Count);
        Assert.IsTrue(Pets.Starters.All(pet => pet.Name.Length > 0 && pet.ModelFile.EndsWith(".adr", StringComparison.Ordinal) && pet.Tricks.Count > 0));
    }

    [TestMethod]
    public void ThePetListIsAsLongAsThePs3Reads()
    {
        // 0xa88634 read 107 bytes for a pet with no tricks, items or classes and empty strings; the list adds its header, count and
        // the active pet (tools/ppc_trace.py on the Pet handler 0x6949c).
        Assert.AreEqual(3 + 4 + 107 + 12, Pets.List([Bare], 0, 0).Length);

        // A trick is 37 bytes with its key, an item class 12.
        var trained = Bare with { Tricks = [new PetTrick(11, 1, 2, 1, 0, 0, 0, 2, true)], ItemClasses = [(31, 31, 5)] };
        Assert.AreEqual(3 + 4 + 107 + 37 + 12 + 12, Pets.List([trained], 0, 0).Length);

        var list = Pets.List(Pets.Starters, 2, Pets.GuidBase + 2);
        Assert.AreEqual(Pets.Opcode, BinaryPrimitives.ReadUInt16LittleEndian(list));
        Assert.AreEqual(Pets.ListSub, list[2]);
        Assert.AreEqual((uint)Pets.Starters.Count, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(3)));
        Assert.AreEqual(Pets.GuidBase + 2, BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(list.Length - 8)));
    }

    [TestMethod]
    public void SummonAndDismissAreRead()
    {
        Assert.AreEqual((2, true), Pets.FromSummonRecall([53, 0, 4, 2, 0, 0, 0, 1]));
        Assert.AreEqual((2, false), Pets.FromSummonRecall([53, 0, 4, 2, 0, 0, 0, 0]));
        Assert.IsNull(Pets.FromSummonRecall([53, 0, 5, 2, 0, 0, 0, 1]));
    }

    [TestMethod]
    public void APositionUpdateIsFortyBytes()
    {
        var update = Pets.Position(Pets.GuidBase + 1, 1, 2, 3, 0, true, 6);

        // 0xcde04 reads 40 bytes, as the PS3 sends its own.
        Assert.AreEqual(40, update.Length);
        Assert.AreEqual(125, BinaryPrimitives.ReadUInt16LittleEndian(update));
        Assert.AreEqual(0, update[34], "moving");
        Assert.AreEqual(6f, BinaryPrimitives.ReadSingleLittleEndian(update.AsSpan(36)));
    }

    [TestMethod]
    public void ASummonedPetFollowsItsCharacter()
    {
        var keeper = new PetKeeper(Pets.Starters);
        var start = new Placement(100, 5, 100, 0, 0, 1);

        var summoned = keeper.Summon(1, start);
        CollectionAssert.AreEqual(new[] { "AddNpc pet 1", $"PetList {Pets.Starters.Count}" }, summoned.Select(packet => packet.Name).ToList());
        Assert.AreEqual(1, keeper.Active);

        // Behind the character (facing +z) and to its side.
        var spawn = summoned[0].Inner;
        Assert.AreEqual(Pets.GuidBase + 1, BinaryPrimitives.ReadUInt64LittleEndian(spawn.AsSpan(4)));
        Assert.IsTrue(BinaryPrimitives.ReadSingleLittleEndian(spawn.AsSpan(4 + 8 + 4 + 4 + 8 + 12 + 4 + 8)) < 100, "z behind");
        Assert.AreEqual(Pets.MovementType, BinaryPrimitives.ReadInt32LittleEndian(spawn.AsSpan(spawn.Length - 4)), "moved by position updates");

        var now = TimeSpan.FromSeconds(10);
        Assert.AreEqual(0, keeper.Moved(start with { Z = 100.2 }, 0, now).Count, "hardly moved");

        var walked = keeper.Moved(start with { Z = 105 }, 6, now + TimeSpan.FromSeconds(1));
        Assert.AreEqual("PetPosition", walked.Single().Name);
        Assert.AreEqual(Pets.GuidBase + 1, BinaryPrimitives.ReadUInt64LittleEndian(walked[0].Inner.AsSpan(2)));

        Assert.AreEqual(0, keeper.Moved(start with { Z = 106 }, 6, now + TimeSpan.FromSeconds(1.05)).Count, "too soon after the last one");

        // A warpstone: the pet is put down beside the character again.
        var teleported = keeper.Moved(start with { X = 900 }, 0, now + TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { "RemovePlayer pet 1", "AddNpc pet 1" }, teleported.Select(packet => packet.Name).ToList());

        var dismissed = keeper.Recall();
        CollectionAssert.AreEqual(new[] { "RemovePlayer pet 1", $"PetList {Pets.Starters.Count}" }, dismissed.Select(packet => packet.Name).ToList());
        Assert.AreEqual(0, keeper.Active);
        Assert.AreEqual(0, keeper.Moved(start, 6, now + TimeSpan.FromSeconds(5)).Count);
        Assert.AreEqual(0, keeper.Summon(99, start).Count, "no such pet");
    }
}
