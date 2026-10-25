using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>Runs the gateway code on the inputs of tools/gen_ps3_gateway_vectors.py and compares with what the Python probe sent.</summary>
[TestClass]
public sealed class GatewayVectorTests
{
    private sealed class Capture : IGatewayTransport
    {
        public List<(int Port, byte[] Data)> Sent { get; } = [];

        public void Send(IPEndPoint peer, byte[] datagram) => Sent.Add((peer.Port, datagram));
    }

    private static GatewayOptions Options((string Name, string Value)[] env) =>
        GatewayOptions.FromEnvironment(name => env.Where(item => item.Name == name).Select(item => item.Value).FirstOrDefault());

    private static GatewayWorld World((string Name, string Value)[] env, string? zone, bool hasSpawn, double x, double z)
    {
        (double X, double Z)? spawn = hasSpawn ? (x, z) : null;

        return new GatewayWorld(Options(env), zone, spawn);
    }

    private static Dictionary<string, string> Character((string Name, string Value)[] values) => values.ToDictionary(item => item.Name, item => item.Value);

    private static void AssertSame(byte[] expected, byte[] actual, string message) => CollectionAssert.AreEqual(expected, actual, message);

    private static void AssertAllSame(byte[][] expected, IReadOnlyList<byte[]> actual, string message)
    {
        Assert.AreEqual(expected.Length, actual.Count, message + ": count");

        for (var index = 0; index < expected.Length; index++)
            CollectionAssert.AreEqual(expected[index], actual[index], $"{message}: packet {index}");
    }

    [TestMethod]
    public void ZoneTypeAndSpawnFollowTheSwitchesLikeThePythonProbe()
    {
        foreach (var scenario in GatewayVectors.Packets)
        {
            var world = World(scenario.Env, scenario.InstallZone, scenario.HasInstallSpawn, scenario.InstallSpawnX, scenario.InstallSpawnZ);

            Assert.AreEqual(scenario.Zone, world.Zone, scenario.Label);
            Assert.AreEqual(scenario.ZoneType, world.ZoneType, scenario.Label);

            var spawn = world.Spawn;

            CollectionAssert.AreEqual(scenario.Spawn, new[] { spawn.X, spawn.Y, spawn.Z, spawn.W }, scenario.Label);
        }
    }

    [TestMethod]
    public void PacketBuildersMatchReference()
    {
        foreach (var scenario in GatewayVectors.Packets)
        {
            var world = World(scenario.Env, scenario.InstallZone, scenario.HasInstallSpawn, scenario.InstallSpawnX, scenario.InstallSpawnZ);
            var character = Character(scenario.Character);
            var label = scenario.Label;

            AssertSame(scenario.ZoneDetails, Ps3Packets.ZoneDetails(world.Zone, world.ZoneType), label + ": ZoneDetails");
            AssertSame(scenario.PlayerBlob, Ps3Packets.PlayerBlob(character, world, GatewayVectors.UnixTime), label + ": player blob");
            AssertSame(scenario.SendSelf, Ps3Packets.SendSelfToClient(character, world, GatewayVectors.UnixTime), label + ": SendSelfToClient");
            AssertSame(scenario.AddPcOther, Ps3Packets.AddPc(character, world), label + ": AddPc (guid 2)");

            var first = character.TryGetValue("FirstName", out var name) && name.Length > 0 ? name : "Test";
            var last = character.TryGetValue("LastName", out var surname) ? surname : string.Empty;

            AssertSame(scenario.AddPcSelf, Ps3Packets.AddPc(character, world, 1, first, last, (0, 0, 0)), label + ": AddPc (guid 1)");
            AssertAllSame(scenario.WorldPackets, Ps3Packets.WorldPackets(character, world, GatewayVectors.UnixTime), label + ": world packets");
            AssertAllSame(scenario.ZoneDone, Ps3Packets.ZoneDonePackets(world.Options, GatewayVectors.UnixTime), label + ": zone done");
            AssertSame(scenario.TimeSync, Ps3Packets.GameTimeSync(GatewayVectors.UnixTime), label + ": game time");
        }
    }

    [TestMethod]
    public void PacketSizesForAnEmptyCharacterAreStable()
    {
        var world = new GatewayWorld();
        var empty = new Dictionary<string, string>();

        // Too big for one 512 byte datagram, which is why the reliable channel fragments (see Ps3Gateway.SendReliable).
        Assert.AreEqual(534, Ps3Packets.PlayerBlob(empty, world, GatewayVectors.UnixTime).Length);
        Assert.AreEqual(540, Ps3Packets.SendSelfToClient(empty, world, GatewayVectors.UnixTime).Length);
        Assert.AreEqual(261, Ps3Packets.AddPc(empty, world).Length);
    }

    [TestMethod]
    public void FixedPacketsMatchReference()
    {
        var fixedPackets = GatewayVectors.Fixed;

        AssertSame(fixedPackets.Tunneled, Ps3Packets.Tunneled([1, 2, 3]), "tunneled");
        AssertSame(fixedPackets.TunneledUnreliable, Ps3Packets.Tunneled([1, 2, 3], reliable: false), "tunneled unreliable");
        AssertSame(fixedPackets.TunneledEmpty, Ps3Packets.Tunneled([]), "tunneled empty");
        AssertSame(fixedPackets.Lobby, Ps3Packets.LobbyDefinitionsReply(), "lobby definitions");
        AssertSame(fixedPackets.Queues, Ps3Packets.ListQueuesReply(), "queues");
        AssertSame(fixedPackets.Profile, Ps3Packets.Profile(1), "profile");
        AssertSame(fixedPackets.ZoneDetailsArgs, Ps3Packets.ZoneDetails("Z", 3, 1, 2, "sky", 1, 9), "zone details with arguments");
        AssertSame([2, 0, 1], Ps3Packets.LoginReply(), "login reply");
        AssertSame([2, 0, 0], Ps3Packets.LoginReply(success: false), "failed login reply");
    }

