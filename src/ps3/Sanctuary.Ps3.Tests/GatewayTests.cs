using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The reliable channel, keep-alives and resends of <see cref="Ps3Gateway"/>, on a virtual clock.</summary>
[TestClass]
public sealed class GatewayTests
{
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 50123);

    private sealed class Capture : IGatewayTransport
    {
        public List<byte[]> Sent { get; } = [];

        public void Send(IPEndPoint peer, byte[] datagram) => Sent.Add(datagram);
    }

    private static (Ps3Gateway Gateway, Capture Capture) Make(GatewayOptions? options = null, TraceLog? trace = null)
    {
        var capture = new Capture();

        return (new Ps3Gateway(capture, new GatewayWorld(options), new CharacterStore(), trace ?? TraceLog.None(), null, () => GatewayVectors.UnixTime, () => 0x01020304), capture);
    }

    private static byte[] Connect(int maxRaw = 512, int version = 3, int code = 0x11223344)
    {
        var packet = new byte[14 + 10];

        packet[1] = 1;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(2), version);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(6), code);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(10), maxRaw);
        "CGAPI_490"u8.CopyTo(packet.AsSpan(14));

        return packet;
    }

    private static byte[] Reliable(int channel, int sequence, byte[] payload)
    {
        var packet = new byte[4 + payload.Length];

        packet[1] = (byte)(9 + channel);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)sequence);
        payload.CopyTo(packet, 4);

        return packet;
    }

    private static byte[] Login() =>
        new PacketWriter().U16(1).Str("0").U64(1).Str("1.77").Str("name").ToArray();

    [TestMethod]
    public void ConnectIsAnsweredWithAConfirmThatEchoesTheCodeAndSize()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(maxRaw: 300), Client, TimeSpan.Zero);

        var confirm = capture.Sent.Single();

        Assert.AreEqual(21, confirm.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 2 }, confirm[..2]);
        Assert.AreEqual(0x11223344, BinaryPrimitives.ReadInt32BigEndian(confirm.AsSpan(2)));
        Assert.AreEqual(0x01020304, BinaryPrimitives.ReadInt32BigEndian(confirm.AsSpan(6)));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, confirm[10..13]); // no CRC bytes, no encryption methods
        Assert.AreEqual(300, BinaryPrimitives.ReadInt32BigEndian(confirm.AsSpan(13)));
        Assert.AreEqual(3, BinaryPrimitives.ReadInt32BigEndian(confirm.AsSpan(17)));
        CollectionAssert.AreEqual(new[] { Client }, gateway.Sessions.ToArray());
        Assert.AreEqual(1, gateway.PacketCount);
    }

    [TestMethod]
    public void EveryDatagramFitsTheNegotiatedSizeAndTheFragmentsReassemble()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(maxRaw: 512), Client, TimeSpan.Zero);
        capture.Sent.Clear();
        gateway.Receive(Reliable(0, 0, Login()), Client, TimeSpan.Zero);

        Assert.IsTrue(capture.Sent.All(datagram => datagram.Length <= 512), "a datagram is larger than the client reads");

        var fragments = capture.Sent.Where(datagram => datagram[1] == 13).ToList();

        Assert.IsTrue(fragments.Count >= 2, "the player data does not fit one datagram and must be fragmented");

        // First fragment: header, 4 byte big endian total length, data. The rest: header, data. Sequences count up.
        var total = (int)BinaryPrimitives.ReadUInt32BigEndian(fragments[0].AsSpan(4));
        var payload = fragments.SelectMany((datagram, index) => datagram.Skip(index == 0 ? 8 : 4)).ToArray();

        Assert.AreEqual(total, payload.Length);

        var sequences = fragments.Select(datagram => (int)BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(2))).ToList();

        CollectionAssert.AreEqual(Enumerable.Range(sequences[0], sequences.Count).ToArray(), sequences);

        // The reassembled payload is the tunneled SendSelfToClient packet.
        var expected = Ps3Packets.Tunneled(Ps3Packets.SendSelfToClient(new Dictionary<string, string>(), new GatewayWorld(), GatewayVectors.UnixTime));

        CollectionAssert.AreEqual(expected, payload);
    }

    [TestMethod]
    public void ASmallerMaxRawMeansSmallerFragments()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(maxRaw: 200), Client, TimeSpan.Zero);
        capture.Sent.Clear();
        gateway.Receive(Reliable(0, 0, Login()), Client, TimeSpan.Zero);

        Assert.IsTrue(capture.Sent.All(datagram => datagram.Length <= 200));
    }

    [TestMethod]
    public void AcknowledgedPacketsAreNotSentAgain()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(), Client, TimeSpan.Zero);
        gateway.Receive(Reliable(0, 0, Login()), Client, TimeSpan.Zero);

        // LoginReply (0), ZoneDetails (1) and the player data in two fragments (2 and 3).
        Assert.AreEqual(4, gateway.PendingCount);

        // An Ack removes one sequence; an AckAll removes everything up to and including its sequence.
        gateway.Receive([0, 0x11, 0, 1], Client, TimeSpan.Zero);

        Assert.AreEqual(3, gateway.PendingCount);

        gateway.Receive([0, 0x15, 0, 2], Client, TimeSpan.Zero);

        Assert.AreEqual(1, gateway.PendingCount);

        // An ack for another channel or another peer does nothing.
        gateway.Receive([0, 0x12, 0, 3], Client, TimeSpan.Zero);
        gateway.Receive([0, 0x11, 0, 3], new IPEndPoint(IPAddress.Loopback, 1), TimeSpan.Zero);

        Assert.AreEqual(1, gateway.PendingCount);

        capture.Sent.Clear();
        gateway.Maintain(TimeSpan.FromSeconds(1.5));

        // Only the still pending packet (the second fragment, sequence 3) is repeated.
        var again = capture.Sent.Single(datagram => datagram[1] is 9 or 13);

        Assert.AreEqual(13, again[1]);
        Assert.AreEqual(3, BinaryPrimitives.ReadUInt16BigEndian(again.AsSpan(2)));
    }

    [TestMethod]
    public void UnacknowledgedPacketsAreResentOnceASecondAndGivenUpAfterEightTries()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(), Client, TimeSpan.Zero);
        gateway.Receive(Reliable(0, 0, Login()), Client, TimeSpan.Zero);

        var pending = gateway.PendingCount;

        Assert.AreEqual(4, pending);

        capture.Sent.Clear();
        gateway.Maintain(TimeSpan.FromSeconds(0.5));

        Assert.AreEqual(0, capture.Sent.Count, "nothing is due after half a second");

        for (var second = 1; second <= 7; second++)
        {
            capture.Sent.Clear();
            gateway.Maintain(TimeSpan.FromSeconds(second));

            Assert.AreEqual(pending, capture.Sent.Count(datagram => datagram[1] is 9 or 13), $"resend {second}");
        }

        // The eighth try was the last: at 8 seconds the packets are dropped without being sent again.
        capture.Sent.Clear();
        gateway.Maintain(TimeSpan.FromSeconds(8));

        Assert.AreEqual(0, capture.Sent.Count(datagram => datagram[1] is 9 or 13));
        Assert.AreEqual(0, gateway.PendingCount);
    }

    [TestMethod]
    public void SessionsGetAKeepAliveEveryFiveSecondsUntilTheyTerminate()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(), Client, TimeSpan.Zero);
        capture.Sent.Clear();

        gateway.Maintain(TimeSpan.FromSeconds(1));
        gateway.Maintain(TimeSpan.FromSeconds(4.9));

        Assert.AreEqual(0, capture.Sent.Count);

        gateway.Maintain(TimeSpan.FromSeconds(6));

        CollectionAssert.AreEqual(new byte[] { 0, 6 }, capture.Sent.Single());

        gateway.Maintain(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, capture.Sent.Count, "the next one is five seconds after the last");

        gateway.Maintain(TimeSpan.FromSeconds(11.1));
        Assert.AreEqual(2, capture.Sent.Count);

        gateway.Receive([0, 5, 0, 0, 0, 0, 0, 3], Client, TimeSpan.FromSeconds(12));
        capture.Sent.Clear();
        gateway.Maintain(TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, capture.Sent.Count);
        Assert.AreEqual(0, gateway.Sessions.Count);
    }

    [TestMethod]
    public void TheSequenceNumberWrapsAfter65535()
    {
        var (gateway, capture) = Make();

        gateway.Receive(Connect(), Client, TimeSpan.Zero);
        capture.Sent.Clear();

        // Every question about the lobby is answered with one reliable packet.
        var request = Reliable(0, 0, new PacketWriter().U16(6).U8(1).U32(4).U16(102).U16(1).ToArray());

        for (var count = 0; count < 65540; count++)
        {
            gateway.Receive(request, Client, TimeSpan.Zero);
            gateway.Receive([0, 0x15, 0xFF, 0xFF], Client, TimeSpan.Zero);
        }

        var replies = capture.Sent.Where(datagram => datagram[1] == 9 && datagram.Length > 4 + 7).Select(datagram => (int)BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(2))).ToList();

        Assert.AreEqual(65540, replies.Count);
        Assert.AreEqual(65535, replies[65535]);
        Assert.AreEqual(0, replies[65536]);
        Assert.AreEqual(3, replies[65539]);
    }

    [TestMethod]
    public void TheWorldReadyFallbackOnlyFiresWithoutClientIsReady()
    {
        var (gateway, capture) = Make();
        var ready = new IPEndPoint(IPAddress.Loopback, 50200);

        foreach (var peer in new[] { Client, ready })
        {
            gateway.Receive(Connect(), peer, TimeSpan.Zero);
            gateway.Receive(Reliable(0, 0, Login()), peer, TimeSpan.Zero);
        }

        // ClientIsReady (13) from one of them.
        gateway.Receive(Reliable(0, 1, new PacketWriter().U16(6).U8(1).U32(2).U16(13).ToArray()), ready, TimeSpan.FromSeconds(1));

        capture.Sent.Clear();
        gateway.RunScheduled(TimeSpan.FromSeconds(19.9));

        Assert.AreEqual(0, capture.Sent.Count);

        gateway.RunScheduled(TimeSpan.FromSeconds(20.1));

        Assert.AreEqual(Ps3Packets.ZoneDonePackets(new GatewayOptions(), GatewayVectors.UnixTime).Count, capture.Sent.Count);

        gateway.RunScheduled(TimeSpan.FromSeconds(60));

        Assert.AreEqual(Ps3Packets.ZoneDonePackets(new GatewayOptions(), GatewayVectors.UnixTime).Count, capture.Sent.Count, "it fires once");
    }

    [TestMethod]
    public void MalformedPacketsAreRecordedAndNeverThrow()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gateway-" + Guid.NewGuid().ToString("N") + ".jsonl");

        try
        {
            var (gateway, capture) = Make(trace: new TraceLog(path));

            foreach (var junk in new byte[][]
            {
                [], [0], [1, 2, 3], [0, 1], [0, 1, 0, 0], [0, 9], [0, 9, 0], [0, 13], [0, 0x11], [0, 3, 0xFF], [0, 3, 5, 1, 2], [0, 5], [0, 30, 1, 2, 3],
                [0, 0xFF, 1, 2, 3, 4],
            })
            {
                gateway.Receive(junk, Client, TimeSpan.Zero);
            }

            Assert.AreEqual(0, capture.Sent.Count(datagram => datagram.Length > 4 && datagram[1] == 2), "no Confirm without a full Connect");

            var lines = System.IO.File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement).ToList();

            Assert.IsTrue(lines.All(line => line.GetProperty("time_utc").GetString()!.EndsWith("+00:00", StringComparison.Ordinal)));
            Assert.IsTrue(lines.Any(line => line.GetProperty("event").GetString() == "packet" && line.GetProperty("label").GetString() == "raw"));
            Assert.IsTrue(lines.Any(line => line.GetProperty("event").GetString() == "terminate"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [TestMethod]
    public void TheConnectAndLoginAreTracedForTheTestHarness()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gateway-" + Guid.NewGuid().ToString("N") + ".jsonl");

        try
        {
            var (gateway, _) = Make(trace: new TraceLog(path));

            gateway.Receive(Connect(), Client, TimeSpan.Zero);
            gateway.Receive(Reliable(0, 0, Login()), Client, TimeSpan.Zero);

            var lines = System.IO.File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement).ToList();

            var connect = lines.Single(line => line.GetProperty("event").GetString() == "connect");

            Assert.AreEqual("CGAPI_490", connect.GetProperty("name").GetString());
            Assert.AreEqual(512, connect.GetProperty("max_raw").GetInt32());
            Assert.AreEqual(3, connect.GetProperty("version").GetInt32());
            Assert.AreEqual("127.0.0.1", connect.GetProperty("peer").GetString());

            var login = lines.Single(line => line.GetProperty("event").GetString() == "login");

            Assert.AreEqual("0", login.GetProperty("ticket").GetString());
            Assert.AreEqual(1, login.GetProperty("guid").GetInt32());

            Assert.AreEqual(4, lines.Count(line => line.GetProperty("event").GetString() == "send"));
            Assert.IsTrue(lines.Any(line => line.GetProperty("event").GetString() == "packet" && line.GetProperty("label").GetString() == "Reliable1 seq=0"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [TestMethod]
    public void TheUdpHostCompletesTheHandshakeOverARealSocket()
    {
        using var host = new UdpGatewayHost(IPAddress.Loopback, 0);

        var gateway = new Ps3Gateway(host, new GatewayWorld(), new CharacterStore(), TraceLog.None());

        host.Start(gateway);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) { Client = { ReceiveTimeout = 5000 } };
        var server = new IPEndPoint(IPAddress.Loopback, host.Port);

        client.Send(Connect(), 24, server);

        var remote = new IPEndPoint(IPAddress.Any, 0);
        var confirm = client.Receive(ref remote);

        Assert.AreEqual(21, confirm.Length);
        Assert.AreEqual(host.Port, remote.Port);

        var login = Reliable(0, 0, Login());

        client.Send(login, login.Length, server);

        // The ack comes first, then the login reply (Reliable1 seq 0: 02 00 01).
        var ack = client.Receive(ref remote);

        CollectionAssert.AreEqual(new byte[] { 0, 0x11, 0, 0 }, ack);

        var reply = client.Receive(ref remote);

        CollectionAssert.AreEqual(new byte[] { 0, 9, 0, 0, 2, 0, 1 }, reply);
        Assert.IsTrue(SpinWait.SpinUntil(() => gateway.Sessions.Count == 1, 2000));
    }

    [TestMethod]
    public void BytesAreShownLikePythonsRepr()
    {
        Assert.AreEqual("b'Session='", Ps3Packets.PythonBytesRepr("Session="u8));
        Assert.AreEqual("b''", Ps3Packets.PythonBytesRepr([]));
        Assert.AreEqual("b'a\\tb\\n\\r'", Ps3Packets.PythonBytesRepr("a\tb\n\r"u8));
        Assert.AreEqual("b'\\\\'", Ps3Packets.PythonBytesRepr("\\"u8));
        Assert.AreEqual("b'\\x00\\x7f\\xff'", Ps3Packets.PythonBytesRepr([0, 0x7f, 0xff]));
        Assert.AreEqual("b\"it's\"", Ps3Packets.PythonBytesRepr("it's"u8));
        Assert.AreEqual("b'it\\'s\"'", Ps3Packets.PythonBytesRepr("it's\""u8));
    }
}
