using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// Runs a <see cref="Ps3Gateway"/> on a UDP socket: one thread receives datagrams, another one ticks the gateway a few times a second.
/// The socket is also the gateway's transport.
/// </summary>
public sealed class UdpGatewayHost : IGatewayTransport, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly Socket _socket;
    private readonly ILogger? _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stop = new(false);
    private Ps3Gateway? _gateway;
    private Thread? _receiver;
    private Thread? _ticker;

    /// <param name="bind">The IPv4 address to listen on.</param>
    /// <param name="port">The UDP port; 0 picks a free one (see <see cref="Port"/>).</param>
    public UdpGatewayHost(IPAddress bind, int port, ILogger? logger = null)
    {
        _logger = logger;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(bind, port));

        // Windows reports an ICMP "port unreachable" for an earlier send as an error on the next receive; ignore those.
        if (OperatingSystem.IsWindows())
            _socket.IOControl((IOControlCode)(-1744830452), [0, 0, 0, 0], null);
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public void Send(IPEndPoint peer, byte[] datagram)
    {
        try
        {
            _socket.SendTo(datagram, peer);
        }
        catch (SocketException)
        {
            // The peer's port is closed; Windows reports it on a later call. The reliable channel resends anyway.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public void Start(Ps3Gateway gateway)
    {
        _gateway = gateway;

        _receiver = new Thread(ReceiveLoop) { Name = "gateway-receive", IsBackground = true };
        _ticker = new Thread(TickLoop) { Name = "gateway-tick", IsBackground = true };

        _receiver.Start();
        _ticker.Start();
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[65535];

        while (!_stop.IsSet)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length;

            try
            {
                length = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException)
            {
                if (_stop.IsSet)
                    return;

                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var peer = (IPEndPoint)remote;

            try
            {
                _gateway!.Receive(buffer[..length], peer, _clock.Elapsed);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A stand-in server must never die on an odd packet.
                _logger?.LogWarning("gateway packet from {Peer} failed: {Message}", peer, exception.Message);
            }
        }
    }

    private void TickLoop()
    {
        while (!_stop.Wait(TickInterval))
        {
            try
            {
                _gateway!.Tick(_clock.Elapsed);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger?.LogWarning("gateway tick failed: {Message}", exception.Message);
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _socket.Dispose();

        _receiver?.Join(TimeSpan.FromSeconds(1));
        _ticker?.Join(TimeSpan.FromSeconds(1));

        _stop.Dispose();
    }
}
