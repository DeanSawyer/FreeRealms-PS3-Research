using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using Sanctuary.Ps3.Assets;
using Sanctuary.Ps3.Install;

namespace Sanctuary.Ps3.Web;

/// <summary>
/// The downloads under <c>/patch/freerealms/ps3scelive/ps3-web/</c>. A <c>FreeRealms.zs/&lt;path&gt;.zs</c> request is answered by
/// wrapping a local file in the <c>.zs</c> container; any other path serves the file under <c>USRDIR</c> as it is. Ported from
/// <c>serve_zs</c> and <c>serve_asset</c> in <c>bootstrap/fr_ps3_bootstrap.py</c>.
/// </summary>
/// <remarks>
/// The client asks for <c>?c=&lt;CRC32 of the decoded file&gt;&amp;l=&lt;container length&gt;</c>. Every configured client folder is searched and
/// the first file whose CRC32 matches is used (after an LF to CRLF conversion if that makes it match). The early
/// <c>serve_lzma_terrain</c> experiment of the Python script is not ported: it was rejected by the client.
/// </remarks>
public sealed class AssetService
{
    private const string ZsFolder = "FreeRealms.zs/";

    private readonly List<string> _roots;
    private readonly ZsContainer? _container;
    private readonly bool _serveMismatch;
    private readonly TraceLog _trace;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    // Which stored-file variant the client accepted last (inferred: it moved on to another file).
    private int _learned;
    private (string Decoded, int Index)? _pending;

    /// <param name="clientRoot">The NPUA30048 folder; its <c>USRDIR</c> is the primary asset root.</param>
    /// <param name="extraClients">More NPUA30048 folders searched in order for a matching CRC32.</param>
    /// <param name="container">The <c>.zs</c> wrapper; null serves every file plain.</param>
    public AssetService(string clientRoot, IEnumerable<string> extraClients, ZsContainer? container, bool serveMismatch, TraceLog trace, ILogger? logger = null)
    {
        _roots = [Path.GetFullPath(Path.Combine(clientRoot, "USRDIR")), .. extraClients.Select(folder => Path.GetFullPath(Path.Combine(folder, "USRDIR")))];
        _container = container;
        _serveMismatch = serveMismatch;
        _trace = trace;
        _logger = logger;
    }

    /// <summary>The folders searched for game files, primary first.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <param name="route">The request path under <see cref="Ps3Routes.PatchPrefix"/>, still percent-encoded.</param>
    /// <param name="query">The query string without the <c>?</c>.</param>
    /// <param name="method">GET or HEAD.</param>
    public WebResponse Serve(string route, string query, string method)
    {
        var relative = FormBody.PercentDecode(route[Ps3Routes.PatchPrefix.Length..]);

        if (_container is not null)
        {
            var zs = ServeZs(route, relative.Replace('\\', '/'), query);

            if (zs is not null)
                return zs;
        }

        return ServePlain(route, relative, method);
    }

