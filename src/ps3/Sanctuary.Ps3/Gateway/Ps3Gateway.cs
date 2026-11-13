using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One connected PS3 client as the server manager shows it.</summary>
/// <param name="Peer">The client's address and UDP port.</param>
/// <param name="ConnectedAt">Gateway clock time of its Connect.</param>
/// <param name="LastPacketAt">Gateway clock time of the last datagram it sent.</param>
/// <param name="Packets">Datagrams received from it.</param>
/// <param name="LoggedIn">It sent the gateway Login.</param>
/// <param name="InWorld">It said ClientIsReady (the world is loading or loaded).</param>
/// <param name="OnlineId">The online id in its Login packet; empty before the login.</param>
/// <param name="ClientVersion">The client version in its Login packet; empty before the login.</param>
/// <param name="ActiveJob">The job it is playing (changes with job switches in the Sanctuary flow).</param>
public sealed record GatewaySession(
    IPEndPoint Peer,
    TimeSpan ConnectedAt,
    TimeSpan LastPacketAt,
    int Packets,
    bool LoggedIn,
    bool InWorld,
    string OnlineId,
    string ClientVersion,
    int ActiveJob);

/// <summary>Where the gateway's datagrams go. The UDP socket implements it; Clover's UdpLibrary could too.</summary>
public interface IGatewayTransport
{
    void Send(IPEndPoint peer, byte[] datagram);
}

/// <summary>
/// The server side of the PS3 client's game connection: a minimal SOE UdpLibrary peer (no CRC, no encryption) plus the application
/// packets that get the client from the login reply into the world. After <c>character/login</c> the client opens a UDP socket to
/// <c>CharacterLoginReply/GatewayAddress</c>; this completes the handshake, acknowledges reliable packets, answers the login, sends the
/// world packets (<see cref="Ps3Packets"/>) and records what the client says next.
/// </summary>
/// <remarks>
/// The class has no threads and no clock of its own: <see cref="Receive"/> takes a datagram, <see cref="Tick"/> runs what is due, and both
/// take the current (monotonic) time, so tests drive it with a virtual clock. Ported from <c>GatewayProbe</c> in
/// <c>bootstrap/gateway_probe.py</c>; the UdpLibrary packet layouts follow the Clover server (Sanctuary.UdpLibrary, AGPL-3.0):
/// <code>
/// Connect : 00 01 | protocol version i32 | connect code i32 | max raw packet size i32 | name\0
/// Confirm : 00 02 | connect code i32 | encrypt code i32 | crc bytes u8 | encrypt methods u8 x2 | max raw packet size i32 | protocol version i32
/// Reliable: 00 09+ch | sequence u16 | payload       Ack: 00 11+ch | sequence u16      (all big endian)
/// </code>
/// </remarks>
public sealed class Ps3Gateway
{
    /// <summary>The client's UdpLibrary closes the connection after three minutes of silence, so a KeepAlive goes out this often.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);

    /// <summary>When the client never says ClientIsReady, the world-ready packets are sent anyway after this long.</summary>
    public static readonly TimeSpan ReadyFallback = TimeSpan.FromSeconds(20);

    /// <summary>How long after ClientFinishedLoading the characters are put into the world.</summary>
    public static readonly TimeSpan SpawnDelay = TimeSpan.FromSeconds(1);

    /// <summary>An unacknowledged reliable packet is sent again after this long, at most <see cref="MaxTries"/> times in all.</summary>
    public static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(1);

    public const int MaxTries = 8;

    public const int MaxLoggedBytes = 160;

    private static readonly Dictionary<int, string> PacketNames = new()
    {
        [0] = "ZeroEscape", [1] = "Connect", [2] = "Confirm", [3] = "Multi", [4] = "Big", [5] = "Terminate", [6] = "KeepAlive",
        [7] = "ClockSync", [8] = "ClockReflect", [9] = "Reliable1", [10] = "Reliable2", [11] = "Reliable3", [12] = "Reliable4",
        [13] = "Fragment1", [14] = "Fragment2", [15] = "Fragment3", [16] = "Fragment4", [17] = "Ack1", [18] = "Ack2",
        [19] = "Ack3", [20] = "Ack4", [21] = "AckAll1", [22] = "AckAll2", [23] = "AckAll3", [24] = "AckAll4", [25] = "Group",
        [26] = "Ordered", [27] = "Ordered2", [28] = "PortAlive", [29] = "UnreachableConnection", [30] = "RequestRemap",
    };

