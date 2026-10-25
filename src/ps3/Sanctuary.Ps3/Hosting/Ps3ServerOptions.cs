using System;
using System.Collections.Generic;
using System.Net;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Hosting;

/// <summary>Everything the PS3 server can be told; each setting matches an option of <c>bootstrap/fr_ps3_bootstrap.py</c>.</summary>
public sealed record Ps3ServerOptions
{
    /// <summary>The container of the <c>.zs</c> assets the client accepts (docs/install/digest-format.md).</summary>
    public const string DefaultCodec = "zs-zlib-zero-trailer";

    /// <summary>The codecs this server knows: the accepted container, the digest-trailer variant, and plain files.</summary>
    public static readonly IReadOnlyList<string> Codecs = [DefaultCodec, "zs-zlib-digest-trailer", "plain"];

    /// <summary>The NPUA30048 folder (<c>--client</c>).</summary>
    public required string ClientRoot { get; init; }

    /// <summary>More NPUA30048 folders searched for a file whose CRC32 matches (<c>--extra-client</c>).</summary>
    public IReadOnlyList<string> ExtraClients { get; init; } = [];

    /// <summary>The original digest (<c>--digest</c>); default <c>USRDIR/Digests/NPUA30048.soe</c> inside the client folder.</summary>
    public string? DigestPath { get; init; }

    /// <summary>Serve a digest that describes the local files (<c>--match-local</c>).</summary>
    public bool MatchLocal { get; init; }

    /// <summary>How <c>.zs</c> assets are wrapped (<c>--codec</c>); one of <see cref="Codecs"/>.</summary>
    public string Codec { get; init; } = DefaultCodec;

    /// <summary>The IPv4 address PS3 clients connect to (<c>--advertise</c>).</summary>
    public IPAddress Advertise { get; init; } = IPAddress.Loopback;

    /// <summary>The DNS forwarder for unrelated hosts (<c>--upstream</c>).</summary>
    public IPAddress Upstream { get; init; } = IPAddress.Parse("1.1.1.1");

    /// <summary>The listener address (<c>--bind</c>): loopback for this machine, any for the LAN.</summary>
    public IPAddress Bind { get; init; } = IPAddress.Loopback;

    /// <summary>The HTTP port (<c>--port</c>); 0 picks a free one.</summary>
    public int HttpPort { get; init; } = 80;

    /// <summary>Run the DNS stand-in (<c>--no-dns</c> turns it off).</summary>
    public bool EnableDns { get; init; } = true;

    public int DnsPort { get; init; } = 53;

    /// <summary>Run the UDP game-server stand-in (<c>--gateway-port 0</c> turns it off).</summary>
    public bool EnableGateway { get; init; } = true;

    /// <summary>The UDP port of the game-server stand-in (<c>--gateway-port</c>); 0 picks a free one.</summary>
    public int GatewayPort { get; init; } = 20260;

    /// <summary>How simple values are written in ps3ws replies (<c>--xml-style</c>).</summary>
    public XmlStyle XmlStyle { get; init; } = XmlStyle.Attributes;

    /// <summary>Status for an account without characters (<c>--account-status</c>).</summary>
    public int AccountStatus { get; init; } = 2;

    /// <summary>Report one sample character instead of an empty account (<c>--existing-character</c>).</summary>
    public bool ExistingCharacter { get; init; }

    /// <summary>The experimental Zone field in the server list (<c>--server-list-variant zone</c>).</summary>
    public bool ZoneField { get; init; }

    /// <summary>Experiment: send local files whose CRC32 does not match (<c>--serve-mismatch</c>).</summary>
    public bool ServeMismatch { get; init; }

    /// <summary>The <c>FR_*</c> switches of the game-server stand-in.</summary>
    public GatewayOptions Gateway { get; init; } = new();

    /// <summary>Receives the lines the Python script prints to the console (digest rewrites, warnings).</summary>
    public Action<string>? Notice { get; init; }
}

/// <summary>A problem the person starting the server can fix (bad option, unreadable digest).</summary>
public sealed class Ps3ServerException(string message, Exception? inner = null) : Exception(message, inner);
