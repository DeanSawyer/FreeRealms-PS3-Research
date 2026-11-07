using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;

namespace Sanctuary.Ps3.Tests;

/// <summary>The checks of tools/check_sanctuary_flow.py that the conversation vectors do not cover.</summary>
[TestClass]
public sealed class SanctuaryFlowTests
{
    [TestMethod]
    public void HalloweenAreasAreOnInNovemberAndOffInDecember()
    {
        Assert.AreEqual(14, SanctuaryFlow.ClientAreas(new DateOnly(2026, 11, 6)).Count);
        Assert.AreEqual(1, SanctuaryFlow.ClientAreas(new DateOnly(2026, 12, 1)).Count);
        Assert.AreEqual(14, SanctuaryFlow.ClientAreas(new DateOnly(2026, 9, 20)).Count, "the first day counts");
        Assert.AreEqual(14, SanctuaryFlow.ClientAreas(new DateOnly(2026, 11, 11)).Count, "the last day counts");
        Assert.AreEqual(1, SanctuaryFlow.ClientAreas(new DateOnly(2026, 11, 12)).Count);
    }

    [TestMethod]
    public void TheNearestAreaIsFoundLikeSanctuaryDoes()
    {
        Assert.AreEqual(5, SanctuaryFlow.NearestArea(-1414.636, 351.567).Id, "the Sanctuary POI lies in the Sanctuary area");
        Assert.AreEqual(8, SanctuaryFlow.NearestArea(51.0, 363.0).Id, "next to Snowhill the nearest area is Snowhill");
    }

    [TestMethod]
    public void TheGameClockRunsEightTimesAsFast()
    {
        var packet = SanctuaryFlow.GameTimeSync(SanctuaryFlow.GameTimeEpoch + 10);

        CollectionAssert.AreEqual(new PacketWriter().U16(52).I64(80).I32(8).U8(0).ToArray(), packet);
    }

    [TestMethod]
    public void SkipDropsByNameAndByFirstWord()
    {
        var packets = new List<NamedPacket> { new NamedPacket("Mana", [1]), new NamedPacket("SetClientArea 4173", [2]), new NamedPacket("SetClientArea 3992", [3]), new NamedPacket("Hitpoints", [4]) };

        var kept = SanctuaryFlow.Select(packets, GatewayOptions.ParseSkip("mana, setclientarea"));

        CollectionAssert.AreEqual(new[] { "Hitpoints" }, kept.Select(packet => packet.Name).ToArray());
    }

    [TestMethod]
    public void TheReferenceTablesAreRead()
    {
        Assert.AreEqual(184, ReferenceData.ItemClasses.Count);
        Assert.AreEqual(71, ReferenceData.ItemCategories.Count);
        Assert.AreEqual(74, ReferenceData.ItemCategoryGroups.Count);
        Assert.AreEqual(8 + 184 * 28, ReferenceData.ItemClassDefinitions().Length);
        Assert.AreEqual(8 + 71 * 21 + 4 + 74 * 12, ReferenceData.ItemCategoryDefinitions().Length);
    }

    [TestMethod]
    public void AJobSwitchCarriesTheJobsProfileEntry()
    {
        var inventory = new Inventory(new Dictionary<string, string>(), new GatewayOptions());

        var replies = inventory.SetProfile(35);

        CollectionAssert.AreEqual(new[] { "SlotCompositeEffectOverride (clear)", "ActivateProfile 35", "AbilitySetDefinition" }, replies.Select(reply => reply.Name).ToArray());
        Assert.AreEqual(35, inventory.Active);
        Assert.AreEqual(0, inventory.SetProfile(4242).Count, "a job the character lacks does nothing");
        Assert.IsNull(inventory.Handle([26, 0, 32, 0, 5, 0, 0, 0]), "other command packets are not inventory requests");
    }
}