    private static readonly Dictionary<int, string> TerminateReasons = new()
    {
        [0] = "none", [1] = "ICMP error", [2] = "timeout (heard nothing)", [3] = "other side terminated", [4] = "manager deleted",
        [5] = "connect failed", [6] = "application closed it", [8] = "unacknowledged timeout", [15] = "corrupt packet", [16] = "protocol mismatch",
    };

    private readonly object _gate = new();
    private readonly IGatewayTransport _transport;
    private readonly GatewayWorld _world;
    private readonly CharacterStore _characters;
    private readonly TraceLog _trace;
    private readonly ILogger? _logger;
    private readonly Func<long> _unixTime;
    private readonly Func<int> _encryptCode;
    private readonly Func<DateOnly> _today;

    private readonly Dictionary<IPEndPoint, int> _sessions = [];
    private readonly Dictionary<IPEndPoint, int> _maxRaw = [];
    private readonly HashSet<IPEndPoint> _clientReady = [];
    private readonly Dictionary<IPEndPoint, Inventory> _inventories = [];

    /// <summary>The saved character each peer plays, and whether it came back with saved progress (so it is dressed from it).</summary>
    private readonly Dictionary<IPEndPoint, (ulong Guid, bool Restored)> _playing = [];

    /// <summary>Where each peer's character stands (its last position update), and when that was last saved.</summary>
    private readonly Dictionary<IPEndPoint, Placement> _positions = [];
    private readonly Dictionary<IPEndPoint, TimeSpan> _positionSavedAt = [];

    /// <summary>How often a moving character's position is saved (Sanctuary saves it when the player leaves).</summary>
    private static readonly TimeSpan PositionSaveInterval = TimeSpan.FromSeconds(15);
    private readonly Dictionary<IPEndPoint, TimeSpan> _connectedAt = [];
    private readonly Dictionary<IPEndPoint, TimeSpan> _lastPacketAt = [];
    private readonly Dictionary<IPEndPoint, int> _packetsFrom = [];
    private readonly Dictionary<IPEndPoint, (string Version, string OnlineId)> _logins = [];
    private readonly Dictionary<(IPEndPoint Peer, int Channel), int> _sendSequence = [];
    private readonly Dictionary<(IPEndPoint Peer, int Channel, int Sequence), Pending> _pending = [];
    private readonly List<Scheduled> _scheduled = [];

    private TimeSpan _now;
    private TimeSpan? _lastKeepAlive;

