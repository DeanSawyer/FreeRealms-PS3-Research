using System.IO;
using System.Net;
using Microsoft.Extensions.Logging;
using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Hosting;

namespace FreeRealms.Bootstrap;

/// <summary>
/// Runs the C# server (<c>Sanctuary.Ps3</c>) inside the manager: DNS, HTTP, asset downloads and the UDP game-server stand-in.
/// It answers like <c>bootstrap/fr_ps3_bootstrap.py</c>, which stays the reference.
/// </summary>
public sealed class NativeServerHost : IServerHost
{
    private RunningPs3Server? running;

    public event Action<string>? Log;

    public async Task StartAsync(ServerSettings settings)
    {
        if (running is not null)
            throw new InvalidOperationException("Server already running.");

        // The trace files are what a bug report needs; the program folder may not be writable.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FR_TRACE_DIR")))
        {
            var traces = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeRealmsPS3Bootstrap", "traces");

            Environment.SetEnvironmentVariable("FR_TRACE_DIR", traces);
            Log?.Invoke($"Trace files go to {traces}");
        }

        var options = new Ps3ServerOptions
        {
            ClientRoot = settings.Client,
            MatchLocal = settings.MatchLocal,
            Bind = IPAddress.Parse(settings.Bind),
            Advertise = IPAddress.Parse(settings.Advertise),
            HttpPort = settings.Port,
            EnableDns = settings.Dns,
            Gateway = GatewayOptions.FromEnvironment(Environment.GetEnvironmentVariable, message => Log?.Invoke(message)),
            Notice = message => Log?.Invoke(message),
        };

        try
        {
            running = await Ps3Server.StartAsync(options, logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(new ActionLoggerProvider(message => Log?.Invoke(message))));
        }
        catch (Ps3ServerException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        Log?.Invoke($"C# server started: HTTP {running.HttpPort}, game-server stand-in UDP {(running.GatewayPort is { } port ? port.ToString() : "off")}.");
    }

    public void Stop()
    {
        var active = running;

        running = null;

        if (active is null)
            return;

        try
        {
            // Off the UI thread: stopping Kestrel awaits work that must not wait for this thread.
            Task.Run(() => active.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            Log?.Invoke($"Stop warning: {exception.Message}");
        }
        finally
        {
            Log?.Invoke("C# server stopped.");
        }
    }

    /// <summary>Sends every log line of the server to the manager's log panel.</summary>
    private sealed class ActionLoggerProvider(Action<string> write) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ActionLogger(write);

        public void Dispose()
        {
        }
    }

    private sealed class ActionLogger(Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var text = formatter(state, exception);

            if (logLevel >= LogLevel.Warning)
                text = $"{logLevel.ToString().ToUpperInvariant()} {text}";

            write(exception is null ? text : $"{text} ({exception.Message})");
        }
    }
}
