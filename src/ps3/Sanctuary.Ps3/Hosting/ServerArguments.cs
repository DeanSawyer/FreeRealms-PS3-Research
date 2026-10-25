using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Hosting;

/// <summary>The command line. The option names and defaults are the ones of <c>bootstrap/fr_ps3_bootstrap.py</c>, so one command works with both servers.</summary>
public static class ServerArguments
{
    public const string Usage = """
        usage: Sanctuary.Ps3.Server --client DIR [options]

        Free Realms PS3 bootstrap server: DNS, HTTP (AppConfig, patch digest, ps3ws replies, .zs asset downloads)
        and the UDP game-server stand-in. Needs a separately obtained PS3 game directory.

          --client DIR              NPUA30048 directory (required)
          --digest FILE             original digest to serve (default: USRDIR/Digests/NPUA30048.soe inside --client)
          --match-local             serve a digest whose size/CRC/length entries describe the files actually in --client
          --extra-client DIR        another NPUA30048 folder searched for a matching CRC32 (repeatable)
          --codec NAME              zs-zlib-zero-trailer (default, the container the client accepts),
                                    zs-zlib-digest-trailer, or plain
          --serve-mismatch          EXPERIMENT: send local files even when the CRC32 differs
          --server-list-variant V   baseline (default) or zone (experimental Zone field)
          --existing-character      report one sample character instead of an empty account
          --account-status N        status for an account without characters (default 2; 1 crashes the client)
          --xml-style S             attributes (default) or elements
          --gateway-port PORT       UDP port of the game-server stand-in (default 20260, 0 turns it off)
          --advertise IP            IPv4 address PS3 clients connect to (default 127.0.0.1)
          --upstream IP             DNS forwarder for unrelated hosts (default 1.1.1.1)
          --no-dns                  do not run the DNS stand-in (use an existing resolver)
          --bind IP                 listener address (default 127.0.0.1; 0.0.0.0 for the LAN)
          --port N                  HTTP port (default 80)
          -h, --help                show this help

        The game-server stand-in is tuned with the same FR_* environment variables as the Python script
        (FR_ZONE, FR_ZONE_TYPE, FR_SPAWN, FR_SPAWN_Y, FR_ADDPC, FR_WELCOME, FR_EXTRA, FR_TIME); FR_TRACE_DIR sets
        where the JSON trace files are written.
        """;

    /// <summary>Parses <paramref name="args"/>. Returns options, or a message to show when the command line is wrong (<see cref="ParseResult.Help"/> for -h).</summary>
    public static ParseResult Parse(IReadOnlyList<string> args, Func<string, string?> environment, Action<string>? notice = null)
    {
        string? client = null;
        string? digest = null;
        var matchLocal = false;
        var extra = new List<string>();
        var codec = Ps3ServerOptions.DefaultCodec;
        var serveMismatch = false;
        var zoneField = false;
        var existing = false;
        var accountStatus = 2;
        var style = XmlStyle.Attributes;
        var gatewayPort = 20260;
        var advertise = IPAddress.Loopback;
        var upstream = IPAddress.Parse("1.1.1.1");
        var dns = true;
        var bind = IPAddress.Loopback;
        var port = 80;

        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            string? inline = null;

            var equals = name.StartsWith("--", StringComparison.Ordinal) ? name.IndexOf('=', StringComparison.Ordinal) : -1;

            if (equals > 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            string? Value()
            {
                if (inline is not null)
                    return inline;

                return index + 1 < args.Count ? args[++index] : null;
            }

            switch (name)
            {
                case "-h":
                case "--help":
                    return ParseResult.ForHelp();
                case "--match-local":
                    matchLocal = true;
                    break;
                case "--serve-mismatch":
                    serveMismatch = true;
                    break;
                case "--existing-character":
                    existing = true;
                    break;
                case "--no-dns":
                    dns = false;
                    break;
                case "--client":
                    client = Value();

                    if (client is null)
                        return ParseResult.Fail("--client needs a value");

                    break;
                case "--digest":
                    digest = Value();

                    if (digest is null)
                        return ParseResult.Fail("--digest needs a value");

                    break;
                case "--extra-client":
                    {
                        var folder = Value();

                        if (folder is null)
                            return ParseResult.Fail("--extra-client needs a value");

                        extra.Add(folder);

                        break;
                    }

                case "--codec":
                    codec = Value() ?? string.Empty;

                    if (!Ps3ServerOptions.Codecs.Contains(codec))
                        return ParseResult.Fail($"--codec: '{codec}' is not one of {string.Join(", ", Ps3ServerOptions.Codecs)} (the early experiments stay in the Python script)");

                    break;
                case "--server-list-variant":
                    {
                        var variant = Value();

                        if (variant is not ("baseline" or "zone"))
                            return ParseResult.Fail("--server-list-variant must be baseline or zone");

                        zoneField = variant == "zone";

                        break;
                    }

                case "--xml-style":
                    {
                        var value = Value();

                        if (value is not ("attributes" or "elements"))
                            return ParseResult.Fail("--xml-style must be attributes or elements");

                        style = value == "elements" ? XmlStyle.Elements : XmlStyle.Attributes;

                        break;
                    }

                case "--account-status":
                    if (!int.TryParse(Value(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out accountStatus))
                        return ParseResult.Fail("--account-status needs a whole number");

                    break;
                case "--gateway-port":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out gatewayPort) || gatewayPort > 65535)
                        return ParseResult.Fail("--gateway-port needs a port number (0 turns the game-server stand-in off)");

                    break;
                case "--port":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535)
                        return ParseResult.Fail("--port needs a port number");

                    break;
                case "--advertise":
                    if (!TryAddress(Value(), out advertise))
                        return ParseResult.Fail("--advertise needs an IPv4 address");

                    break;
                case "--upstream":
                    if (!TryAddress(Value(), out upstream))
                        return ParseResult.Fail("--upstream needs an IPv4 address");

                    break;
                case "--bind":
                    if (!TryAddress(Value(), out bind))
                        return ParseResult.Fail("--bind needs an IPv4 address");

                    break;
                default:
                    return ParseResult.Fail($"unrecognized argument: {args[index]}");
            }
        }

        if (client is null)
            return ParseResult.Fail("the following argument is required: --client");

        var options = new Ps3ServerOptions
        {
            ClientRoot = client,
            DigestPath = digest,
            MatchLocal = matchLocal,
            ExtraClients = extra,
            Codec = codec,
            ServeMismatch = serveMismatch,
            ZoneField = zoneField,
            ExistingCharacter = existing,
            AccountStatus = accountStatus,
            XmlStyle = style,
            EnableGateway = gatewayPort != 0,
            GatewayPort = gatewayPort,
            Advertise = advertise,
            Upstream = upstream,
            EnableDns = dns,
            Bind = bind,
            HttpPort = port,
            Gateway = GatewayOptions.FromEnvironment(environment, notice),
            Notice = notice,
        };

        return ParseResult.Ok(options);
    }

    private static bool TryAddress(string? text, out IPAddress address)
    {
        if (text is not null && IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            address = parsed;

            return true;
        }

        address = IPAddress.None;

        return false;
    }
}

/// <summary>The outcome of reading the command line.</summary>
public sealed class ParseResult
{
    private ParseResult()
    {
    }

    public Ps3ServerOptions? Options { get; private init; }

    public string? Error { get; private init; }

    public bool Help { get; private init; }

    public static ParseResult Ok(Ps3ServerOptions options) => new() { Options = options };

    public static ParseResult Fail(string error) => new() { Error = error };

    public static ParseResult ForHelp() => new() { Help = true };
}