    [TestMethod]
    public void LoginPacketsAreReadLikeThePythonProbeReadsThem()
    {
        foreach (var vector in GatewayVectors.Logins)
        {
            var parsed = Ps3Packets.ParseLogin(vector.Payload);

            if (!vector.Valid)
            {
                Assert.IsNull(parsed, vector.Label);

                continue;
            }

            Assert.IsNotNull(parsed, vector.Label);
            Assert.AreEqual(vector.Ticket, parsed.Ticket, vector.Label);
            Assert.AreEqual(vector.Guid, parsed.Guid, vector.Label);
            Assert.AreEqual(vector.Version, parsed.Version, vector.Label);
            Assert.AreEqual(vector.Unknown, parsed.Unknown, vector.Label);
        }
    }

    [TestMethod]
    public void ClientPacketsAreDescribedLikeThePythonProbeDescribesThem()
    {
        foreach (var vector in GatewayVectors.Descriptions)
            Assert.AreEqual(vector.Text, Ps3Packets.DescribeInner(vector.Opcode, vector.Inner), $"opcode {vector.Opcode}: {BitConverter.ToString(vector.Inner)}");
    }

    [TestMethod]
    public void WholeConversationsMatchReference()
    {
        foreach (var vector in GatewayVectors.Conversations)
        {
            var capture = new Capture();
            var characters = new CharacterStore();

            characters.Replace(vector.Character);

            var gateway = new Ps3Gateway(
                capture,
                World(vector.Env, vector.InstallZone, vector.HasInstallSpawn, vector.InstallSpawnX, vector.InstallSpawnZ),
                characters,
                TraceLog.None(),
                null,
                () => GatewayVectors.UnixTime,
                () => GatewayVectors.EncryptCode);

            var now = TimeSpan.Zero;

            for (var index = 0; index < vector.Steps.Length; index++)
            {
                var step = vector.Steps[index];
                var label = $"{vector.Label}, step {index}";

                capture.Sent.Clear();

                if (step.Receive)
                {
                    gateway.Receive(step.Data, new IPEndPoint(IPAddress.Loopback, step.Port), now);
                }
                else
                {
                    now += TimeSpan.FromSeconds(step.Seconds);
                    gateway.RunScheduled(now);
                }

                Assert.AreEqual(step.Sent.Length, capture.Sent.Count, label + ": number of datagrams");

                for (var sent = 0; sent < step.Sent.Length; sent++)
                {
                    Assert.AreEqual(step.Sent[sent].Port, capture.Sent[sent].Port, $"{label}, datagram {sent}: peer");
                    CollectionAssert.AreEqual(step.Sent[sent].Data, capture.Sent[sent].Data, $"{label}, datagram {sent}");
                }
            }
        }
    }

    [TestMethod]
    public void EnvironmentSwitchesAreReadLikeThePython()
    {
        string? Get(Dictionary<string, string> values, string name) => values.GetValueOrDefault(name);

        var defaults = GatewayOptions.FromEnvironment(name => Get(new Dictionary<string, string>(), name));

        Assert.IsNull(defaults.Zone);
        Assert.AreEqual(2, defaults.ZoneType);
        Assert.IsNull(defaults.Spawn);
        Assert.AreEqual(400.0, defaults.SpawnY);
        Assert.AreEqual(AddPcMode.Other, defaults.AddPc);
        Assert.IsTrue(defaults.Welcome);
        Assert.IsFalse(defaults.Extra);
        Assert.IsTrue(defaults.TimeSync);

        var set = new Dictionary<string, string>
        {
            ["FR_ZONE"] = " Somewhere ",
            ["FR_ZONE_TYPE"] = " 3 ",
            ["FR_SPAWN"] = "1.5; 2 ,-3",
            ["FR_SPAWN_Y"] = "99.5",
            ["FR_ADDPC"] = " BOTH ",
            ["FR_WELCOME"] = "0",
            ["FR_EXTRA"] = " 1 ",
            ["FR_TIME"] = "0",
        };

        var options = GatewayOptions.FromEnvironment(name => Get(set, name));

        Assert.AreEqual("Somewhere", options.Zone);
        Assert.AreEqual(3, options.ZoneType);
        Assert.AreEqual(1.5, options.Spawn!.Value.X);
        Assert.AreEqual(2.0, options.Spawn.Value.Y);
        Assert.AreEqual(-3.0, options.Spawn.Value.Z);
        Assert.AreEqual(99.5, options.SpawnY);
        Assert.AreEqual(AddPcMode.Both, options.AddPc);
        Assert.IsFalse(options.Welcome);
        Assert.IsTrue(options.Extra);
        Assert.IsFalse(options.TimeSync);

        var warnings = new List<string>();
        var bad = GatewayOptions.FromEnvironment(name => Get(new() { ["FR_SPAWN"] = "a,b,c", ["FR_ZONE_TYPE"] = "x", ["FR_ADDPC"] = "" }, name), warnings.Add);

        Assert.IsNull(bad.Spawn);
        Assert.AreEqual(2, bad.ZoneType);
        Assert.AreEqual(AddPcMode.None, bad.AddPc);
        Assert.AreEqual(1, warnings.Count);
        Assert.IsTrue(warnings[0].Contains("FR_SPAWN", StringComparison.Ordinal));
    }
}
