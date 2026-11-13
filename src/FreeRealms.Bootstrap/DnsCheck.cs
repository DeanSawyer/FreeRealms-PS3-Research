using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace FreeRealms.Bootstrap;

/// <summary>
/// Asks the server's own DNS for ps3.freerealms.com, the way the game does, right after the server starts. A VPN (Proton VPN, for
/// one) or an antivirus "secure DNS" can take every DNS request on the PC, including those to 127.0.0.1, and answer them from the
/// internet; the game then never reaches the server and stops at the first loading screen with nothing in the log.
/// </summary>
public static class DnsCheck
{
    public const string Host = "ps3.freerealms.com";

    /// <summary>Null when the server's DNS answered with <paramref name="expected"/>; otherwise what went wrong, for the log.</summary>
    public static async Task<string?> ProblemAsync(IPAddress server, IPAddress expected, int port = 53)
    {
        const ushort id = 0x4652;

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await udp.SendAsync(Query(id, Host), new IPEndPoint(server, port), timeout.Token);

            var reply = await udp.ReceiveAsync(timeout.Token);
            var answer = AnswerAddress(reply.Buffer, id);

            if (answer is not null && answer.Equals(expected))
                return null;

            return answer is null
                ? $"The DNS check for {Host} got a reply the server would not send. {Blame}"
                : $"The DNS check for {Host} got {answer} instead of {expected}: the server's DNS did not answer. {Blame}";
        }
        catch (OperationCanceledException)
        {
            return $"The DNS check for {Host} got no answer within 3 seconds. {Blame}";
        }
        catch (SocketException exception)
        {
            return $"The DNS check for {Host} failed ({exception.Message}). {Blame}";
        }
    }

    private const string Blame =
        "Something on this PC is taking DNS requests before the server gets them, usually a VPN (such as Proton VPN) or an "
        + "antivirus DNS or web protection. Turn it off while you play, or the game stops at the first loading screen.";

    /// <summary>A standard query for the A record of <paramref name="host"/>.</summary>
    internal static byte[] Query(ushort id, string host)
    {
        var labels = host.Split('.');
        var packet = new byte[12 + labels.Sum(label => label.Length + 1) + 1 + 4];

        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0100); // recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1); // one question

        var position = 12;

        foreach (var label in labels)
        {
            packet[position++] = (byte)label.Length;

            foreach (var character in label)
                packet[position++] = (byte)character;
        }

        packet[position++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(position), 1); // A
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(position + 2), 1); // IN

        return packet;
    }

    /// <summary>The address in the first A record of a reply to query <paramref name="id"/>; null when there is none.</summary>
    internal static IPAddress? AnswerAddress(byte[] reply, ushort id)
    {
        if (reply.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(reply) != id)
            return null;

        var questions = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6));
        var position = 12;

        for (var i = 0; i < questions; i++)
        {
            if (!SkipName(reply, ref position) || position + 4 > reply.Length)
                return null;

            position += 4;
        }

        for (var i = 0; i < answers; i++)
        {
            if (!SkipName(reply, ref position) || position + 10 > reply.Length)
                return null;

            var type = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(position + 8));

            position += 10;

            if (position + length > reply.Length)
                return null;

            if (type == 1 && length == 4)
                return new IPAddress(reply.AsSpan(position, 4));

            position += length;
        }

        return null;
    }

    private static bool SkipName(byte[] reply, ref int position)
    {
        while (position < reply.Length)
        {
            var size = reply[position];

            if (size == 0)
            {
                position++;

                return true;
            }

            if ((size & 0xC0) == 0xC0)
            {
                position += 2;

                return position <= reply.Length;
            }

            position += 1 + size;
        }

        return false;
    }
}
