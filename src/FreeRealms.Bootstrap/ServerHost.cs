namespace FreeRealms.Bootstrap;

/// <summary>What the manager window collects before a server is started.</summary>
public sealed record ServerSettings(string Client, string Bind, string Advertise, int Port, bool Dns, bool MatchLocal);

/// <summary>One way to run the PS3 bootstrap server: the built-in C# server or the Python reference script.</summary>
public interface IServerHost
{
    event Action<string>? Log;

    Task StartAsync(ServerSettings settings);

    void Stop();
}
