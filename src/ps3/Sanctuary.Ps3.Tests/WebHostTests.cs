using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;
using Sanctuary.Ps3.Install;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The web front on a real Kestrel socket (loopback, free port).</summary>
[TestClass]
public sealed class WebHostTests
{
    [TestMethod]
    public async Task ServesConfigRepliesAndFilesOverHttp()
    {
        var raw = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("free realms line\r\n", 100)));
        var plain = Enumerable.Range(0, 5000).Select(value => (byte)(value * 31)).ToArray();

        using var install = new TempInstall(
        [
            ("client/USRDIR/art/notes.txt", raw),
            ("client/USRDIR/Digests/NPUA30048.soe", plain),
        ]);

        var characters = new CharacterStore();
        var digest = new byte[] { 1, 2, 3, 4, 5 };

        var (app, _) = Ps3WebHost.Create(
            "127.0.0.1",
            0,
            loggers => new Ps3WebService(
                new Ps3WebOptions { ClientRoot = Path.Combine(install.Root, "client"), Digest = digest, Advertise = "127.0.0.1" },
                TraceLog.None(loggers.CreateLogger("trace")),
                characters,
                loggers.CreateLogger("web")),
            logging => logging.SetMinimumLevel(LogLevel.Warning));

        await app.StartAsync();

        try
        {
            var port = Ps3WebHost.PortOf(app);

            Assert.IsTrue(port > 0);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };

            // AppConfig
            var config = await client.GetAsync(Ps3Routes.AppConfig);

            Assert.AreEqual(200, (int)config.StatusCode);
            Assert.AreEqual("text/xml; charset=utf-8", config.Content.Headers.ContentType!.ToString());
            Assert.AreEqual("no-cache", config.Headers.CacheControl!.ToString());
            Assert.IsTrue(config.Headers.ConnectionClose ?? false);
            Assert.IsFalse(config.Headers.Contains("Server"));
            CollectionAssert.AreEqual(AppConfigDocument.Build("127.0.0.1"), await config.Content.ReadAsByteArrayAsync());

            // Digest, and HEAD
            CollectionAssert.AreEqual(digest, await client.GetByteArrayAsync(Ps3Routes.DigestRoute));

            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Ps3Routes.DigestRoute));

            Assert.AreEqual(200, (int)head.StatusCode);
            Assert.AreEqual(digest.Length, head.Content.Headers.ContentLength);

            // ps3ws POST
            using var form = new StringContent("Session=&Name=Zo%C3%AB", Encoding.UTF8, "application/x-www-form-urlencoded");
            var created = await client.PostAsync("/ps3ws/character/create.action?request_locale=en_US", form);

            Assert.AreEqual(200, (int)created.StatusCode);
            CollectionAssert.AreEqual(Ps3Replies.Build()[Ps3ReplyKind.CharacterCreate], await created.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("Zoë", characters.Snapshot()["Name"]);

            // A .zs download
            var zs = await client.GetByteArrayAsync($"{Ps3Routes.PatchPrefix}FreeRealms.zs/art/notes.txt.zs?c={Crc32.Compute(raw)}&l={ZsContainer.PayloadLength(raw) + ZsContainer.TrailerLength}");

            CollectionAssert.AreEqual(raw, ZsContainer.Inflate(zs.AsSpan(0, zs.Length - ZsContainer.TrailerLength)));

            // A plain file, streamed from disk
            var file = await client.GetByteArrayAsync($"{Ps3Routes.PatchPrefix}Digests/NPUA30048.soe");

            CollectionAssert.AreEqual(plain, file);

            // Errors
            Assert.AreEqual(404, (int)(await client.GetAsync("/nowhere")).StatusCode);
            Assert.AreEqual(404, (int)(await client.GetAsync($"{Ps3Routes.PatchPrefix}art/none.bin")).StatusCode);
            Assert.AreEqual(409, (int)(await client.GetAsync($"{Ps3Routes.PatchPrefix}FreeRealms.zs/art/notes.txt.zs?c=1&l=9")).StatusCode);
            Assert.AreEqual(501, (int)(await client.PostAsync("/other", new StringContent("x"))).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
