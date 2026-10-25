using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Assets;
using Sanctuary.Ps3.Install;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The router and the asset service, called directly (no sockets).</summary>
[TestClass]
public sealed class WebServiceTests
{
    private const string Prefix = Ps3Routes.PatchPrefix;

    private static readonly byte[] DigestBytes = [.. Enumerable.Range(0, 300).Select(value => (byte)value)];

    /// <summary>A throw-away install with a trace file, plus the service reading it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly TempInstall _install;

        public Rig(IEnumerable<(string File, byte[] Data)>? files = null, Action<Ps3WebOptionsBuilder>? configure = null)
        {
            _install = new TempInstall(files ?? []);

            var builder = new Ps3WebOptionsBuilder { ClientRoot = Path.Combine(_install.Root, "client") };

            configure?.Invoke(builder);

            TracePath = Path.Combine(_install.Root, "trace.jsonl");
            Characters = new CharacterStore();

            Service = new Ps3WebService(
                new Ps3WebOptions
                {
                    ClientRoot = builder.ClientRoot,
                    ExtraClients = [.. builder.ExtraClients.Select(name => Path.Combine(_install.Root, name))],
                    Digest = DigestBytes,
                    Advertise = "192.168.1.20",
                    Replies = builder.Replies,
                    Container = builder.Plain ? null : new ZsContainer(),
                    ServeMismatch = builder.ServeMismatch,
                },
                new TraceLog(TracePath),
                Characters);
        }

        public string Root => _install.Root;

        public string TracePath { get; }

        public CharacterStore Characters { get; }

        public Ps3WebService Service { get; }

        public WebResponse Get(string target, string method = "GET") =>
            Service.Handle(new WebRequest(method, target, Host: "ps3.freerealms.com", UserAgent: "test", Peer: "127.0.0.1"));

        public WebResponse Post(string target, string body, long? declared = null, string contentType = "application/x-www-form-urlencoded") =>
            Post(target, Encoding.UTF8.GetBytes(body), declared, contentType);

        public WebResponse Post(string target, byte[] body, long? declared = null, string contentType = "application/x-www-form-urlencoded") =>
            Service.Handle(new WebRequest(
                "POST",
                target,
                Host: "ps3.freerealms.com",
                Peer: "127.0.0.1",
                ContentType: contentType,
                HeaderNames: ["Content-Length", "Content-Type", "Host", "MIME-Version"],
                ContentLength: declared ?? body.Length,
                Body: body));