    private WebResponse? ServeZs(string route, string relative, string query)
    {
        if (!(relative.StartsWith(ZsFolder, StringComparison.Ordinal) && relative.EndsWith(".zs", StringComparison.Ordinal)))
            return null;

        var decoded = relative[ZsFolder.Length..^3];

        if (!TryNormalize(decoded, out var segments))
            return WebResponse.Text(400, "Invalid path");

        var candidates = new List<string>();

        foreach (var root in _roots)
        {
            var source = Combine(root, segments);

            if (source is null || !IsInside(root, source))
                return WebResponse.Text(403, "Forbidden");

            if (File.Exists(source))
                candidates.Add(source);
        }

        var pairs = FormBody.Parse(query, keepBlank: false);
        var wantCrc = Number(pairs, "c");
        var wantLength = Number(pairs, "l");
        var attempt = First(pairs, "cb").Length;

        lock (_gate)
        {
            if (_pending is { } pending && pending.Decoded != decoded)
            {
                _learned = pending.Index;
                _pending = null;

                _logger?.LogInformation("STORED variant {Variant} was accepted (client moved on from {File})", ZsContainer.StoredVariantLabels[pending.Index], pending.Decoded);
            }
        }

        if (candidates.Count == 0)
        {
            _trace.Write(
                "zs_source_missing",
                ("route", route),
                ("decoded", decoded),
                ("expected_crc32", wantCrc),
                ("manifest_length", wantLength),
                ("roots", _roots));

            return WebResponse.Text(404, "Decoded source not found");
        }

        byte[]? raw = null;
        string? chosen = null;
        var note = string.Empty;
        var tried = new List<Dictionary<string, object>>();
        string? firstSource = null;
        byte[]? firstData = null;

        foreach (var source in candidates)
        {
            var data = File.ReadAllBytes(source);

            if (firstData is null)
            {
                firstSource = source;
                firstData = data;
            }

            var crc = Crc32.Compute(data);

            tried.Add(new Dictionary<string, object> { ["source"] = source, ["size"] = data.Length, ["crc32"] = Hex(crc) });

            if (wantCrc is null || crc == wantCrc)
            {
                raw = data;
                chosen = source;
                break;
            }

            if (data.Length <= DigestEditor.TextFixLimit)
            {
                // Some tools rewrite line endings in text files (LF vs CRLF).
                var fixedText = DigestEditor.ToCrlf(data);

                if (Crc32.Compute(fixedText) == wantCrc)
                {
                    raw = fixedText;
                    chosen = source;
                    note = "lf_to_crlf";
                    break;
                }
            }
        }

        if (raw is null && _serveMismatch)
        {
            // EXPERIMENT: the local file is a different build than the digest describes. Send it anyway to find out whether the
            // client verifies the content after downloading.
            chosen = firstSource;
            raw = firstData;
            note = "served_crc_mismatch";

            _trace.Write(
                "zs_serving_mismatch",
                ("route", route),
                ("decoded", decoded),
                ("expected_crc32", Hex(wantCrc)),
                ("expected_length", wantLength),
                ("source", chosen),
                ("local_size", raw!.Length),
                ("local_crc32", Hex(Crc32.Compute(raw))));

            _logger?.LogWarning("SERVE-MISMATCH {File}: sending local copy ({Size} bytes, crc {Crc}) although the client asked for crc {Wanted}", decoded, raw.Length, Hex(Crc32.Compute(raw)), Hex(wantCrc));
        }

        if (raw is null)
        {
            _trace.Write(
                "zs_source_crc_mismatch",
                ("route", route),
                ("decoded", decoded),
                ("expected_crc32", Hex(wantCrc)),
                ("tried", tried));

            _logger?.LogWarning("No local copy of {File} matches client CRC {Wanted}; tried {Tried}", decoded, Hex(wantCrc), string.Join(", ", tried.Select(item => $"({item["source"]}, {item["size"]}, {item["crc32"]})")));

            return WebResponse.Text(409, "Source does not match client CRC");
        }

        var rawCrc = Crc32.Compute(raw);
        int? target = wantLength is { } length and not 0 ? (int)Math.Clamp(length - ZsContainer.TrailerLength, int.MinValue, int.MaxValue) : null;
        var variant = string.Empty;
        byte[] encoded;

        if (target is not null && target == raw.Length)
        {
            // Stored (uncompressed) file: vary the container on each client retry.
            int index;

            lock (_gate)
            {
                index = (_learned + attempt) % ZsContainer.StoredVariantLabels.Count;
                _pending = (decoded, index);
            }

            (encoded, variant) = ZsContainer.EncodeStored(index, raw);

            _logger?.LogInformation("STORED {File} attempt={Attempt} variant={Variant}", decoded, attempt, variant);
        }
        else
        {
            encoded = _container!.Encode(raw, target);
        }

        _trace.Write(
            "zs_response",
            ("route", route),
            ("decoded", decoded),
            ("codec", CodecName(_container!)),
            ("note", note),
            ("variant", variant),
            ("attempt", attempt),
            ("source", chosen),
            ("decoded_bytes", raw.Length),
            ("decoded_crc32", Hex(rawCrc)),
            ("bytes", encoded.Length),
            ("manifest_length", wantLength),
            ("matches_manifest_length", encoded.Length == wantLength));

        if (wantLength is not null && encoded.Length != wantLength)
            _logger?.LogWarning("ZS {File}: sent {Sent} bytes, manifest says {Wanted}", decoded, encoded.Length, wantLength);

        return WebResponse.Data("application/octet-stream", encoded);
    }

    private WebResponse ServePlain(string route, string relative, string method)
    {
        // Only files under the PS3 client's USRDIR are exposed, never arbitrary host files.
        if (!TryNormalize(relative.Replace('\\', '/'), out var segments))
            return WebResponse.Text(400, "Invalid path");

        var root = _roots[0];
        var target = Combine(root, segments);

        if (target is null || !IsInside(root, target))
            return WebResponse.Text(403, "Forbidden");

        if (!File.Exists(target))
        {
            _trace.Write("asset_missing", ("route", route), ("file", relative));

            return WebResponse.Text(404, "Asset not found");
        }

        var size = new FileInfo(target).Length;

        _trace.Write("asset_reply", ("route", route), ("bytes", size), ("method", method));

        return WebResponse.FromFile(target, size);
    }

    /// <summary>
    /// Splits a request path into folder names. The path must not be empty, rooted, contain NUL, a drive or stream colon, or a
    /// <c>..</c> segment; empty and <c>.</c> segments are dropped the way <c>pathlib</c> does.
    /// </summary>
    public static bool TryNormalize(string path, out List<string> segments)
    {
        segments = [];

        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal) || path.StartsWith('/'))
            return false;

        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".")
                continue;

            if (part == ".." || part.Contains(':', StringComparison.Ordinal))
                return false;

            segments.Add(part);
        }

        return true;
    }

    /// <summary>The full path of <paramref name="segments"/> under <paramref name="root"/>, or null when the operating system rejects the name.</summary>
    private static string? Combine(string root, List<string> segments)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it (the check Python does with <c>relative_to</c>).</summary>
    public static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);

        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static string First(List<(string Name, string Value)> pairs, string name)
    {
        foreach (var (key, value) in pairs)
        {
            if (key == name)
                return value;
        }

        return string.Empty;
    }

    /// <summary>A query value that is only ASCII digits; null otherwise. A number too large for 64 bits counts as the largest one.</summary>
    private static long? Number(List<(string Name, string Value)> pairs, string name)
    {
        var text = First(pairs, name);

        if (text.Length == 0 || !text.All(char.IsAsciiDigit))
            return null;

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : long.MaxValue;
    }

    /// <summary>Python's <c>hex()</c>: lower case, no padding.</summary>
    private static string Hex(long? value) => value is { } number ? "0x" + number.ToString("x", CultureInfo.InvariantCulture) : "None";

    private static string CodecName(ZsContainer container) =>
        container.Codec == ZsCodec.DigestTrailer ? "zs-zlib-digest-trailer" : "zs-zlib-zero-trailer";
}
