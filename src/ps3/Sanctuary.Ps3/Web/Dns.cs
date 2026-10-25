using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Sanctuary.Ps3.Web;

/// <summary>The question of a DNS query: lower case host, type, class and the raw question bytes to echo back.</summary>
public sealed record DnsQuestion(string Host, int Type, int Class, byte[] Raw);

/// <summary>Reads a DNS query and builds the answers the stand-in gives. Ported from <c>parse_dns_question</c> and <c>dns_answer</c>.</summary>
public static class DnsPacket
{
    /// <summary>Parses a query with exactly one question; throws <see cref="FormatException"/> for anything else.</summary>
    public static DnsQuestion Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 17 || BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]) != 1)
            throw new FormatException("Expected one DNS question");

        var position = 12;
        var labels = new List<string>();

        while (true)
        {
            if (position >= packet.Length)
                throw new FormatException("Truncated DNS name");

            int size = packet[position++];

            if (size == 0)
                break;

            if (size > 63 || position + size > packet.Length)
                throw new FormatException("Invalid DNS label");

            var label = new StringBuilder(size);

            foreach (var value in packet.Slice(position, size))
            {
                if (value >= 0x80)
                    throw new FormatException("Invalid DNS label");

                label.Append(char.ToLowerInvariant((char)value));
            }

            labels.Add(label.ToString());
            position += size;
        }

        if (position + 4 > packet.Length)
            throw new FormatException("Missing DNS query type");

        var type = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(position, 2));
        var queryClass = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(position + 2, 2));

        return new DnsQuestion(string.Join('.', labels), type, queryClass, packet[12..(position + 4)].ToArray());
    }

    /// <summary>
    /// The reply for a query about one of our hosts. An A record gets the address; an AAAA or HTTPS lookup for a mapped host must
    /// also get a reply (NOERROR, no data), because forwarding it to the dead original DNS stalled the client's address lookup
    /// before any HTTP request was sent.
    /// </summary>
    public static byte[] Answer(byte[] packet, DnsQuestion question, byte[] ipv4)
    {
        byte[] answer;
        ushort count;

        if (question.Type == 1)
        {
            answer = new byte[16];
            answer[0] = 0xC0;
            answer[1] = 0x0C;
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(6), 60);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(10), 4);
            ipv4.CopyTo(answer, 12);
            count = 1;
        }
        else
        {
            answer = [];
            count = 0;
        }

        var reply = new byte[12 + question.Raw.Length + answer.Length];

        packet.AsSpan(0, 2).CopyTo(reply);
        reply[2] = 0x81;
        reply[3] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), count);
        question.Raw.CopyTo(reply, 12);
        answer.CopyTo(reply, 12 + question.Raw.Length);

        return reply;
    }
}

/// <summary>
/// A small DNS server: our hosts go to the advertised address, everything else is forwarded to the upstream resolver.
/// Needs UDP port 53, which on Windows means an Administrator shell.
/// </summary>
public sealed class DnsServer : IDisposable
{
    private readonly IPEndPoint _bind;
    private readonly byte[] _answerIp;
    private readonly string _advertise;
    private readonly IPEndPoint _upstream;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _socket;
    private Task? _loop;

    public DnsServer(IPAddress bind, int port, IPAddress advertise, IPAddress upstream, ILogger? logger = null)
    {
        _bind = new IPEndPoint(bind, port);
        _answerIp = advertise.GetAddressBytes();
        _advertise = advertise.ToString();
        _upstream = new IPEndPoint(upstream, 53);
        _logger = logger;
    }

    /// <summary>The port actually bound (useful with port 0 in tests); valid after <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)_socket!.Client.LocalEndPoint!).Port;

    public void Start()
    {
        _socket = new UdpClient(AddressFamily.InterNetwork);
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(_bind);

        // A reply to a closed port makes UdpClient.ReceiveAsync throw on Windows; ignore ICMP errors from clients that went away.
        if (OperatingSystem.IsWindows())
            _socket.Client.IOControl((IOControlCode)(-1744830452), [0, 0, 0, 0], null);

        _loop = Task.Run(Loop);
    }

    private async Task Loop()
    {
        var socket = _socket!;

        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await socket.ReceiveAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(socket, received.Buffer, received.RemoteEndPoint));
        }
    }

    private async Task HandleAsync(UdpClient socket, byte[] packet, IPEndPoint client)
    {
        var host = "?";
        var type = -1;

        try
        {
            var question = DnsPacket.Parse(packet);

            host = question.Host;
            type = question.Type;

            if (Array.IndexOf(Ps3Routes.Hosts, question.Host) >= 0 && question.Class == 1)
            {
                var reply = DnsPacket.Answer(packet, question, _answerIp);

                await socket.SendAsync(reply, client, _stop.Token).ConfigureAwait(false);
                _logger?.LogInformation("DNS {Host} type={Type} -> {Target} from {Client}", host, type, question.Type == 1 ? _advertise : "no data", client.Address);
            }
            else
            {
                // Everything else, including AAAA, goes to the upstream resolver instead of getting a bogus answer.
                using var upstream = new UdpClient(AddressFamily.InterNetwork);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                await upstream.SendAsync(packet, _upstream, timeout.Token).ConfigureAwait(false);

                var answer = await upstream.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                await socket.SendAsync(answer.Buffer, client, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogWarning("DNS {Host} type={Type} from {Client} failed: {Message}", host, type, client.Address, exception.Message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket?.Dispose();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop ends with the socket.
        }

        _stop.Dispose();
    }
}
