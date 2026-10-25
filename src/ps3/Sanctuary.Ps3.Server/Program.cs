using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using Sanctuary.Ps3.Hosting;
using Sanctuary.Ps3.Server;

Console.OutputEncoding = new UTF8Encoding(false);

var parsed = ServerArguments.Parse(args, Environment.GetEnvironmentVariable, line => Console.WriteLine(line));

if (parsed.Help)
{
    Console.WriteLine(ServerArguments.Usage);

    return 0;
}

if (parsed.Options is null)
{
    Console.Error.WriteLine(ServerArguments.Usage);
    Console.Error.WriteLine();
    Console.Error.WriteLine($"error: {parsed.Error}");

    return 2;
}

using var cancel = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancel.Cancel();
};

RunningPs3Server server;

try
{
    server = await Ps3Server.StartAsync(
        parsed.Options,
        logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddConsole(options => options.FormatterName = PythonStyleFormatter.FormatterName);
            logging.AddConsoleFormatter<PythonStyleFormatter, ConsoleFormatterOptions>();
        });
}
catch (Ps3ServerException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");

    return 2;
}
catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException)
{
    Console.Error.WriteLine($"Cannot bind the DNS/HTTP/UDP port: {exception.Message}");
    Console.Error.WriteLine("Run from an Administrator PowerShell (ports 53 and 80 need it on Windows), or pick other ports.");

    return 1;
}

await using (server)
{
    await server.WaitForShutdownAsync(cancel.Token);

    Console.WriteLine("Stopping");
}

return 0;