    public Ps3Gateway(
        IGatewayTransport transport,
        GatewayWorld world,
        CharacterStore characters,
        TraceLog trace,
        ILogger? logger = null,
        Func<long>? unixTime = null,
        Func<int>? encryptCode = null,
        Func<DateOnly>? today = null)
    {
        _transport = transport;
        _world = world;
        _characters = characters;
        _trace = trace;
        _logger = logger;
        _unixTime = unixTime ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _encryptCode = encryptCode ?? (() => RandomNumberGenerator.GetInt32(int.MaxValue));
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>Datagrams received so far.</summary>
    public int PacketCount { get; private set; }

    /// <summary>Peers that have completed a Connect and not terminated.</summary>
    public IReadOnlyCollection<IPEndPoint> Sessions
    {
        get
        {
            lock (_gate)
                return [.. _sessions.Keys];
        }
    }

    /// <summary>The gateway clock at the last datagram or tick (the time the host passes in).</summary>
    public TimeSpan Clock
    {
        get
        {
            lock (_gate)
                return _now;
        }
    }

    /// <summary>The connected clients, oldest first, for the server manager.</summary>
    public IReadOnlyList<GatewaySession> SessionSnapshot()
    {
        lock (_gate)
        {
            return _sessions.Keys
                .Select(peer => new GatewaySession(
                    peer,
                    _connectedAt.GetValueOrDefault(peer),
                    _lastPacketAt.GetValueOrDefault(peer),
                    _packetsFrom.GetValueOrDefault(peer),
                    _logins.ContainsKey(peer),
                    _clientReady.Contains(peer),
                    _logins.TryGetValue(peer, out var login) ? login.OnlineId : string.Empty,
                    _logins.TryGetValue(peer, out var seen) ? seen.Version : string.Empty,
                    _inventories.TryGetValue(peer, out var inventory) ? inventory.Active : Jobs.StarterJob))
                .OrderBy(session => session.ConnectedAt)
                .ToList();
        }
    }

    /// <summary>Reliable packets sent and not yet acknowledged.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>Handles one datagram from <paramref name="peer"/>. Exceptions are the caller's to log; the gateway must survive odd packets.</summary>
    public void Receive(byte[] data, IPEndPoint peer, TimeSpan now)
    {
        lock (_gate)
        {
            _now = now;
            _lastPacketAt[peer] = now;
            _packetsFrom[peer] = _packetsFrom.GetValueOrDefault(peer) + 1;

            Handle(data, peer, 0);
        }
    }

    /// <summary>Runs what is due and keeps the connections alive (see <see cref="RunScheduled"/> and <see cref="Maintain"/>).</summary>
    public void Tick(TimeSpan now)
    {
        RunScheduled(now);
        Maintain(now);
    }

    /// <summary>Runs the delayed actions (the world-ready fallback and the spawn after loading) that are due at <paramref name="now"/>, in due order.</summary>
    public void RunScheduled(TimeSpan now)
    {
        lock (_gate)
        {
            _now = now;

            while (true)
            {
                var due = _scheduled.Where(item => item.Due <= now).OrderBy(item => item.Due).FirstOrDefault();

                if (due is null)
                    return;

                _scheduled.Remove(due);
                due.Run();
            }
        }
    }

    /// <summary>
    /// The client's UdpLibrary closes the connection when it hears nothing for three minutes, so every session gets a KeepAlive
    /// (<c>00 06</c>) every few seconds; unacknowledged reliable packets are sent again after a second, up to eight tries.
    /// </summary>
    public void Maintain(TimeSpan now)
    {
        lock (_gate)
        {
            _now = now;

            _lastKeepAlive ??= now;

            if (now - _lastKeepAlive >= KeepAliveInterval)
            {
                _lastKeepAlive = now;

                foreach (var peer in _sessions.Keys.ToList())
                    Transmit(peer, [0x00, 0x06]);
            }

            foreach (var (key, item) in _pending.ToList())
            {
                if (now - item.LastSent < ResendAfter)
                    continue;

                if (item.Tries >= MaxTries)
                {
                    _pending.Remove(key);

                    continue;
                }

                item.LastSent = now;
                item.Tries++;

                Transmit(key.Peer, item.Packet);
            }
        }
    }

    private void Handle(byte[] data, IPEndPoint peer, int depth)
    {
        PacketCount++;

        if (data.Length < 2 || data[0] != 0)
        {
            Record(peer, "raw", data);

            return;
        }

        var kind = data[1];
        var name = PacketNames.TryGetValue(kind, out var known) ? known : $"type{kind}";

        if (kind == 1 && data.Length >= 14)
        {
            var version = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(2, 4));
            var code = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(6, 4));
            var maxRaw = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(10, 4));
            var protocol = version > 2 ? ReadCString(data, 14) : string.Empty;

            _sessions[peer] = code;
            _maxRaw[peer] = maxRaw;
            _connectedAt[peer] = _now;
            _logins.Remove(peer);

            _logger?.LogInformation("gateway Connect from {Peer}: protocol v{Version} name='{Name}' code=0x{Code:x8} max_raw={MaxRaw}", peer, version, protocol, code, maxRaw);

            _trace.Write(
                "connect",
                ("peer", peer.Address.ToString()),
                ("version", version),
                ("name", protocol),
                ("code", code),
                ("max_raw", maxRaw),
                ("hex", Hex(data, MaxLoggedBytes)));

            var confirm = new byte[21];

            confirm[1] = 2;
            BinaryPrimitives.WriteInt32BigEndian(confirm.AsSpan(2), code);
            BinaryPrimitives.WriteInt32BigEndian(confirm.AsSpan(6), _encryptCode());
            BinaryPrimitives.WriteInt32BigEndian(confirm.AsSpan(13), maxRaw);
            BinaryPrimitives.WriteInt32BigEndian(confirm.AsSpan(17), 3);

