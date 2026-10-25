using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Sanctuary.Ps3.Web;

/// <summary>
/// Everything the PS3 client asks over HTTP: the AppConfig, the digest, the <c>.zs</c> and plain asset downloads and the ps3ws
/// web-service replies. A pure function from request to response, so it is tested without a socket; <see cref="Ps3WebHost"/> puts it
/// on Kestrel. Trace event names and fields are the ones of <c>make_handler</c> in <c>bootstrap/fr_ps3_bootstrap.py</c>.
/// </summary>
public sealed class Ps3WebService
{
    /// <summary>Largest request body accepted (the Python server's limit).</summary>
    public const long MaxBody = 1024 * 1024;

    private readonly Ps3WebOptions _options;
    private readonly TraceLog _trace;
    private readonly CharacterStore _characters;
    private readonly ILogger? _logger;
    private readonly byte[] _config;
    private readonly IReadOnlyDictionary<Ps3ReplyKind, byte[]> _replies;
    private readonly AssetService _assets;

    public Ps3WebService(Ps3WebOptions options, TraceLog trace, CharacterStore characters, ILogger? logger = null)
    {
        _options = options;
        _trace = trace;
        _characters = characters;
        _logger = logger;
        _config = AppConfigDocument.Build(options.Advertise);
        _replies = Ps3Replies.Build(options.Replies);
        _assets = new AssetService(options.ClientRoot, options.ExtraClients, options.Container, options.ServeMismatch, trace, logger);
    }

    /// <summary>The AppConfig document that is served.</summary>
    public byte[] AppConfig => _config;

    public AssetService Assets => _assets;

    public TraceLog Trace => _trace;

    /// <summary>How the server-list variant is called in the trace.</summary>
    public string ServerListVariant => _options.Replies.ZoneField ? "zone" : "baseline";

    public WebResponse Handle(WebRequest request)
    {
        switch (request.Method)
        {
            case "GET":
            case "HEAD":
                return Get(request);
            case "POST":
                return Post(request);
            default:
                return WebResponse.Text(501, "Unsupported method");
        }
    }

    /// <summary>The name a reply kind has in the trace (<c>reply_kind</c>).</summary>
    public static string KindName(Ps3ReplyKind kind) => kind switch
    {
        Ps3ReplyKind.ServerList => "server_list",
        Ps3ReplyKind.AccountInfo => "account_info",
        Ps3ReplyKind.CheckName => "check_name",
        Ps3ReplyKind.CharacterCreate => "character_create",
        Ps3ReplyKind.CharacterLogin => "character_login",
        Ps3ReplyKind.CharacterDelete => "character_delete",
        Ps3ReplyKind.FriendsList => "friends_list",
        _ => "generic",
    };

    private WebResponse Get(WebRequest request)
    {
        var route = request.Path;

        _trace.Write(
            "http_request",
            ("method", request.Method),
            ("route", route),
            ("query", request.Query),
            ("host", request.Host),
            ("peer", request.Peer));

        _logger?.LogInformation("{Method} {Target} host={Host} agent={Agent}", request.Method, request.Target, request.Host, request.UserAgent);

        if (route == Ps3Routes.AppConfig)
        {
            _trace.Write("config_reply", ("route", route), ("status", 200), ("bytes", _config.Length), ("sha256", Sha256(_config)));
            _logger?.LogInformation("200 AppConfig ({Bytes} bytes)", _config.Length);

            return WebResponse.Data("text/xml; charset=utf-8", _config);
        }

        if (route == Ps3Routes.DigestRoute)
        {
            _trace.Write("digest_reply", ("route", route), ("status", 200), ("bytes", _options.Digest.Length), ("sha256", Sha256(_options.Digest)));
            _logger?.LogInformation("200 original PS3 digest ({Bytes} bytes)", _options.Digest.Length);

            return WebResponse.Data("application/octet-stream", _options.Digest);
        }

        if (route.StartsWith(Ps3Routes.PatchPrefix, StringComparison.Ordinal))
            return _assets.Serve(route, request.Query, request.Method);

        _trace.Write("unhandled_get", ("method", request.Method), ("route", route), ("host", request.Host), ("status", 404));

        return WebResponse.Text(404, "Not implemented");
    }

    private WebResponse Post(WebRequest request)
    {
        var length = request.ContentLength;

        if (length < 0 || length > MaxBody)
            return WebResponse.Text(413, "Invalid body length");

        // Account and session payloads are never logged.
        var body = request.Body ?? [];
        var route = request.Path;

        _trace.Write("ps3_post", ("route", route), ("query", request.Query), ("body_bytes", length), ("peer", request.Peer));

        if (route == "/ps3ws/game/serverList.action")
        {
            // The server-list request carries no account data; record its shape.
            var head = body.AsSpan(0, Math.Min(64, body.Length)).ToArray();

            _trace.Write(
                "serverlist_request",
                ("body_hex", Convert.ToHexString(head).ToLowerInvariant()),
                ("body_text", Encoding.Latin1.GetString(head)),
                ("content_type", request.ContentType),
                ("header_names", (request.HeaderNames ?? []).Select(name => name.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList()));

            _logger?.LogInformation("serverList request body={Body} content-type={ContentType}", Encoding.Latin1.GetString(head), request.ContentType);
        }

        _logger?.LogInformation("POST {Target} host={Host} body_bytes={Bytes}", request.Target, request.Host, length);

        if (!route.StartsWith(Ps3Routes.WebServicePrefix, StringComparison.Ordinal))
            return WebResponse.Text(501, "Not implemented");

        var kind = Ps3Replies.KindOf(route);
        var reply = _replies[kind];

        // Only field names are logged here. The values of a character_create request are logged below (never the Session); the
        // session and account values are never logged.
        var fields = FormBody.Parse(Encoding.Latin1.GetString(body), keepBlank: true)
            .Select(pair => pair.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        _trace.Write(
            "ps3_reply",
            ("route", route),
            ("http_status", 200),
            ("body_bytes", reply.Length),
            ("reply_kind", KindName(kind)),
            ("request_fields", fields),
            ("server_list_variant", kind == Ps3ReplyKind.ServerList ? ServerListVariant : null),
            ("xml_style", _options.Replies.Style == XmlStyle.Attributes ? "attributes" : "elements"));

        if (kind == Ps3ReplyKind.CharacterCreate)
        {
            var values = FormBody.FirstValues(FormBody.Parse(new UTF8Encoding(false).GetString(body), keepBlank: true))
                .Where(pair => pair.Name != "Session")
                .ToList();

            _characters.Replace(values);

            _logger?.LogInformation(
                "character_create values (kept for the game-server probe): {Values}",
                string.Join(", ", values.OrderBy(pair => pair.Name, StringComparer.Ordinal).Select(pair => $"{pair.Name}={pair.Value}")));
        }

        _logger?.LogInformation(
            "{Kind} request fields: {Fields} -> replying Status={Status}",
            KindName(kind),
            fields.Count == 0 ? "(none)" : string.Join(", ", fields),
            Ps3Replies.StatusOf(reply) ?? "?");

        _logger?.LogInformation("200 test reply for {Route} ({Bytes} bytes)", route, reply.Length);

        return WebResponse.Data("text/xml; charset=utf-8", reply);
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
