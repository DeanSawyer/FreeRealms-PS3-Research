using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Hosting;
using Sanctuary.Ps3.Install;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The whole server (web front, DNS, UDP game stand-in) started the way <c>Program.cs</c> starts it, on loopback with free ports.</summary>
[TestClass]
public sealed class Ps3ServerTests
{
    private static byte[] Digest()
    {
        var trailer = new byte[ZsContainer.TrailerLength];

        return DigestFile.Pack(ReferenceVectors.Digest.Body, trailer);
    }

    private static Ps3ServerOptions Options(string root, Action<string>? notice = null, bool match = false) => new()
    {
        ClientRoot = Path.Combine(root, "client"),
        HttpPort = 0,
        GatewayPort = 0,
        EnableDns = false,
        MatchLocal = match,
        Notice = notice,
    };

    private static byte[] Query(ushort id, string host)
    {
        var packet = new System.Collections.Generic.List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };

        foreach (var label in host.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }

        packet.AddRange([0, 0, 1, 0, 1]);

        return [.. packet];
    }

    [TestMethod]
    public async Task StartsServesAndStops()
    {
        using var install = new TempInstall([("client/USRDIR/Digests/NPUA30048.soe", Digest())]);

        var traces = Path.Combine(install.Root, "traces");
        var previous = Environment.GetEnvironmentVariable("FR_TRACE_DIR");

        Environment.SetEnvironmentVariable("FR_TRACE_DIR", traces);

        try
        {
            var notices = new System.Collections.Generic.List<string>();

            await using (var server = await Ps3Server.StartAsync(Options(install.Root, notices.Add)))
            {
                Assert.IsTrue(server.HttpPort > 0);
                Assert.IsTrue(server.GatewayPort > 0);
                Assert.IsNull(server.DnsPort);

                // The original digest is 21466 bytes; a synthetic one is not, and the user is told once.
                Assert.AreEqual(1, notices.Count(text => text.StartsWith("WARNING:", StringComparison.Ordinal)));

                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.HttpPort}"), Timeout = TimeSpan.FromSeconds(30) };

                CollectionAssert.AreEqual(AppConfigDocument.Build("127.0.0.1"), await client.GetByteArrayAsync(Ps3Routes.AppConfig));
                CollectionAssert.AreEqual(server.Digest.Digest, await client.GetByteArrayAsync(Ps3Routes.DigestRoute));

                // Character login tells the client to use the UDP port the server really bound.
                using var form = new StringContent("Session=&CharacterId=1", Encoding.UTF8, "application/x-www-form-urlencoded");
                var login = await client.PostAsync("/ps3ws/character/login.action?request_locale=en_US", form);
                var text = await login.Content.ReadAsStringAsync();

                Assert.AreEqual(200, (int)login.StatusCode);
                Assert.IsTrue(text.Contains($"127.0.0.1:{server.GatewayPort}", StringComparison.Ordinal), text);

                // The UDP stand-in answers a Connect with a Confirm.
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) { Client = { ReceiveTimeout = 5000 } };
                var connect = new byte[24];

                connect[1] = 1;
                BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(2), 3);
                BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(6), 0x11223344);
                BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(10), 512);
                "CGAPI_490"u8.CopyTo(connect.AsSpan(14));
                udp.Send(connect, connect.Length, new IPEndPoint(IPAddress.Loopback, server.GatewayPort!.Value));

                var remote = new IPEndPoint(IPAddress.Any, 0);

                Assert.AreEqual(21, udp.Receive(ref remote).Length);
                Assert.IsTrue(SpinWait.SpinUntil(() => server.Gateway!.Sessions.Count == 1, 2000));
            }

            // Both trace files are written where FR_TRACE_DIR points.
            Assert.IsTrue(File.ReadAllText(Path.Combine(traces, "ps3_protocol_trace.jsonl")).Contains("\"event\":\"bootstrap_start\"", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(traces, "ps3_gateway_trace.jsonl")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FR_TRACE_DIR", previous);
        }
    }

    [TestMethod]
    public async Task TheGatewayCanBeTurnedOff()
    {
        using var install = new TempInstall([("client/USRDIR/Digests/NPUA30048.soe", Digest())]);

        var options = Options(install.Root) with { EnableGateway = false };

        await using var server = await Ps3Server.StartAsync(options);

        Assert.IsNull(server.GatewayPort);
        Assert.IsNull(server.Gateway);
    }

    [TestMethod]
    public async Task AMissingDigestIsAnErrorTheUserCanFix()
    {
        using var install = new TempInstall([("client/USRDIR/readme.txt", [1])]);

        var exception = await Assert.ThrowsExactlyAsync<Ps3ServerException>(async () => await Ps3Server.StartAsync(Options(install.Root)));

        Assert.IsTrue(exception.Message.Contains("cannot read the digest", StringComparison.Ordinal), exception.Message);
    }

    [TestMethod]
    public async Task ADigestThatIsNotAZlibStreamIsAnErrorTheUserCanFix()
    {
        using var install = new TempInstall([("client/USRDIR/Digests/NPUA30048.soe", Enumerable.Repeat((byte)7, 300).ToArray())]);

        var exception = await Assert.ThrowsExactlyAsync<Ps3ServerException>(async () => await Ps3Server.StartAsync(Options(install.Root)));

        Assert.IsTrue(exception.Message.Contains("not a valid digest", StringComparison.Ordinal), exception.Message);
    }

    [TestMethod]
    public async Task AnUnknownCodecIsRejected()
    {
        using var install = new TempInstall([("client/USRDIR/Digests/NPUA30048.soe", Digest())]);

        var options = Options(install.Root) with { Codec = "zs-lzma" };

        await Assert.ThrowsExactlyAsync<Ps3ServerException>(async () => await Ps3Server.StartAsync(options));
    }

    [TestMethod]
    public async Task DnsAnswersForTheGameHostWhenEnabled()
    {
        using var install = new TempInstall([("client/USRDIR/Digests/NPUA30048.soe", Digest())]);

        var options = Options(install.Root) with { EnableDns = true, DnsPort = 0, Advertise = IPAddress.Loopback };

        await using var server = await Ps3Server.StartAsync(options);

        Assert.IsTrue(server.DnsPort > 0);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) { Client = { ReceiveTimeout = 5000 } };
        var query = Query(0x1234, "ps3.freerealms.com");

        udp.Send(query, query.Length, new IPEndPoint(IPAddress.Loopback, server.DnsPort!.Value));

        var remote = new IPEndPoint(IPAddress.Any, 0);
        var answer = udp.Receive(ref remote);

        CollectionAssert.AreEqual(new byte[] { 127, 0, 0, 1 }, answer[^4..]);
    }
}
