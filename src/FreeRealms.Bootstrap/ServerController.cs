using System.IO;
using System.Net;

using Microsoft.Extensions.Logging;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Hosting;

namespace FreeRealms.Bootstrap;

/// <summary>How serious a log line is, as the log page filters it.</summary>
public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One line of the server's log.</summary>
/// <param name="Time">Local time it was written.</param>
/// <param name="Severity">Info, warning or error.</param>
/// <param name="Source">Which part of the server wrote it: Server, Game server, Web or Manager.</param>
/// <param name="Message">The text.</param>
public sealed record LogEntry(DateTime Time, LogSeverity Severity, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string SeverityText => Severity switch
    {
        LogSeverity.Warning => "WARN",
        LogSeverity.Error => "ERROR",
        _ => "INFO",
    };

    /// <summary>The line as it goes into a report file.</summary>
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss,fff} {SeverityText} [{Source}] {Message}";
}

/// <summary>
/// Runs the PS3 server (<see cref="Ps3Server"/>) inside the manager: the DNS stand-in, the web server (AppConfig, patch digest, ps3ws
/// replies, asset downloads) and the UDP game server. Every log line goes to <see cref="Log"/>.
/// </summary>
public sealed class ServerController
{
    private RunningPs3Server? running;

    /// <summary>Raised for every log line, on whatever thread wrote it.</summary>
    public event Action<LogEntry>? Log;

    public bool IsRunning => running is not null;

    public DateTime? StartedAt { get; private set; }

    public RunningPs3Server? Server => running;

    /// <summary>Starts every service with <paramref name="settings"/>.</summary>
    /// <exception cref="InvalidOperationException">A setting is wrong or a port is taken; the message says which.</exception>
    public async Task StartAsync(ManagerSettings settings)
    {
        if (running is not null)
            throw new InvalidOperationException("The server is already running.");

        if (!IPAddress.TryParse(settings.Bind, out var bind))
            throw new InvalidOperationException($"'{settings.Bind}' is not an IPv4 address to listen on.");

        if (!IPAddress.TryParse(settings.Advertise, out var advertise))
            throw new InvalidOperationException($"'{settings.Advertise}' is not an IPv4 address to advertise.");

        if (!File.Exists(Path.Combine(settings.ClientFolder, "USRDIR", "Digests", "NPUA30048.soe")) && !File.Exists(settings.OriginalDigest))
            throw new InvalidOperationException("Choose your NPUA30048 client folder in Settings (it must contain USRDIR\\Digests\\NPUA30048.soe).");

        // The server writes its trace files where FR_TRACE_DIR points; the program folder may not be writable.
        Directory.CreateDirectory(ManagerSettings.TraceFolder);
        Environment.SetEnvironmentVariable("FR_TRACE_DIR", ManagerSettings.TraceFolder);

        var switches = settings.GatewaySwitches();
        var options = new Ps3ServerOptions
        {
            ClientRoot = settings.ClientFolder,
            ExtraClients = Directory.Exists(settings.ExtraClientFolder) ? new[] { settings.ExtraClientFolder } : Array.Empty<string>(),
            DigestPath = File.Exists(settings.OriginalDigest) ? settings.OriginalDigest : null,
            MatchLocal = settings.MatchLocal,
            CharacterFile = ManagerSettings.CharacterFile,
            Bind = bind,
            Advertise = advertise,
            HttpPort = settings.HttpPort,
            EnableDns = settings.Dns,
            GatewayPort = settings.GatewayPort,
            // The gameplay settings; any other FR_* switch (FR_COINSTORE_ITEMS and the like) can be set in the window that starts the manager.
            Gateway = GatewayOptions.FromEnvironment(
                name => switches.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name),
                message => Write(LogSeverity.Warning, "Server", message)),
            Notice = message => Write(LogSeverity.Info, "Server", message),
        };

        try
        {
            running = await Task.Run(() => Ps3Server.StartAsync(options, logging => logging
                .SetMinimumLevel(LogLevel.Information)
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddProvider(new EntryLoggerProvider(this))));
        }
        catch (Ps3ServerException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(
                $"A port is already in use or not allowed ({exception.Message}). Close other servers using UDP 53, TCP {settings.HttpPort} or UDP {settings.GatewayPort}, and run the manager as administrator.",
                exception);
        }

        StartedAt = DateTime.Now;

        Write(LogSeverity.Info, "Manager", $"Server started: web {running.HttpPort}, game server {(running.GatewayPort is { } port ? port.ToString() : "off")}, DNS {(running.DnsPort is { } dns ? dns.ToString() : "off")}.");

        if (running.DnsPort is { } dnsPort)
        {
            var asked = bind.Equals(IPAddress.Any) ? IPAddress.Loopback : bind;
            var problem = await DnsCheck.ProblemAsync(asked, advertise, dnsPort);

            if (problem is null)
                Write(LogSeverity.Info, "Manager", $"DNS check: {DnsCheck.Host} -> {advertise}, as the game will see it.");
            else
                Write(LogSeverity.Warning, "Manager", problem);
        }
    }

    /// <summary>Stops every service; safe to call when nothing runs.</summary>
    public async Task StopAsync()
    {
        var active = running;

        running = null;
        StartedAt = null;

        if (active is null)
            return;

        try
        {
            // Off the UI thread: stopping Kestrel awaits work that must not wait for this thread.
            await Task.Run(() => active.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception exception)
        {
            Write(LogSeverity.Warning, "Manager", $"Stopping took too long or failed: {exception.Message}");
        }

        Write(LogSeverity.Info, "Manager", "Server stopped.");
    }

    /// <summary>Writes a line of the manager's own.</summary>
    public void Write(LogSeverity severity, string source, string message) => Log?.Invoke(new LogEntry(DateTime.Now, severity, source, message));

    private static string SourceOf(string category) => category switch
    {
        "gateway" => "Game server",
        "homecoming" => "Server",
        _ when category.StartsWith("Microsoft", StringComparison.Ordinal) => "Web",
        _ => category,
    };

    private sealed class EntryLoggerProvider(ServerController owner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new EntryLogger(owner, SourceOf(categoryName));

        public void Dispose()
        {
        }
    }

    private sealed class EntryLogger(ServerController owner, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var severity = logLevel switch
            {
                >= LogLevel.Error => LogSeverity.Error,
                LogLevel.Warning => LogSeverity.Warning,
                _ => LogSeverity.Info,
            };

            var text = formatter(state, exception);

            owner.Write(severity, source, exception is null ? text : $"{text} ({exception.Message})");
        }
    }
}
