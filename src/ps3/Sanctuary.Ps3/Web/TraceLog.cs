using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Sanctuary.Ps3.Web;

/// <summary>
/// Writes the JSON-lines trace files (<c>ps3_protocol_trace.jsonl</c>, <c>ps3_gateway_trace.jsonl</c>) the test harness reads, in
/// the format of <c>bootstrap/tracing.py</c>: one object per line, <c>time_utc</c> and <c>event</c> first. A failed write is
/// ignored, because a diagnostic aid must never turn a client request into an error.
/// </summary>
public sealed class TraceLog
{
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger? _logger;

    public TraceLog(string? path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>A log that only reaches the logger (used by tests and by hosts that do not want files).</summary>
    public static TraceLog None(ILogger? logger = null) => new(null, logger);

    /// <summary>
    /// The trace file <paramref name="name"/> in <c>FR_TRACE_DIR</c> or the current directory; the temporary folder when that
    /// folder cannot be written.
    /// </summary>
    public static string ResolvePath(string name)
    {
        var folder = Environment.GetEnvironmentVariable("FR_TRACE_DIR");

        if (string.IsNullOrWhiteSpace(folder))
            folder = Directory.GetCurrentDirectory();

        try
        {
            Directory.CreateDirectory(folder);

            var probe = Path.Combine(folder, name + ".probe");

            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            folder = Path.GetTempPath();
        }

        return Path.Combine(folder, name);
    }

    /// <summary>The line that <see cref="Write"/> would append (without the newline).</summary>
    public static string Format(DateTime utcNow, string name, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        using var stream = new MemoryStream();

        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("time_utc", utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture));
            json.WriteString("event", name);

            foreach (var (key, value) in fields)
            {
                json.WritePropertyName(key);
                JsonSerializer.Serialize(json, value, ValueOptions);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Write(string name, params (string Key, object? Value)[] fields)
    {
        var pairs = new List<KeyValuePair<string, object?>>(fields.Length);

        foreach (var (key, value) in fields)
            pairs.Add(new KeyValuePair<string, object?>(key, value));

        var line = Format(DateTime.UtcNow, name, pairs);

        if (_path is not null)
        {
            try
            {
                lock (_gate)
                    File.AppendAllText(_path, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Ignored on purpose: see the class summary.
            }
        }

        if (_logger is not null && _logger.IsEnabled(LogLevel.Information))
        {
            var text = new StringBuilder();

            foreach (var (key, value) in fields)
                text.Append(' ').Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture));

            _logger.LogInformation("TRACE {Event}{Fields}", name, text.ToString());
        }
    }
}