            Transmit(peer, confirm);
        }
        else if (kind is >= 9 and <= 12)
        {
            // Reliable: acknowledge, record, interpret.
            var channel = kind - 9;

            if (data.Length >= 4)
            {
                var sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));

                Transmit(peer, Ack(17 + channel, sequence));
                Record(peer, $"{name} seq={sequence}", data[4..]);
                OnApplicationPacket(peer, channel, data[4..], 0);
            }
            else
            {
                Record(peer, name, data);
            }
        }
        else if (kind is >= 13 and <= 16)
        {
            // Fragment: acknowledge and record (the client's fragments are not reassembled).
            var channel = kind - 13;

            if (data.Length >= 4)
                Transmit(peer, Ack(17 + channel, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2))));

            Record(peer, name, data.Length >= 4 ? data[4..] : []);
        }
        else if (kind is >= 17 and <= 24)
        {
            // Ack or AckAll for what we sent.
            var channel = (kind - 17) % 4;

            if (data.Length >= 4)
            {
                var sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));

                foreach (var key in _pending.Keys.Where(key => key.Peer.Equals(peer) && key.Channel == channel && (key.Sequence == sequence || (kind >= 21 && key.Sequence <= sequence))).ToList())
                    _pending.Remove(key);
            }

            var rest = data.Length > 2 ? data[2..] : [];

            _logger?.LogInformation("gateway {Name} from {Peer}: {Hex}", name, peer, Convert.ToHexString(rest).ToLowerInvariant());
            _trace.Write("ack", ("peer", peer.Address.ToString()), ("label", name), ("hex", Convert.ToHexString(rest).ToLowerInvariant()));
        }
        else if (kind == 3 && depth < 3)
        {
            // Multi: [length][packet]...
            var position = 2;

            while (position < data.Length)
            {
                int length = data[position++];

                if (length == 0xFF && position + 2 <= data.Length)
                {
                    length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
                    position += 2;
                }

                Handle(Slice(data, position, length), peer, depth + 1);

                position += length;
            }
        }
        else if (kind == 5)
        {
            // Stop keeping this peer alive, after saving where its character stands (Sanctuary saves the position when a player leaves).
            _sessions.Remove(peer);

            if (_inventories.TryGetValue(peer, out var leaving))
                SaveProgress(peer, leaving);

            var reason = data.Length >= 8 ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2)) : -1;

            _logger?.LogInformation(
                "gateway Terminate from {Peer}: {Hex} (reason {Reason}: {Meaning})",
                peer,
                Convert.ToHexString(data[2..]).ToLowerInvariant(),
                reason,
                TerminateReasons.GetValueOrDefault(reason, "?"));

            _trace.Write("terminate", ("peer", peer.Address.ToString()), ("hex", Convert.ToHexString(data).ToLowerInvariant()));
        }
        else if (kind is 6 or 28)
        {
            // KeepAlive / PortAlive
        }
        else
        {
            Record(peer, name, data);
        }
    }

    private void OnApplicationPacket(IPEndPoint peer, int channel, byte[] payload, int depth)
    {
        if (payload.Length < 2)
            return;

        if (payload[0] == 0 && (payload[1] is 3 or 0x19) && depth < 3)
        {
            // The client bundles small application packets into one reliable payload: a Multi (3) or Group (25) packet, i.e. 00 19, then
            // [length][packet] for each one (0xff starts a two byte big endian length).
            var position = 2;

            while (position < payload.Length)
            {
                int length = payload[position++];

                if (length == 0xFF && position + 2 <= payload.Length)
                {
                    length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position, 2));
                    position += 2;
                }

                OnApplicationPacket(peer, channel, Slice(payload, position, length), depth + 1);

                position += length;
            }

            return;
        }

        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(payload);

        if (opcode == 1)
        {
            // gateway Login
            var login = Ps3Packets.ParseLogin(payload);

            if (login is null)
            {
                _logger?.LogWarning("gateway Login from {Peer} did not parse as ticket/guid/version/unknown", peer);

                return;
            }

            _logger?.LogInformation("gateway Login: ticket='{Ticket}' guid={Guid} version='{Version}' unknown='{Unknown}'", login.Ticket, login.Guid, login.Version, login.Unknown);
            _trace.Write("login", ("ticket", login.Ticket), ("guid", login.Guid), ("version", login.Version), ("unknown", login.Unknown));

            _logins[peer] = (login.Version, login.Unknown);

            SendReliable(peer, Ps3Packets.LoginReply(), channel);

            _logger?.LogInformation("ZoneDetails: zone='{Zone}' type={Type} (set FR_ZONE / FR_ZONE_TYPE to change)", _world.Zone, _world.ZoneType);

            if (_world.Options.Sanctuary)
            {
                // Sanctuary's login packets around the PS3 ZoneDetails and player data, then a session inventory for its handlers.
                var character = _characters.Snapshot();
                var options = _world.Options;
                var unixTime = _unixTime();
                var saved = _characters.Current();

                // A saved character comes back with what it had (Sanctuary loads its items and profiles from the database); a new one
                // gets Sanctuary's starting items. Without items (FR_ITEMS=0) nothing is saved or restored.
                var restored = options.Items && saved?.Progress is not null;
                var loadout = restored ? SavedProgress.ToLoadout(saved!.Progress!, _world.Zone) : Loadout.ForNewCharacter(character, options);

                // Sanctuary gives a new character its StartingCoins (login.json); a character saved before the coin store gets them once.
                if (options.StartingCoins > 0 && !(restored && saved!.Progress!.GotStartingCoins))
                {
                    loadout = loadout with { Coins = (int)Math.Min((long)loadout.Coins + options.StartingCoins, int.MaxValue) };
                    _logger?.LogInformation("gateway: starting coins: {Coins}", options.StartingCoins);
                }

                _positions.Remove(peer);
                _positionSavedAt[peer] = _now;

                if (loadout.Position is { } place)
                {
                    _positions[peer] = place;
                    _logger?.LogInformation("gateway: back where it stood: x={X:0.0} y={Y:0.0} z={Z:0.0}", place.X, place.Y, place.Z);
                }

                if (restored)
                {
                    _logger?.LogInformation(
                        "gateway: {Name} comes back with {Items} item(s) on job {Job} ({JobName}), {Coins} coin(s)",
                        saved!.Name,
                        loadout.Items.Count,
                        loadout.ActiveJob,
                        Jobs.NameOf(loadout.ActiveJob),
                        loadout.Coins);
                }

                SendNamed(peer, channel, "login", SanctuaryFlow.LoginPackets(
                    Ps3Packets.ZoneDetails(_world.Zone, _world.ZoneType, sky: options.Sky),
                    Ps3Packets.SendSelfToClient(character, _world, unixTime, loadout),
                    Items.ItemAddPackets(loadout.Items)));

                _inventories[peer] = new Inventory(loadout, options);

                if (saved is not null)
                    _playing[peer] = (saved.Guid, restored);
                else
                    _playing.Remove(peer);
            }
            else
            {
                foreach (var packet in Ps3Packets.WorldPackets(_characters.Snapshot(), _world, _unixTime()))
                    SendReliable(peer, packet, channel);
            }

            _clientReady.Remove(peer);
            Schedule(ReadyFallback, () => SendReadyFallback(peer, channel));
        }
        else if ((opcode is 5 or 6) && payload.Length >= 7)
        {
            // A tunneled client packet.
            var size = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(3, 4));
            var inner = Slice(payload, 7, size);
            var innerOpcode = inner.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(inner) : -1;

            _logger?.LogInformation(
                "gateway tunneled ({Outer}) inner opcode {Inner}{Description} from {Peer} ({Bytes} bytes): {Hex}",
                opcode,
                innerOpcode,
                Ps3Packets.DescribeInner(innerOpcode, inner),
                peer,
                inner.Length,
                Hex(inner, MaxLoggedBytes));

            _trace.Write("tunneled", ("peer", peer.Address.ToString()), ("outer", opcode), ("inner_opcode", innerOpcode), ("hex", Hex(inner, 512)));

            if (_world.Options.Sanctuary)
            {
                OnSanctuaryPacket(peer, channel, opcode, innerOpcode, inner);

                return;
            }

            var request = inner.Length >= 4 && inner[2] == 0x01 && inner[3] == 0x00;

            if (opcode == 6 && innerOpcode == 102 && request)
                SendReliable(peer, Ps3Packets.Tunneled(Ps3Packets.LobbyDefinitionsReply()), channel); // mini-game list request

            if (opcode == 6 && innerOpcode == 141 && request)
                SendReliable(peer, Ps3Packets.Tunneled(Ps3Packets.ListQueuesReply()), channel); // matchmaking queue list request

            if (innerOpcode == 52 && _world.Options.TimeSync)
                SendReliable(peer, Ps3Packets.GameTimeSync(_unixTime()), channel); // the client asks for the game time

            if (innerOpcode == 10)
                Schedule(SpawnDelay, () => SpawnCharacters(peer, channel)); // ClientFinishedLoading: now put characters in the world

            if (innerOpcode == 13)
            {
                // ClientIsReady: the initial data is complete.
                _clientReady.Add(peer);

                foreach (var packet in Ps3Packets.ZoneDonePackets(_world.Options, _unixTime()))
                    SendReliable(peer, packet, channel);
            }
        }
        else
        {
            _logger?.LogInformation("gateway application opcode {Opcode} from {Peer} ({Bytes} bytes)", opcode, peer, payload.Length);
        }
    }

    /// <summary>
    /// <c>FR_SANCTUARY=1</c>: answers the client as Sanctuary does (<see cref="SanctuaryFlow"/>), like <c>on_sanctuary_packet</c>. Inventory
    /// requests (42) and job switches (26/13) go to the session's <see cref="Inventory"/>; Sanctuary answers the mini-game list and the game
    /// time, never the matchmaking queue list (141/1), and sends no AddPc for a lone player.
    /// </summary>
    private void OnSanctuaryPacket(IPEndPoint peer, int channel, int opcode, int innerOpcode, byte[] inner)
    {
        if (innerOpcode is 42 or 26 && _inventories.TryGetValue(peer, out var inventory) && inventory.Handle(inner) is { } replies)
        {
            var sub = inner.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(2)) : -1;

            _logger?.LogInformation("sanctuary: inventory request {Opcode}/{Sub} {Hex}", innerOpcode, sub, Convert.ToHexString(inner).ToLowerInvariant());
            _trace.Write("inventory", ("peer", peer.Address.ToString()), ("opcode", innerOpcode), ("sub", sub), ("hex", Convert.ToHexString(inner).ToLowerInvariant()));

            SendNamed(peer, channel, $"inventory {innerOpcode}/{sub}", replies);

            if (replies.Count > 0)
                SaveProgress(peer, inventory);

            return;
        }

        if (_world.Options.CoinStore && innerOpcode is CoinStore.Opcode or 35 && _inventories.TryGetValue(peer, out var shopper)
            && shopper.HandleCoinStore(inner, _unixTime()) is { } shopReplies)
        {
            var sub = inner.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(2)) : -1;

            _logger?.LogInformation("sanctuary: coin store request {Opcode}/{Sub} {Hex}", innerOpcode, sub, Convert.ToHexString(inner).ToLowerInvariant());
            _trace.Write("coin_store", ("peer", peer.Address.ToString()), ("opcode", innerOpcode), ("sub", sub), ("hex", Convert.ToHexString(inner).ToLowerInvariant()));

            SendNamed(peer, channel, $"coin store {innerOpcode}/{sub}", shopReplies);

            if (innerOpcode == CoinStore.Opcode && sub is CoinStore.BuyRequestSub or CoinStore.SellRequestSub)
            {
                _logger?.LogInformation("gateway: coin store {Action}: {Coins} coin(s) left", sub == CoinStore.BuyRequestSub ? "buy" : "sell", shopper.Coins);
                SaveProgress(peer, shopper);
            }

            return;
        }

        if (innerOpcode == Placement.Opcode && Placement.FromUpdate(inner) is { } moved)
        {
            // The position update: remember where the character stands and save it now and then.
            _positions[peer] = moved;

            if (_now - _positionSavedAt.GetValueOrDefault(peer) >= PositionSaveInterval && _inventories.TryGetValue(peer, out var walking))
            {
                _positionSavedAt[peer] = _now;
                SaveProgress(peer, walking);
            }

            return;
        }

        if (opcode == 6 && innerOpcode == 102 && inner.Length >= 4 && inner[2] == 0x01 && inner[3] == 0x00)
            SendNamed(peer, channel, "lobby request", [new NamedPacket("LobbyGameDefinitions", SanctuaryFlow.LobbyDefinitionsReply())]);

        if (innerOpcode == 141)
            _logger?.LogInformation("sanctuary: no reply to opcode 141 (Sanctuary has no handler for it)");

        if (innerOpcode == 52)
            SendNamed(peer, channel, "game time request", [new NamedPacket("GameTimeSync", SanctuaryFlow.GameTimeSync(_unixTime()))]);

        if (innerOpcode == 13)
        {
            _clientReady.Add(peer);

            var character = _characters.Snapshot();

            var ready = SanctuaryFlow.ClientIsReadyPackets(
                1,
                character,
                CharacterMappings.Head(character),
                CharacterMappings.Hair(character),
                CharacterMappings.SkinTone(character),
                CharacterMappings.FacePaint(character),
                CharacterMappings.ModelCustomization(character));

            // FR_COINSTORE: the coin store's items instead of the empty list, and the coins the character has.
            if (_world.Options.CoinStore)
            {
                ready = ready.Select(packet => packet.Name == "CoinStoreItemList" ? packet with { Inner = CoinStore.ItemList() } : packet).ToList();

                if (_inventories.TryGetValue(peer, out var wallet))
                    ready.Add(new NamedPacket("CoinCount", CoinStore.CoinCount(wallet.Coins)));

                _logger?.LogInformation("gateway: coin store: {Items} item(s) listed", CoinStore.Catalog.Count);
            }

            SendNamed(peer, channel, "ClientIsReady", ready);
        }

        if (innerOpcode == 10)
        {
            var spawn = _world.Spawn;

            var activeJob = _inventories.TryGetValue(peer, out var session) ? session.Active : Ps3Packets.ProfileId;
            var (x, z) = _positions.TryGetValue(peer, out var standing) ? (standing.X, standing.Z) : (spawn.X, spawn.Z);

            SendNamed(peer, channel, "ClientFinishedLoading", SanctuaryFlow.FinishedLoadingPackets(1, x, z, activeJob, _today()));

            // The clothes are not Sanctuary's: its PC client reads them from the items in the player data.
            Schedule(SpawnDelay, () => SendEquipment(peer, channel));
        }
    }

    /// <summary>
    /// Saves what the peer's character has now, as Sanctuary writes its database: after an equip, unequip or job switch, every
    /// <see cref="PositionSaveInterval"/> while it moves, and when the client leaves.
    /// </summary>
    private void SaveProgress(IPEndPoint peer, Inventory inventory)
    {
        if (!_world.Options.Items || !_playing.TryGetValue(peer, out var playing))
            return;

        var now = inventory.Snapshot() with { Position = _positions.GetValueOrDefault(peer) };

        if (!_characters.SaveProgress(playing.Guid, SavedProgress.ToProgress(now, _world.Zone, gotStartingCoins: true)))
            return;

        _trace.Write("progress", ("peer", peer.Address.ToString()), ("guid", playing.Guid), ("job", inventory.Active));

        if (_characters.SaveProblem is { } problem)
            _logger?.LogWarning("gateway: progress not saved: {Problem}", problem);
    }

    /// <summary>Sends named inner packets tunneled, leaving out those <c>FR_SKIP</c> names and logging each by name.</summary>
    private void SendNamed(IPEndPoint peer, int channel, string stage, IEnumerable<NamedPacket> packets)
    {
        var selected = SanctuaryFlow.Select(packets, _world.Options.Skip);

        _logger?.LogInformation("sanctuary: {Stage} -> {Count} packets: {Names}", stage, selected.Count, string.Join(", ", selected.Select(packet => packet.Name)));

        foreach (var (name, inner) in selected)
        {
            var note = SanctuaryFlow.OpcodeNote(inner);

            if (note.Length > 0)
                _logger?.LogInformation("sanctuary: {Name}{Note}", name, note);

            _trace.Write("sanctuary", ("peer", peer.Address.ToString()), ("stage", stage), ("name", name), ("size", inner.Length));

            SendReliable(peer, Ps3Packets.Tunneled(inner), channel);
        }
    }

    /// <summary>Sends your character's AddPc after the client finished loading (the server marks the player visible then), then the creator's clothes.</summary>
    private void SpawnCharacters(IPEndPoint peer, int channel)
    {
        if (!_sessions.ContainsKey(peer))
            return;

        var character = _characters.Snapshot();
        var addPc = Ps3Packets.AddPc(character, _world);

        _logger?.LogInformation("gateway: AddPc for your character (guid 1) ({Bytes} bytes)", addPc.Length);

        SendReliable(peer, Ps3Packets.Tunneled(addPc), channel);

        SendEquipment(peer, channel);
    }

    /// <summary>Dresses your character (guid 1) in the clothes picked in the character creator (<see cref="Ps3Packets.EquipmentPackets"/>).</summary>
    private void SendEquipment(IPEndPoint peer, int channel)
    {
        if (!_sessions.ContainsKey(peer))
            return;

        // A saved character wears its active job's equipment; a new one the creator's clothes.
        List<byte[]> equipment;

        if (_playing.TryGetValue(peer, out var playing) && playing.Restored && _inventories.TryGetValue(peer, out var inventory))
            equipment = _world.Options.EquipPacket == 0 ? [] : [Clothing.EquippedItemsChange(1, inventory.ActiveAttachments())];
        else
            equipment = Ps3Packets.EquipmentPackets(_characters.Snapshot(), _world.Options);

        if (equipment.Count > 0)
            _logger?.LogInformation("gateway: dressing your character (guid 1) with {Count} packet(s)", equipment.Count);

        foreach (var packet in equipment)
        {
            _trace.Write("equipment", ("peer", peer.Address.ToString()), ("size", packet.Length));

            SendReliable(peer, Ps3Packets.Tunneled(packet), channel);
        }
    }

    /// <summary>
    /// If the client never says ClientIsReady, send the world-ready packets anyway so the log shows what it does next (it may be waiting for
    /// them before it says anything).
    /// </summary>
    private void SendReadyFallback(IPEndPoint peer, int channel)
    {
        if (_clientReady.Contains(peer) || _world.Options.Sanctuary) // Sanctuary has no such fallback
            return;

        _logger?.LogInformation("gateway: no ClientIsReady from {Peer} after {Seconds} s; sending ZoneDoneSendingInitialData and DoneSendingPreloadCharacters anyway", peer, (int)ReadyFallback.TotalSeconds);
        _trace.Write("fallback", ("peer", peer.Address.ToString()));

        foreach (var packet in Ps3Packets.ZoneDonePackets(_world.Options, _unixTime()))
            SendReliable(peer, packet, channel);
    }

    /// <summary>
    /// Sends one application payload reliably, splitting it into fragments if it does not fit one datagram. The PS3 client reads datagrams into
    /// a buffer of exactly the negotiated max raw size (512); a bigger one is rejected by the console's network layer and never arrives, so
    /// the 560 byte player data packet was lost. UdpLibrary's answer is a fragmented reliable packet: type 13 + channel, then the sequence
    /// number, with a 4 byte big endian total length in front of the data of the first fragment.
    /// </summary>
    private void SendReliable(IPEndPoint peer, byte[] payload, int channel)
    {
        var room = _maxRaw.GetValueOrDefault(peer, 512) - 4 - 4; // header (4) and a little slack

        if (payload.Length <= room)
        {
            SendOne(peer, channel, 9 + channel, payload, string.Empty);

            return;
        }

        var data = new byte[4 + payload.Length];

        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)payload.Length);
        payload.CopyTo(data, 4);

        var pieces = (data.Length + room - 1) / room;

        for (var number = 1; number <= pieces; number++)
        {
            var start = (number - 1) * room;

            SendOne(peer, channel, 13 + channel, data[start..Math.Min(start + room, data.Length)], $" fragment {number}/{pieces} of {payload.Length} bytes");
        }
    }

    private void SendOne(IPEndPoint peer, int channel, int kind, byte[] body, string note)
    {
        var sequence = _sendSequence.GetValueOrDefault((peer, channel), 0);

        _sendSequence[(peer, channel)] = (sequence + 1) & 0xFFFF;

        var packet = new byte[4 + body.Length];

        packet[1] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)sequence);
        body.CopyTo(packet, 4);

        _pending[(peer, channel, sequence)] = new Pending(packet, _now);

        Transmit(peer, packet);

        _logger?.LogInformation("gateway -> {Peer} Reliable{Channel} seq={Sequence} ({Bytes} bytes){Note}: {Hex}", peer, channel + 1, sequence, body.Length, note, Hex(body, MaxLoggedBytes));
        _trace.Write("send", ("peer", peer.Address.ToString()), ("channel", channel), ("seq", sequence), ("kind", kind), ("size", body.Length), ("hex", Hex(body, 512)));
    }

    private void Schedule(TimeSpan delay, Action run) => _scheduled.Add(new Scheduled(_now + delay, run));

    private void Transmit(IPEndPoint peer, byte[] datagram) => _transport.Send(peer, datagram);

    private static byte[] Ack(int kind, int sequence)
    {
        var packet = new byte[4];

        packet[1] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)sequence);

        return packet;
    }

    private void Record(IPEndPoint peer, string label, byte[] payload)
    {
        var shownLength = Math.Min(payload.Length, MaxLoggedBytes);

        _logger?.LogInformation("gateway {Label} from {Peer} ({Bytes} bytes): {Hex}{More}", label, peer, payload.Length, Hex(payload, MaxLoggedBytes), payload.Length > shownLength ? " ..." : string.Empty);
        _trace.Write("packet", ("peer", peer.Address.ToString()), ("label", label), ("size", payload.Length), ("hex", Hex(payload, 512)));
    }

    private static string Hex(byte[] data, int limit) => Convert.ToHexString(data, 0, Math.Min(data.Length, limit)).ToLowerInvariant();

    /// <summary>Python slice semantics: a range that runs past the end is cut short.</summary>
    private static byte[] Slice(byte[] data, long start, long length)
    {
        if (start >= data.Length)
            return [];

        return data[(int)start..(int)Math.Min(start + length, data.Length)];
    }

    private static string ReadCString(byte[] data, int start)
    {
        var end = Array.IndexOf(data, (byte)0, start);

        if (end < 0)
            end = data.Length;

        return Encoding.Latin1.GetString(data, start, end - start);
    }

    private sealed class Pending(byte[] packet, TimeSpan lastSent)
    {
        public byte[] Packet { get; } = packet;

        public TimeSpan LastSent { get; set; } = lastSent;

        public int Tries { get; set; } = 1;
    }

    private sealed record Scheduled(TimeSpan Due, Action Run);
}
