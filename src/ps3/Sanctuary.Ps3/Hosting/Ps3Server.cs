using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Install;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Hosting;

/// <summary>
/// Starts the PS3 bootstrap server: DNS, the HTTP front (AppConfig, digest, ps3ws replies, asset downloads) and the UDP game-server
/// stand-in. Same job and same order as <c>main()</c> in <c>bootstrap/fr_ps3_bootstrap.py</c>.
/// </summary>
public static class Ps3Server
{
    /// <exception cref="Ps3ServerException">An option or the digest is not usable.</exception>
    public static async Task<RunningPs3Server> StartAsync(Ps3ServerOptions options, Action<ILoggingBuilder>? configureLogging = null)
    {
        var loggers = LoggerFactory.Create(builder => configureLogging?.Invoke(builder));
        var log = loggers.CreateLogger("homecoming");
        var notice = options.Notice ?? (_ => { });

        DnsServer? dns = null;
        UdpGatewayHost? gatewayHost = null;
        WebApplication? app = null;

        try
        {
            var usrDir = Path.Combine(options.ClientRoot, "USRDIR");
            var digestPath = options.DigestPath ?? Path.Combine(usrDir, "Digests", "NPUA30048.soe");

            byte[] blob;

            try
            {
                blob = await File.ReadAllBytesAsync(digestPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new Ps3ServerException($"cannot read the digest {digestPath}: {exception.Message}", exception);
            }

            PreparedDigest digest;

            try
            {
                digest = DigestPreparation.Prepare(blob, options.MatchLocal ? usrDir : null);
            }
            catch (InvalidDataException exception)
            {
                throw new Ps3ServerException($"{digestPath} is not a valid digest ({exception.Message})", exception);
            }

            if (!digest.OriginalLength)
            {
                notice($"WARNING: {digestPath} is {blob.Length} bytes, not the original {DigestFile.OriginalLength} (the client replaces it with the digest it last downloaded). " +
                       "Continuing; pass --digest with the original file if you have one.");
            }

            var protocolTrace = new TraceLog(TraceLog.ResolvePath("ps3_protocol_trace.jsonl"), log);

            if (options.MatchLocal)
            {
                foreach (var change in digest.Changes)
                {
                    protocolTrace.Write(
                        "digest_rewrite",
                        ("path", change.Path),
                        ("old_size", change.OldSize),
                        ("new_size", change.NewSize),
                        ("old_crc32", change.OldCrc32),
                        ("new_crc32", change.NewCrc32),
                        ("old_length", change.OldLength),
                        ("new_length", change.NewLength));

                    notice($"DIGEST-REWRITE {change.Path}: size {change.OldSize} -> {change.NewSize}, crc 0x{change.OldCrc32:X8} -> 0x{change.NewCrc32:X8}, length {change.OldLength} -> {change.NewLength}");
                }

                notice($"--match-local: {digest.Changes.Count} digest entries rewritten; serving a {digest.Digest.Length}-byte digest (original {DigestFile.OriginalLength})");
            }

            var container = options.Codec switch
            {
                "zs-zlib-zero-trailer" => new ZsContainer(),
                "zs-zlib-digest-trailer" => new ZsContainer(ZsCodec.DigestTrailer, digest.Trailer),
                "plain" => null,
                _ => throw new Ps3ServerException($"unknown codec '{options.Codec}'; this server knows {string.Join(", ", Ps3ServerOptions.Codecs)} (the early experiments stay in the Python script)"),
            };

            protocolTrace.Write(
                "bootstrap_start",
                ("advertise", options.Advertise.ToString()),
                ("port", options.HttpPort),
                ("client", options.ClientRoot),
                ("digest_bytes", digest.Digest.Length),
                ("server_list_variant", options.ZoneField ? "zone" : "baseline"));

            if (options.EnableDns)
            {
                dns = new DnsServer(options.Bind, options.DnsPort, options.Advertise, options.Upstream, log);
                dns.Start();

                log.LogInformation("DNS listening on {Bind}:{Port}; hosts -> {Advertise}; upstream={Upstream}", options.Bind, dns.Port, options.Advertise, options.Upstream);
            }

            var characters = new CharacterStore();
            var gatewayPort = options.GatewayPort;

            if (options.EnableGateway)
            {
                gatewayHost = new UdpGatewayHost(options.Bind, options.GatewayPort, loggers.CreateLogger("gateway"));
                gatewayPort = gatewayHost.Port;
            }

            var web = new Ps3WebService(
                new Ps3WebOptions
                {
                    ClientRoot = options.ClientRoot,
                    ExtraClients = options.ExtraClients,
                    Digest = digest.Digest,
                    Advertise = options.Advertise.ToString(),
                    Replies = new Ps3ReplyOptions(options.XmlStyle, options.AccountStatus, options.ExistingCharacter, options.ZoneField, $"{options.Advertise}:{gatewayPort}"),
                    Container = container,
                    ServeMismatch = options.ServeMismatch,
                },
                protocolTrace,
                characters,
                log);

            app = Ps3WebHost.Create(options.Bind.ToString(), options.HttpPort, loggers, web);

            await app.StartAsync();

            var httpPort = Ps3WebHost.PortOf(app);

            Ps3Gateway? gateway = null;

            if (gatewayHost is not null)
            {
                var gatewayLog = loggers.CreateLogger("gateway");

                // The zone sent in ZoneDetails must exist in this install's packs (docs/re/zone-loading.md).
                var world = GatewayWorld.FromInstall(usrDir, options.Gateway, gatewayLog);

                gateway = new Ps3Gateway(gatewayHost, world, characters, new TraceLog(TraceLog.ResolvePath("ps3_gateway_trace.jsonl")), gatewayLog);

                gatewayHost.Start(gateway);

                gatewayLog.LogInformation("UDP gateway listening on {Bind}:{Port} (handshake, reliable channel, world packets)", options.Bind, gatewayHost.Port);
            }

            log.LogInformation(
                "ps3ws replies: xml-style={Style}, empty account -> Status {Status}{Sample}",
                options.XmlStyle == XmlStyle.Attributes ? "attributes" : "elements",
                options.AccountStatus,
                options.ExistingCharacter ? " (existing-character sample)" : string.Empty);

            log.LogInformation("Server-list response variant: {Variant} (experimental Zone field: {Zone})", options.ZoneField ? "zone" : "baseline", options.ZoneField);
            log.LogInformation("HTTP listening on {Bind}:{Port}; AppConfig={Config} bytes; digest={Digest} bytes", options.Bind, httpPort, web.AppConfig.Length, digest.Digest.Length);

            return new RunningPs3Server(loggers, app, dns, gatewayHost, gateway, web, characters, digest, httpPort);
        }
        catch
        {
            if (app is not null)
                await app.DisposeAsync();

            gatewayHost?.Dispose();
            dns?.Dispose();
            loggers.Dispose();

            throw;
        }
    }
}

/// <summary>A started server; dispose it to stop everything.</summary>
public sealed class RunningPs3Server : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly WebApplication _app;
    private readonly DnsServer? _dns;
    private readonly UdpGatewayHost? _gatewayHost;

