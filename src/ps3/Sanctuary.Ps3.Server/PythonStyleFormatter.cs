using System;
using System.IO;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Sanctuary.Ps3.Server;

/// <summary>
/// Console lines like the Python script's <c>%(asctime)s %(levelname)s %(message)s</c>: <c>2026-10-25 14:15:40,829 INFO text</c>. The test
/// scripts search the log for words such as <c>WARNING</c> and <c>Traceback</c>, so the level names are the Python ones.
/// </summary>
public sealed class PythonStyleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "python";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (message is null && logEntry.Exception is null)
            return;

        var level = logEntry.LogLevel switch
        {
            LogLevel.Trace or LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => "INFO",
        };

        textWriter.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff", System.Globalization.CultureInfo.InvariantCulture));
        textWriter.Write(' ');
        textWriter.Write(level);
        textWriter.Write(' ');
        textWriter.WriteLine(message);

        if (logEntry.Exception is not null)
            textWriter.WriteLine(logEntry.Exception.ToString());
    }
}
