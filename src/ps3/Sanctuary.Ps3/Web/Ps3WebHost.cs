using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sanctuary.Ps3.Web;

/// <summary>Puts <see cref="Ps3WebService"/> on Kestrel: one terminal middleware, every answer closing the connection.</summary>
public static class Ps3WebHost
{
    /// <summary>
    /// Builds, but does not start, a web application that answers every request with the service.
    /// </summary>
    /// <param name="bind">The IPv4 address to listen on.</param>
    /// <param name="port">The port; 0 picks a free one (see <see cref="PortOf"/>).</param>
    /// <param name="createService">Creates the service once the logger factory exists.</param>
    /// <param name="configureLogging">Logging setup, for example the console format.</param>
    public static (WebApplication App, Ps3WebService Service) Create(
        string bind,
        int port,
        Func<ILoggerFactory, Ps3WebService> createService,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var builder = WebApplication.CreateSlimBuilder();

        configureLogging?.Invoke(builder.Logging);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Parse(bind), port);
        });

        var app = builder.Build();
        var service = createService(app.Services.GetRequiredService<ILoggerFactory>());

        app.Run(context => Handle(context, service));

        return (app, service);
    }

    /// <summary>The port a started application listens on (useful with port 0); 0 when it is not known.</summary>
    public static int PortOf(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>();

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return uri.Port;
        }

        return 0;
    }

    private static async Task Handle(HttpContext context, Ps3WebService service)
    {
        var http = context.Request;
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? http.Path.Value ?? "/";
        var peer = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var declared = http.ContentLength ?? 0;

        byte[]? body = null;

        if (HttpMethods.IsPost(http.Method) && declared is >= 0 and <= Ps3WebService.MaxBody)
        {
            body = new byte[declared];

            try
            {
                await http.Body.ReadExactlyAsync(body, context.RequestAborted);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or BadHttpRequestException)
            {
                await Write(context, service, WebResponse.Text(400, "Incomplete body"), head: false, peer);

                return;
            }
        }

        var request = new WebRequest(
            http.Method,
            target,
            http.Host.ToString(),
            http.Headers.UserAgent.ToString(),
            peer,
            http.ContentType ?? string.Empty,
            [.. http.Headers.Keys],
            declared,
            body);

        await Write(context, service, service.Handle(request), HttpMethods.IsHead(http.Method), peer);
    }

    private static async Task Write(HttpContext context, Ps3WebService service, WebResponse response, bool head, string peer)
    {
        var result = context.Response;

        result.StatusCode = response.Status;
        result.ContentType = response.ContentType;
        result.ContentLength = response.Length;
        result.Headers.CacheControl = "no-cache";
        result.Headers.Connection = "close";

        if (head)
            return;

        try
        {
            if (response.FilePath is { } path)
                await result.SendFileAsync(path, context.RequestAborted);
            else
                await result.Body.WriteAsync(response.Body, context.RequestAborted);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            if (response.FilePath is null)
                service.Trace.Write("response_write_failed", ("path", context.Request.Path.Value), ("exception", exception.GetType().Name), ("peer", peer));
            else
                service.Trace.Write("asset_write_failed", ("route", context.Request.Path.Value), ("exception", exception.GetType().Name));
        }
    }
}
