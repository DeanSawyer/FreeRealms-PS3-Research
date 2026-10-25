using System;
using System.Collections.Generic;
using System.Text;

namespace Sanctuary.Ps3.Web;

/// <summary>One HTTP request as the web service sees it, independent of the server that received it.</summary>
/// <param name="Method">GET, HEAD, POST, ...</param>
/// <param name="Target">The raw request target, still percent-encoded: <c>/path?query</c>.</param>
/// <param name="ContentLength">The declared body length; negative when it is not a number.</param>
/// <param name="Body">The body; only read by the host when the declared length is acceptable.</param>
public sealed record WebRequest(
    string Method,
    string Target,
    string Host = "",
    string UserAgent = "",
    string Peer = "",
    string ContentType = "",
    IReadOnlyList<string>? HeaderNames = null,
    long ContentLength = 0,
    byte[]? Body = null)
{
    /// <summary>The part of the target before <c>?</c> and <c>#</c> (what <c>urlsplit(...).path</c> gives).</summary>
    public string Path => Split().Path;

    /// <summary>The part after <c>?</c>, without the fragment.</summary>
    public string Query => Split().Query;

    private (string Path, string Query) Split()
    {
        var text = Target;

        // A request line may carry an absolute URL (as sent to proxies); urlsplit keeps only the path and query.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var slash = text.IndexOf('/', text.IndexOf("://", StringComparison.Ordinal) + 3);

            text = slash < 0 ? "/" : text[slash..];
        }

        var fragment = text.IndexOf('#', StringComparison.Ordinal);

        if (fragment >= 0)
            text = text[..fragment];

        var question = text.IndexOf('?', StringComparison.Ordinal);

        return question < 0 ? (text, string.Empty) : (text[..question], text[(question + 1)..]);
    }
}

/// <summary>An answer: a status, a content type and either bytes or a file to stream. Every answer closes the connection.</summary>
public sealed record WebResponse(int Status, string ContentType, byte[] Body, string? FilePath = null, long FileLength = 0)
{
    /// <summary>The value of the <c>Content-Length</c> header.</summary>
    public long Length => FilePath is null ? Body.LongLength : FileLength;

    /// <summary>A short plain-text answer, the way the reference server writes its errors.</summary>
    public static WebResponse Text(int status, string message) =>
        new(status, "text/plain", Encoding.ASCII.GetBytes(message + "\r\n"));

    public static WebResponse Data(string contentType, byte[] body) => new(200, contentType, body);

    public static WebResponse FromFile(string path, long length) => new(200, "application/octet-stream", [], path, length);
}