    internal RunningPs3Server(
        ILoggerFactory loggers,
        WebApplication app,
        DnsServer? dns,
        UdpGatewayHost? gatewayHost,
        Ps3Gateway? gateway,
        Ps3WebService web,
        CharacterStore characters,
        PreparedDigest digest,
        int httpPort)
    {
        _loggers = loggers;
        _app = app;
        _dns = dns;
        _gatewayHost = gatewayHost;
        Gateway = gateway;
        Web = web;
        Characters = characters;
        Digest = digest;
        HttpPort = httpPort;
    }

    public int HttpPort { get; }

    /// <summary>The UDP port of the game-server stand-in; null when it is off.</summary>
    public int? GatewayPort => _gatewayHost?.Port;

    public int? DnsPort => _dns?.Port;

    public Ps3Gateway? Gateway { get; }

    public Ps3WebService Web { get; }

    public CharacterStore Characters { get; }

    public PreparedDigest Digest { get; }

    /// <summary>Completes when the application is told to stop (Ctrl+C) or <paramref name="token"/> is cancelled.</summary>
    public Task WaitForShutdownAsync(CancellationToken token = default) => _app.WaitForShutdownAsync(token);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();

            _gatewayHost?.Dispose();
            _dns?.Dispose();
            _loggers.Dispose();
        }
    }
}
