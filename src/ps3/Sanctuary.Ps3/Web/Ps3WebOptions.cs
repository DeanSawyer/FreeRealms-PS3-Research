using System.Collections.Generic;

using Sanctuary.Ps3.Install;

namespace Sanctuary.Ps3.Web;

/// <summary>Settings of the web front; each one matches an option of the Python bootstrap.</summary>
public sealed class Ps3WebOptions
{
    /// <summary>The NPUA30048 folder whose <c>USRDIR</c> holds the game files (<c>--client</c>).</summary>
    public required string ClientRoot { get; init; }

    /// <summary>More NPUA30048 folders searched, in order, for a file whose CRC32 matches the request (<c>--extra-client</c>).</summary>
    public IReadOnlyList<string> ExtraClients { get; init; } = [];

    /// <summary>The digest to serve, trailer included.</summary>
    public required byte[] Digest { get; init; }

    /// <summary>IPv4 address the client is told to connect to (<c>--advertise</c>).</summary>
    public string Advertise { get; init; } = "127.0.0.1";

    /// <summary>What the ps3ws replies say.</summary>
    public Ps3ReplyOptions Replies { get; init; } = new();

    /// <summary>How <c>.zs</c> assets are wrapped; null serves the plain files without the container.</summary>
    public ZsContainer? Container { get; init; } = new();

    /// <summary>Experiment: send the local file even when its CRC32 is not the one requested (<c>--serve-mismatch</c>).</summary>
    public bool ServeMismatch { get; init; }
}