        public List<JsonElement> Trace()
        {
            if (!File.Exists(TracePath))
                return [];

            return [.. File.ReadAllLines(TracePath).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line).RootElement.Clone())];
        }

        public JsonElement LastTrace(string name) => Trace().Last(item => item.GetProperty("event").GetString() == name);

        public void Dispose() => _install.Dispose();
    }

    private sealed class Ps3WebOptionsBuilder
    {
        public string ClientRoot { get; set; } = string.Empty;

        public List<string> ExtraClients { get; } = [];

        public Ps3ReplyOptions Replies { get; set; } = new();

        public bool Plain { get; set; }

        public bool ServeMismatch { get; set; }
    }

    private static byte[] Text(int lines, string eol = "\r\n") =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(line => $"free realms line {line % 7}{eol}")));

    /// <summary>Bytes zlib cannot make smaller, so the digest lists them as stored.</summary>
    private static byte[] Noise(int length)
    {
        var data = new byte[length];
        var state = 12345u;

        for (var index = 0; index < length; index++)
        {
            state = (state * 1664525u) + 1013904223u;
            data[index] = (byte)(state >> 24);
        }

        return data;
    }

    private static string ZsRoute(string file, string query) => $"{Prefix}FreeRealms.zs/{file}.zs?{query}";

    private static string Query(byte[] raw) => $"c={Crc32.Compute(raw)}&l={ZsContainer.PayloadLength(raw) + ZsContainer.TrailerLength}";

    // ----------------------------------------------------------------------------------------- GET routes

    [TestMethod]
    public void AppConfigAndDigestAreServedForGetAndHead()
    {
        using var rig = new Rig();

        foreach (var method in new[] { "GET", "HEAD" })
        {
            var config = rig.Get(Ps3Routes.AppConfig, method);

            Assert.AreEqual(200, config.Status);
            Assert.AreEqual("text/xml; charset=utf-8", config.ContentType);
            CollectionAssert.AreEqual(AppConfigDocument.Build("192.168.1.20"), config.Body);

            var digest = rig.Get(Ps3Routes.DigestRoute, method);

            Assert.AreEqual(200, digest.Status);
            Assert.AreEqual("application/octet-stream", digest.ContentType);
            CollectionAssert.AreEqual(DigestBytes, digest.Body);
            Assert.AreEqual(DigestBytes.Length, digest.Length);
        }

        Assert.AreEqual(2, rig.Trace().Count(item => item.GetProperty("event").GetString() == "digest_reply"));

        var reply = rig.LastTrace("config_reply");

        Assert.AreEqual(200, reply.GetProperty("status").GetInt32());
        Assert.AreEqual(64, reply.GetProperty("sha256").GetString()!.Length);
    }

    [TestMethod]
    public void UnknownGetRoutesAre404()
    {
        using var rig = new Rig();

        var response = rig.Get("/somewhere/else");

        Assert.AreEqual(404, response.Status);
        Assert.AreEqual("text/plain", response.ContentType);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("Not implemented\r\n"), response.Body);
        Assert.AreEqual("/somewhere/else", rig.LastTrace("unhandled_get").GetProperty("route").GetString());
    }

    [TestMethod]
    public void OtherMethodsAre501()
    {
        using var rig = new Rig();

        Assert.AreEqual(501, rig.Get("/", "DELETE").Status);
    }

    // ----------------------------------------------------------------------------------------- POST routes

    [TestMethod]
    public void ServerListReplyAndTraceMatchTheReference()
    {
        using var rig = new Rig();

        var response = rig.Post("/ps3ws/game/serverList.action?request_locale=en_US", "Session=");

        Assert.AreEqual(200, response.Status);
        Assert.AreEqual("text/xml; charset=utf-8", response.ContentType);
        CollectionAssert.AreEqual(Ps3Replies.Build()[Ps3ReplyKind.ServerList], response.Body);

        var request = rig.LastTrace("serverlist_request");

        Assert.AreEqual("53657373696f6e3d", request.GetProperty("body_hex").GetString());
        Assert.AreEqual("Session=", request.GetProperty("body_text").GetString());
        CollectionAssert.AreEqual(
            new[] { "content-length", "content-type", "host", "mime-version" },
            request.GetProperty("header_names").EnumerateArray().Select(item => item.GetString()).ToArray());

        var reply = rig.LastTrace("ps3_reply");

        Assert.AreEqual("server_list", reply.GetProperty("reply_kind").GetString());
        Assert.AreEqual("baseline", reply.GetProperty("server_list_variant").GetString());
        Assert.AreEqual("attributes", reply.GetProperty("xml_style").GetString());
        Assert.AreEqual(response.Body.Length, reply.GetProperty("body_bytes").GetInt32());
        CollectionAssert.AreEqual(new[] { "Session" }, reply.GetProperty("request_fields").EnumerateArray().Select(item => item.GetString()).ToArray());

        var post = rig.LastTrace("ps3_post");

        Assert.AreEqual("request_locale=en_US", post.GetProperty("query").GetString());
        Assert.AreEqual(8, post.GetProperty("body_bytes").GetInt32());
    }

    [TestMethod]
    public void OnlyTheServerListReplyNamesAVariant()
    {
        using var rig = new Rig(configure: options => options.Replies = new Ps3ReplyOptions(ZoneField: true));

        rig.Post("/ps3ws/account/info.action", "Session=a&Account=b");

        var reply = rig.LastTrace("ps3_reply");

        Assert.AreEqual("account_info", reply.GetProperty("reply_kind").GetString());
        Assert.AreEqual(JsonValueKind.Null, reply.GetProperty("server_list_variant").ValueKind);
        CollectionAssert.AreEqual(new[] { "Account", "Session" }, reply.GetProperty("request_fields").EnumerateArray().Select(item => item.GetString()).ToArray());

        rig.Post("/ps3ws/game/serverList.action", "Session=");

        Assert.AreEqual("zone", rig.LastTrace("ps3_reply").GetProperty("server_list_variant").GetString());
    }

    [TestMethod]
    public void EveryPs3wsRouteGetsItsOwnReply()
    {
        using var rig = new Rig();

        var replies = Ps3Replies.Build();

        foreach (var (route, kind) in new[]
        {
            ("/ps3ws/game/serverList.action", Ps3ReplyKind.ServerList),
            ("/ps3ws/account/info.action", Ps3ReplyKind.AccountInfo),
            ("/ps3ws/character/checkName.action", Ps3ReplyKind.CheckName),
            ("/ps3ws/character/create.action", Ps3ReplyKind.CharacterCreate),
            ("/ps3ws/character/login.action", Ps3ReplyKind.CharacterLogin),
            ("/ps3ws/character/delete.action", Ps3ReplyKind.CharacterDelete),
            ("/ps3ws/character/friendsList.action", Ps3ReplyKind.FriendsList),
            ("/ps3ws/anything/else.action", Ps3ReplyKind.Generic),
        })
        {
            CollectionAssert.AreEqual(replies[kind], rig.Post(route, "Session=x").Body, route);
        }
    }

    [TestMethod]
    public void PostsOutsideTheWebServiceAre501()
    {
        using var rig = new Rig();

        var response = rig.Post("/app/appconfig.php", "x=1");

        Assert.AreEqual(501, response.Status);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("Not implemented\r\n"), response.Body);
    }

    [TestMethod]
    public void BodiesOverOneMebibyteAreRejected()
    {
        using var rig = new Rig();

        Assert.AreEqual(413, rig.Post("/ps3ws/game/serverList.action", [], declared: (1024 * 1024) + 1).Status);
        Assert.AreEqual(413, rig.Post("/ps3ws/game/serverList.action", [], declared: -1).Status);
        Assert.AreEqual(200, rig.Post("/ps3ws/game/serverList.action", [], declared: 0).Status);
        Assert.AreEqual(200, rig.Post("/ps3ws/game/serverList.action", new byte[1024 * 1024]).Status);
    }

    [TestMethod]
    public void CharacterCreateKeepsTheValuesButNotTheSession()
    {
        using var rig = new Rig();

        Assert.AreEqual(0, rig.Characters.Snapshot().Count);

        rig.Post("/ps3ws/character/create.action", "Session=secret&Name=Zo%C3%AB+Dean&Head=2&Skin=&Name=ignored");

        var values = rig.Characters.Snapshot();

        Assert.AreEqual(3, values.Count);
        Assert.AreEqual("Zo\u00eb Dean", values["Name"]);
        Assert.AreEqual("2", values["Head"]);
        Assert.AreEqual(string.Empty, values["Skin"]);
        Assert.IsFalse(values.ContainsKey("Session"));

        // The next character replaces the whole set.
        rig.Post("/ps3ws/character/create.action", "Session=x&Name=Next");

        CollectionAssert.AreEqual(new[] { "Name" }, rig.Characters.Snapshot().Keys.ToArray());
        Assert.AreEqual("Next", rig.Characters.Snapshot()["Name"]);

        // Other routes leave it alone.
        rig.Post("/ps3ws/character/login.action", "Name=Other");

        Assert.AreEqual("Next", rig.Characters.Snapshot()["Name"]);
    }

    // ----------------------------------------------------------------------------------------- .zs downloads

    [TestMethod]
    public void ZsDownloadWrapsTheMatchingFile()
    {
        var raw = Text(200);

        using var rig = new Rig([("client/USRDIR/art/notes.txt", raw)]);

        var response = rig.Get(ZsRoute("art/notes.txt", Query(raw)));

        Assert.AreEqual(200, response.Status);
        Assert.AreEqual("application/octet-stream", response.ContentType);
        Assert.AreEqual(ZsContainer.PayloadLength(raw) + ZsContainer.TrailerLength, response.Body.Length);
        CollectionAssert.AreEqual(raw, ZsContainer.Inflate(response.Body.AsSpan(0, response.Body.Length - ZsContainer.TrailerLength)));
        Assert.AreEqual(0x01, response.Body[^ZsContainer.TrailerLength]);
        Assert.IsTrue(response.Body[^(ZsContainer.TrailerLength - 1)..].All(value => value == 0));

        var trace = rig.LastTrace("zs_response");

        Assert.AreEqual("zs-zlib-zero-trailer", trace.GetProperty("codec").GetString());
        Assert.AreEqual("art/notes.txt", trace.GetProperty("decoded").GetString());
        Assert.IsTrue(trace.GetProperty("matches_manifest_length").GetBoolean());
        Assert.AreEqual($"0x{Crc32.Compute(raw):x}", trace.GetProperty("decoded_crc32").GetString());
        Assert.AreEqual(0, trace.GetProperty("attempt").GetInt32());
    }

    [TestMethod]
    public void ZsDownloadWithoutAQueryServesTheFirstFile()
    {
        var raw = Text(50);

        using var rig = new Rig([("client/USRDIR/a.txt", raw)]);

        var response = rig.Get($"{Prefix}FreeRealms.zs/a.txt.zs");

        Assert.AreEqual(200, response.Status);
        CollectionAssert.AreEqual(raw, ZsContainer.Inflate(response.Body.AsSpan(0, response.Body.Length - ZsContainer.TrailerLength)));
    }

    [TestMethod]
    public void ZsDownloadPadsTheStreamToTheLengthTheDigestExpects()
    {
        var raw = Text(300);
        var natural = ZsContainer.PayloadLength(raw);

        using var rig = new Rig([("client/USRDIR/a.txt", raw)]);

        // The digest of another build recorded more bytes than this zlib writes; the container must still be exactly that long.
        foreach (var extra in new[] { 24, 30, 45 })
        {
            var response = rig.Get(ZsRoute("a.txt", $"c={Crc32.Compute(raw)}&l={natural + extra + ZsContainer.TrailerLength}"));

            Assert.AreEqual(200, response.Status);
            Assert.AreEqual(natural + extra + ZsContainer.TrailerLength, response.Body.Length, $"extra {extra}");
            CollectionAssert.AreEqual(raw, ZsContainer.Inflate(response.Body.AsSpan(0, response.Body.Length - ZsContainer.TrailerLength)), $"extra {extra}");
        }
    }

    [TestMethod]
    public void ZsDownloadWithAWrongCrcIs409()
    {
        var raw = Text(20);

        using var rig = new Rig([("client/USRDIR/a.txt", raw)]);

        var response = rig.Get(ZsRoute("a.txt", "c=1&l=100"));

        Assert.AreEqual(409, response.Status);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("Source does not match client CRC\r\n"), response.Body);

        var trace = rig.LastTrace("zs_source_crc_mismatch");

        Assert.AreEqual("0x1", trace.GetProperty("expected_crc32").GetString());
        Assert.AreEqual(1, trace.GetProperty("tried").GetArrayLength());
        Assert.AreEqual(raw.Length, trace.GetProperty("tried")[0].GetProperty("size").GetInt32());
    }

    [TestMethod]
    public void ZsDownloadOfAMissingFileIs404()
    {
        using var rig = new Rig([("client/USRDIR/a.txt", Text(5))]);

        var response = rig.Get(ZsRoute("art/missing.bin", "c=1&l=200"));

        Assert.AreEqual(404, response.Status);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("Decoded source not found\r\n"), response.Body);
        Assert.AreEqual("art/missing.bin", rig.LastTrace("zs_source_missing").GetProperty("decoded").GetString());
    }

    [TestMethod]
    public void ZsDownloadConvertsLfFilesToCrlfWhenThatMatches()
    {
        var crlf = Text(120);
        var lf = Text(120, "\n");

        using var rig = new Rig([("client/USRDIR/art/notes.txt", lf)]);

        var response = rig.Get(ZsRoute("art/notes.txt", Query(crlf)));

        Assert.AreEqual(200, response.Status);
        CollectionAssert.AreEqual(crlf, ZsContainer.Inflate(response.Body.AsSpan(0, response.Body.Length - ZsContainer.TrailerLength)));
        Assert.AreEqual("lf_to_crlf", rig.LastTrace("zs_response").GetProperty("note").GetString());
    }

    [TestMethod]
    public void ZsDownloadSearchesTheExtraClientFolders()
    {
        var right = Text(80);
        var wrong = Text(81);

        using var rig = new Rig(
            [("client/USRDIR/a.txt", wrong), ("other/USRDIR/a.txt", right)],
            options => options.ExtraClients.Add("other"));

        var response = rig.Get(ZsRoute("a.txt", Query(right)));

        Assert.AreEqual(200, response.Status);
        CollectionAssert.AreEqual(right, ZsContainer.Inflate(response.Body.AsSpan(0, response.Body.Length - ZsContainer.TrailerLength)));
        Assert.IsTrue(rig.LastTrace("zs_response").GetProperty("source").GetString()!.Contains("other", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ServeMismatchSendsTheLocalFileAnyway()
    {
        var local = Text(60);

        using var rig = new Rig([("client/USRDIR/a.txt", local)], options => options.ServeMismatch = true);

        var response = rig.Get(ZsRoute("a.txt", "c=1&l=400"));

        Assert.AreEqual(200, response.Status);
        CollectionAssert.AreEqual(local, ZsContainer.Inflate(response.Body.AsSpan(0, response.Body.Length - ZsContainer.TrailerLength)));
        Assert.AreEqual("served_crc_mismatch", rig.LastTrace("zs_response").GetProperty("note").GetString());
        Assert.AreEqual("0x1", rig.LastTrace("zs_serving_mismatch").GetProperty("expected_crc32").GetString());
    }

    [TestMethod]
    public void StoredFilesTryOneContainerVariantPerRetryAndRememberTheAccepted()
    {
        var stored = Noise(200);
        var other = Text(40);

        Assert.AreEqual(stored.Length, ZsContainer.PayloadLength(stored), "the sample must be incompressible");

        using var rig = new Rig([("client/USRDIR/stored.bin", stored), ("client/USRDIR/other.txt", other), ("client/USRDIR/stored2.bin", Noise(210))]);

        var query = Query(stored);

        string Variant() => rig.LastTrace("zs_response").GetProperty("variant").GetString()!;

        CollectionAssert.AreEqual(ZsContainer.EncodeStored(0, stored).Data, rig.Get(ZsRoute("stored.bin", query)).Body);
        Assert.AreEqual("raw+trailer00", Variant());

        // The client retries the same file with a growing cb= suffix.
        CollectionAssert.AreEqual(ZsContainer.EncodeStored(1, stored).Data, rig.Get(ZsRoute("stored.bin", query + "&cb=x")).Body);
        Assert.AreEqual("raw+trailer02", Variant());
        Assert.AreEqual(1, rig.LastTrace("zs_response").GetProperty("attempt").GetInt32());

        CollectionAssert.AreEqual(ZsContainer.EncodeStored(3, stored).Data, rig.Get(ZsRoute("stored.bin", query + "&cb=xyz")).Body);
        Assert.AreEqual("raw+trailer04", Variant());

        // It moved on to another file, so variant 3 was accepted and the next stored file starts there.
        Assert.AreEqual(200, rig.Get(ZsRoute("other.txt", Query(other))).Status);

        var second = Noise(210);

        CollectionAssert.AreEqual(ZsContainer.EncodeStored(3, second).Data, rig.Get(ZsRoute("stored2.bin", Query(second))).Body);
        Assert.AreEqual("raw+trailer04", Variant());
    }

    [TestMethod]
    public void ZsDownloadRejectsPathsOutsideTheInstall()
    {
        using var rig = new Rig([("secret.txt", Text(3)), ("client/USRDIR/a.txt", Text(3))]);

        foreach (var route in new[]
        {
            $"{Prefix}FreeRealms.zs/../../secret.txt.zs",
            $"{Prefix}FreeRealms.zs/%2e%2e/%2e%2e/secret.txt.zs",
            $"{Prefix}FreeRealms.zs/..%2f..%2fsecret.txt.zs",
            $"{Prefix}FreeRealms.zs/..%5c..%5csecret.txt.zs",
            $"{Prefix}FreeRealms.zs//etc/passwd.zs",
            $"{Prefix}FreeRealms.zs/%00.zs",
            $"{Prefix}FreeRealms.zs/C:/Windows/win.ini.zs",
            $"{Prefix}FreeRealms.zs/a.txt:stream.zs",
        })
        {
            Assert.AreEqual(400, rig.Get(route).Status, route);
        }
    }

    // ----------------------------------------------------------------------------------------- plain downloads

    [TestMethod]
    public void PlainDownloadsServeTheFileUnderUsrdir()
    {
        var data = Noise(1000);

        using var rig = new Rig([("client/USRDIR/Digests/NPUA30048.soe", data)]);

        foreach (var method in new[] { "GET", "HEAD" })
        {
            var response = rig.Get($"{Prefix}Digests/NPUA30048.soe", method);

            Assert.AreEqual(200, response.Status);
            Assert.AreEqual("application/octet-stream", response.ContentType);
            Assert.AreEqual(1000, response.Length);
            Assert.AreEqual(Path.Combine(rig.Root, "client", "USRDIR", "Digests", "NPUA30048.soe"), response.FilePath);
        }

        var trace = rig.LastTrace("asset_reply");

        Assert.AreEqual(1000, trace.GetProperty("bytes").GetInt32());
        Assert.AreEqual("HEAD", trace.GetProperty("method").GetString());
    }

    [TestMethod]
    public void PlainDownloadsAlsoWorkInTheZsFolderWhenTheContainerIsOff()
    {
        var data = Text(10);

        using var rig = new Rig([("client/USRDIR/FreeRealms.zs/art/a.txt.zs", data)], options => options.Plain = true);

        var response = rig.Get($"{Prefix}FreeRealms.zs/art/a.txt.zs?c=1&l=2");

        Assert.AreEqual(200, response.Status);
        Assert.AreEqual(data.Length, response.Length);
    }

    [TestMethod]
    public void PlainDownloadsOfMissingFilesAre404()
    {
        using var rig = new Rig([("client/USRDIR/a.txt", Text(2))]);

        var response = rig.Get($"{Prefix}art/nothing.bin");

        Assert.AreEqual(404, response.Status);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("Asset not found\r\n"), response.Body);
        Assert.AreEqual("art/nothing.bin", rig.LastTrace("asset_missing").GetProperty("file").GetString());

        // A folder is not a file.
        Assert.AreEqual(404, rig.Get($"{Prefix}art").Status);
    }

    [TestMethod]
    public void PlainDownloadsRejectPathsOutsideUsrdir()
    {
        using var rig = new Rig([("client/secret.txt", Text(3)), ("client/USRDIR/a.txt", Text(3))]);

        foreach (var route in new[]
        {
            $"{Prefix}",
            $"{Prefix}../secret.txt",
            $"{Prefix}..%2fsecret.txt",
            $"{Prefix}%2e%2e%5csecret.txt",
            $"{Prefix}/a.txt",
            $"{Prefix}a.txt%00.zs",
        })
        {
            Assert.AreEqual(400, rig.Get(route).Status, route);
        }
    }

    // ----------------------------------------------------------------------------------------- helpers

    [TestMethod]
    public void NormalizeDropsEmptyAndDotSegments()
    {
        Assert.IsTrue(AssetService.TryNormalize("a//b/./c", out var segments));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, segments);

        Assert.IsTrue(AssetService.TryNormalize(".", out var none));
        Assert.AreEqual(0, none.Count);

        foreach (var bad in new[] { "", "/a", "a/../b", "..", "a\0b", "c:/x", "a:b" })
            Assert.IsFalse(AssetService.TryNormalize(bad, out _), bad);
    }

    [TestMethod]
    public void InsideMeansBelowTheRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fr-root"));

        Assert.IsTrue(AssetService.IsInside(root, root));
        Assert.IsTrue(AssetService.IsInside(root, Path.Combine(root, "a", "b.txt")));
        Assert.IsFalse(AssetService.IsInside(root, Path.GetDirectoryName(root)!));
        Assert.IsFalse(AssetService.IsInside(root, root + "-sibling"));
        Assert.IsFalse(AssetService.IsInside(root, Path.Combine(root, "..", "x")));
    }

    [TestMethod]
    public void DigestPreparationServesTheDigestUnchangedByDefault()
    {
        var trailer = new byte[ZsContainer.TrailerLength];

        trailer[0] = 1;

        var blob = DigestFile.Pack(ReferenceVectors.Digest.Body, trailer);

        var prepared = DigestPreparation.Prepare(blob, null);

        CollectionAssert.AreEqual(blob, prepared.Digest);
        CollectionAssert.AreEqual(trailer, prepared.Trailer);
        Assert.AreEqual(0, prepared.Changes.Count);
        Assert.IsFalse(prepared.OriginalLength, "a test digest is not 21466 bytes");
        Assert.ThrowsExactly<InvalidDataException>(() => DigestPreparation.Prepare(new byte[50], null));
    }

    [TestMethod]
    public void DigestPreparationMatchLocalKeepsTheTrailer()
    {
        var trailer = new byte[ZsContainer.TrailerLength];

        for (var index = 0; index < trailer.Length; index++)
            trailer[index] = (byte)(index + 7);

        var blob = DigestFile.Pack(ReferenceVectors.Digest.Body, trailer);

        using var install = new TempInstall(ReferenceVectors.Digest.LocalFiles);

        var prepared = DigestPreparation.Prepare(blob, install.Root);

        Assert.AreEqual(ReferenceVectors.Digest.Changes.Length, prepared.Changes.Count);
        CollectionAssert.AreEqual(trailer, prepared.Digest[^ZsContainer.TrailerLength..]);

        var (body, split) = DigestFile.Split(prepared.Digest);

        CollectionAssert.AreEqual(ReferenceVectors.Digest.AdaptedBody, body);
        CollectionAssert.AreEqual(trailer, split);
    }
}
