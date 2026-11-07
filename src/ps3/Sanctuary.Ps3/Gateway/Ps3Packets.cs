using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>The first packet a client sends after connecting: <c>u16 opcode 1</c>, ticket, guid, version, unknown.</summary>
public sealed record LoginFields(string Ticket, ulong Guid, string Version, string Unknown);

/// <summary>
/// The application packets the PS3 client (build 1.77) reads, as pure functions of the character, the world and the time.
/// Layouts were found by running the client's own readers (tools/ppc_trace.py) and follow Clover's packets where the order matches
/// (docs/protocol/gateway.md). Ported from <c>bootstrap/gateway_probe.py</c>. Everything here returns the packet's bytes; sending is
/// <see cref="Ps3Gateway"/>'s job, so another transport (Clover's UdpLibrary) can carry the same packets.
/// </summary>
public static class Ps3Packets
{
    /// <summary>Clover's DefaultProfileId: the first job a new character has.</summary>
    public const int ProfileId = 1;

    /// <summary>Bytes the client reads per profile entry.</summary>
    public const int ProfileSize = 179;

    public static readonly (double X, double Y, double Z, double W) SpawnRotation = (0.5531712, 0.0, 0.8330676, 0.0);

    private static readonly Dictionary<int, string> HeadNames = new() { [1] = "caucasian", [2] = "african", [3] = "asian" };

    private static readonly Dictionary<int, string> InnerNames = new()
    {
        [10] = "FinishedLoading",
        [13] = "ClientIsReady",
        [88] = "SetLocale",
        [109] = "ClientLog",
        [169] = "ClientInitializationDetails",
    };

    /// <summary>A tunneled (outer opcode 5) packet carrying <paramref name="inner"/>.</summary>
    public static byte[] Tunneled(byte[] inner, bool reliable = true) =>
        new PacketWriter().U16(5).U8(reliable ? 1 : 0).U32((uint)inner.Length).Raw(inner).ToArray();

    /// <summary>Gateway LoginReply: <c>u16 opcode 2</c>, <c>u8</c> success.</summary>
    public static byte[] LoginReply(bool success = true) => new PacketWriter().U16(2).U8(success ? 1 : 0).ToArray();

    /// <summary>Inner packet 43 as the client reads it; it must consume every byte.</summary>
    public static byte[] ZoneDetails(string name, int zoneType, int tutorial = 0, int unknown = 0, string sky = "", int arena = 0, int zoneId = 1) =>
        new PacketWriter().U16(43).Str(name).I32(zoneType).U8(tutorial).U8(unknown).Str(sky).U8(arena).I32(zoneId).ToArray();

    /// <summary>
    /// Offset of the job's rank inside a profile entry: the client's reader stores it at profile+20, which the job list's Rank column
    /// returns (0x37, the next field, is RankPercent).
    /// </summary>
    public const int ProfileRankOffset = 0x33;

    /// <summary>
    /// Rank of every job: Sanctuary's level 20. It must be above 0: the HUD script <c>GroupHandler:GetNextJob</c>, run from
    /// <c>HUD:showMain</c> right after the zone loads, loops until it finds a job with a rank, so rank 0 keeps the loading screen up forever.
    /// </summary>
    public const int ProfileRank = Jobs.Rank;

    /// <summary>
    /// One entry of the profile list (a job; the client reads 179 bytes per entry): id, name id, description id, type, icon, the three
    /// image sets and the members-only byte from <see cref="Jobs"/> (an id it does not have gets only the id), the rank at
    /// <see cref="ProfileRankOffset"/>, zeros for everything else.
    /// </summary>
    public static byte[] Profile(int profileId)
    {
        var writer = new PacketWriter();

        if (Jobs.TryGet(profileId, out var job))
        {
            writer.I32(job.Id).I32(job.NameId).I32(job.DescriptionId).I32(job.Type).I32(job.Icon)
                .I32(job.AbilityBgImageSet).I32(job.BadgeImageSet).I32(job.ButtonImageSet).U8(job.MembersOnly ? 1 : 0);
        }
        else
        {
            writer.I32(profileId);
        }

        return writer.Zeros(ProfileRankOffset - writer.Length).I32(ProfileRank).Zeros(ProfileSize - ProfileRankOffset - 4).ToArray();
    }

    /// <summary>The profile list of the player data: a count, then every job Sanctuary gives a new character (<c>FR_JOBS</c>).</summary>
    public static byte[] ProfileList(GatewayOptions options)
    {
        var chosen = Jobs.Selected(options.Jobs);
        var writer = new PacketWriter().I32(chosen.Count);

        foreach (var job in chosen)
            writer.Raw(Profile(job.Id));

        return writer.ToArray();
    }

    /// <summary>
    /// The player data of SendSelfToClient. The profile list holds the jobs Sanctuary gives a new character (all of them; <see cref="Jobs"/>,
    /// <c>FR_JOBS</c>); it must not be empty because the client looks the active profile up right after loading the player and dereferences
    /// null when the list is empty. Every other list is empty, so there are no items, pets, quests or stats yet.
    /// </summary>
    public static byte[] PlayerBlob(IReadOnlyDictionary<string, string> character, GatewayWorld world, long unixTime, int guid = 1)
    {
        var spawn = world.Spawn;

        return new PacketWriter()
            .U64(0).U64((ulong)guid) // launch ticket, guid
            .I32(Number(character, "Model", 1))
            .Str(CharacterMappings.Head(character)).Str(CharacterMappings.Hair(character))
            .I32(Number(character, "HairColor", 0)).I32(Number(character, "EyeColor", 0))
            .Str(CharacterMappings.SkinTone(character)).Str(CharacterMappings.FacePaint(character))
            .Str(CharacterMappings.ModelCustomization(character)) // skin tone, face paint, model customization
            .F32(spawn.X).F32(spawn.Y).F32(spawn.Z).F32(spawn.W) // position (the client's camera follows this player)
            .F32(SpawnRotation.X).F32(SpawnRotation.Y).F32(SpawnRotation.Z).F32(SpawnRotation.W)
            .I32(0).I32(0).I32(0) // name ids
            .Str(Text(character, "FirstName", "Test")).Str(Text(character, "LastName", string.Empty))
            .I32(0).I64(unixTime).I32(0).I32(0) // coins, birthday, age, play time
            .U8(0).I32(2).U8(0) // underage, membership status, member nag screen
            .I32(0).I32(0).I32(0).I32(0).I32(1) // chat country, chat language, preferred language, chat language, login count
            .U8(0) // grandfathered
            .I32(0).I32(0) // two vehicle loadouts (kart race, demo derby)
            .Raw(ProfileList(world.Options)) // profile list: the jobs (Jobs)
            .I32(ProfileId) // active profile id
            .Zeros(0x111 - 0xa3) // remaining fields: empty lists and zeros
            .ToArray();
    }

    /// <summary>Inner packet 12, SendSelfToClient: <c>i32</c> length, then the player blob.</summary>
    public static byte[] SendSelfToClient(IReadOnlyDictionary<string, string> character, GatewayWorld world, long unixTime)
    {
        var blob = PlayerBlob(character, world, unixTime);

        return new PacketWriter().U16(12).I32(blob.Length).Raw(blob).ToArray();
    }

    /// <summary>
    /// PlayerUpdate (opcode 35) sub-opcode 1, AddPc: put a player into the world. Follows Clover's PlayerUpdatePacketAddPc except that this
    /// build has no IsReferee flag, a three field title (no icon id) and nothing after the name offset. Equipment is empty for now, so the
    /// character is drawn without clothes.
    /// </summary>
    public static byte[] AddPc(
        IReadOnlyDictionary<string, string> character,
        GatewayWorld world,
        int guid = 2,
        string firstName = "Helper",
        string lastName = "",
        (double X, double Y, double Z)? offset = null)
    {
        var shift = offset ?? (2.0, 0.0, 2.0);
        var spawn = world.Spawn;
        var head = $"<race>_<gender>_head_{HeadName(character)}.adr";
        const string hair = "<race>_<gender>_hair_eyebrows_bald.adr";

        return new PacketWriter()
            .U16(35).U16(1).U64((ulong)guid)
            .I32(0).I32(0).I32(0).Str(firstName).Str(lastName) // name ids, names
            .I32(Number(character, "Model", 1))
            .I32(0).I32(0).I32(0) // chat bubble: foreground, background, size
            .F32(spawn.X + shift.X).F32(spawn.Y + shift.Y).F32(spawn.Z + shift.Z).F32(spawn.W)
            .F32(SpawnRotation.X).F32(SpawnRotation.Y).F32(SpawnRotation.Z).F32(SpawnRotation.W)
            .I32(0) // attachments (equipment): none yet
            .Str(head).Str(hair)
            .I32(Number(character, "HairColor", 0)).I32(Number(character, "EyeColor", 0)).I32(0) // hair colour, eye colour, unused
            .Str(SkinTone(character)).Str("none").Str(string.Empty) // skin tone, face paint, model customization
            .F32(8.0) // max movement speed
            .U8(0).U8(0).I32(0) // underage, member, temporary appearance
            .I32(0) // guilds: none
            .I32(ProfileId) // active profile id
            .I32(0).I32(0).I32(0) // title
            .I32(0) // effect tags: none
            .U64(0).I32(-1).I32(-1).F32(0.0) // mount guid, seat, queue position, name offset
            .ToArray();
    }

    /// <summary>
    /// The packets that dress the character in the clothes the character creator picked (<see cref="Clothing"/>): by default one
    /// EquippedItemsChange (PlayerUpdate sub-opcode 7), the only one seen to work in RPCS3 (the client plays the job change effect
    /// with it). With <c>FR_EQUIP=5</c> one EquipItem (ClientUpdate sub-opcode 5) per piece, item ids 1, 2, 3... as Sanctuary numbers
    /// a new character's items; with <c>FR_EQUIP=0</c> none. The player data carries no items
    /// yet, so this is what dresses the character; the gateway sends it after ClientFinishedLoading, once the character exists.
    /// </summary>
    public static List<byte[]> EquipmentPackets(IReadOnlyDictionary<string, string> character, GatewayOptions options, ulong guid = 1)
    {
        var pieces = Clothing.Attachments(character);

        if (options.EquipPacket == 0 || pieces.Count == 0)
            return [];

        if (options.EquipPacket != 5)
            return [Clothing.EquippedItemsChange(guid, pieces)];

        var packets = new List<byte[]>();

        for (var index = 0; index < pieces.Count; index++)
            packets.Add(Clothing.EquipItem(index + 1, pieces[index], ProfileId));

        return packets;
    }

    /// <summary>LobbyGameDefinition (102) sub-opcode 2: the mini-game list, empty. The client reads exactly five zero counts.</summary>
    public static byte[] LobbyDefinitionsReply()
    {
        var payload = new PacketWriter().I32(0).I32(0).I32(0).I32(0).I32(0).ToArray();

        return new PacketWriter().U16(102).U16(2).I32(payload.Length).Raw(payload).ToArray();
    }

    /// <summary>Matchmaking (141) sub-opcode 2, ListQueuesResponse: an unknown u64 and a count of queues (zero).</summary>
    public static byte[] ListQueuesReply() => new PacketWriter().U16(141).U16(2).U64(0).I32(0).ToArray();

    /// <summary>GameTimeSync (52), tunneled: i64 time, i32 server rate (8), u8 use client time (0).</summary>
    public static byte[] GameTimeSync(long unixTime) => Tunneled(new PacketWriter().U16(52).I64(unixTime).I32(8).U8(0).ToArray());

    /// <summary>
    /// What the client's WaitForWorldReady step waits for besides loading: ZoneDoneSendingInitialData (opcode 14) and the "done sending preload
    /// characters" ClientUpdate (opcode 38, sub-opcode 26, bool true; the client only counts it as received when the bool is set). Each packet
    /// is already tunneled.
    /// </summary>
    public static List<byte[]> ZoneDonePackets(GatewayOptions options, long unixTime)
    {
        var welcome = new List<byte[]>();

        // LoadWelcomeScreen (93): Clover notes the client will not render the rest of the UI unless this is sent with ShowWelcomeScreen true.
        if (options.Welcome)
            welcome.Add(Tunneled(new PacketWriter().U16(93).U8(1).I32(0).I32(0).I32(0).I32(0).ToArray()));

        if (options.Extra)
        {
            // ClientUpdate (38) sub 1 Hitpoints: i32 current, i32 max (Clover layout, untested on the PS3 client).
            welcome.Add(Tunneled(new PacketWriter().U16(38).U16(1).I32(2500).I32(2500).ToArray()));

            // ClientUpdate (38) sub 13 Mana: i32 current, i32 max, u8 show-over-head (Clover layout, untested).
            welcome.Add(Tunneled(new PacketWriter().U16(38).U16(13).I32(100).I32(100).U8(0).ToArray()));
        }

        if (options.TimeSync)
            welcome.Insert(0, GameTimeSync(unixTime));

        welcome.Add(Tunneled(new PacketWriter().U16(14).ToArray()));
        welcome.Add(Tunneled(new PacketWriter().U16(38).U16(26).U8(1).ToArray()));

        return welcome;
    }

    /// <summary>The packets sent right after the login reply, in order (each already tunneled).</summary>
    public static List<byte[]> WorldPackets(IReadOnlyDictionary<string, string> character, GatewayWorld world, long unixTime)
    {
        var packets = new List<byte[]>();
        var options = world.Options;

        if (options.Extra)
        {
            // Untested: InitializationParameters (166) is a u32 length + environment name, and Clover's ClientGameSettings (144) layout.
            packets.Add(Tunneled(new PacketWriter().U16(166).I32(4).Raw("Live"u8).ToArray()));
            packets.Add(Tunneled(new PacketWriter().U16(144).I32(4).I32(7).I32(268).U8(1).F32(1.0).ToArray()));
        }

        packets.Add(Tunneled(ZoneDetails(world.Zone, world.ZoneType)));

        // Untested: PlayerUpdate sub 37 ItemDefinitions, empty list (u32 payload length 4, u32 count 0).
        if (options.Extra)
            packets.Add(Tunneled(new PacketWriter().U16(35).U16(37).I32(4).I32(0).ToArray()));

        packets.Add(Tunneled(SendSelfToClient(character, world, unixTime)));

        return packets;
    }

    /// <summary>Decodes a gateway Login packet; null when it does not parse.</summary>
    public static LoginFields? ParseLogin(ReadOnlySpan<byte> payload)
    {
        if (!TryReadString(payload, 2, out var ticket, out var position))
            return null;

        if (position + 8 > payload.Length)
            return null;

        var guid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(position, 8));

        if (!TryReadString(payload, position + 8, out var version, out position))
            return null;

        if (!TryReadString(payload, position, out var unknown, out _))
            return null;

        return new LoginFields(ticket, guid, version, unknown);
    }

    /// <summary>A readable note for the inner client packets that are known, so the log shows what the client said.</summary>
    public static string DescribeInner(int opcode, ReadOnlySpan<byte> inner)
    {
        if (!InnerNames.TryGetValue(opcode, out var name))
            return string.Empty;

        var detail = string.Empty;

        if (opcode == 88 && inner.Length >= 6)
        {
            // SetLocale: string
            detail = " " + PythonRepr(Utf8Slice(inner, 6, BinaryPrimitives.ReadUInt32LittleEndian(inner[2..])));
        }
        else if (opcode == 169 && inner.Length >= 6)
        {
            // An i32, a timezone offset in seconds.
            detail = " " + BinaryPrimitives.ReadInt32LittleEndian(inner[2..]).ToString(CultureInfo.InvariantCulture);
        }
        else if (opcode == 109 && inner.Length >= 6)
        {
            // ClientLog: file name, text
            var length = BinaryPrimitives.ReadUInt32LittleEndian(inner[2..]);
            var nameEnd = 6L + length;

            if (nameEnd + 4 <= inner.Length)
            {
                var textLength = BinaryPrimitives.ReadUInt32LittleEndian(inner[(int)nameEnd..]);

                detail = $" {PythonRepr(Utf8Slice(inner, 6, length))}: {PythonRepr(Utf8Slice(inner, nameEnd + 4, textLength))}";
            }
        }

        return $" ({name}{detail})";
    }

    /// <summary>A string the way Python's <c>repr</c> shows it (single quotes unless the text has a single quote and no double quote).</summary>
    public static string PythonRepr(string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var result = new StringBuilder().Append(quote);

        foreach (var c in text)
        {
            if (c == quote || c == '\\')
                result.Append('\\').Append(c);
            else if (c == '\n')
                result.Append("\\n");
            else if (c == '\r')
                result.Append("\\r");
            else if (c == '\t')
                result.Append("\\t");
            else if (c < 0x20 || c == 0x7f)
                result.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else
                result.Append(c);
        }

        return result.Append(quote).ToString();
    }

    /// <summary>Bytes the way Python's <c>repr</c> shows them: <c>b'Session='</c>, non-printable bytes as <c>\xNN</c>.</summary>
    public static string PythonBytesRepr(ReadOnlySpan<byte> data)
    {
        var hasSingle = data.Contains((byte)'\'');
        var hasDouble = data.Contains((byte)'"');
        var quote = hasSingle && !hasDouble ? '"' : '\'';
        var result = new StringBuilder("b").Append(quote);

        foreach (var b in data)
        {
            if (b == quote || b == '\\')
                result.Append('\\').Append((char)b);
            else if (b == '\n')
                result.Append("\\n");
            else if (b == '\r')
                result.Append("\\r");
            else if (b == '\t')
                result.Append("\\t");
            else if (b < 0x20 || b >= 0x7f)
                result.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
            else
                result.Append((char)b);
        }

        return result.Append(quote).ToString();
    }

    private static string Utf8Slice(ReadOnlySpan<byte> data, long start, long length)
    {
        if (start >= data.Length)
            return string.Empty;

        var count = (int)Math.Min(length, data.Length - start);

        return Encoding.UTF8.GetString(data.Slice((int)start, count));
    }

    private static bool TryReadString(ReadOnlySpan<byte> buffer, int position, out string value, out int next)
    {
        value = string.Empty;
        next = position;

        if (position + 4 > buffer.Length)
            return false;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position, 4));

        position += 4;

        if (length > 4096 || position + length > buffer.Length)
            return false;

        value = Encoding.Latin1.GetString(buffer.Slice(position, (int)length));
        next = position + (int)length;

        return true;
    }

    /// <summary><c>int(str(value).strip() or default)</c> for a request field; anything that is not a number gives the default.</summary>
    private static int Number(IReadOnlyDictionary<string, string> character, string key, int fallback)
    {
        if (!character.TryGetValue(key, out var text))
            return fallback;

        text = text.Trim();

        if (text.Length == 0)
            return fallback;

        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static string HeadName(IReadOnlyDictionary<string, string> character) =>
        HeadNames.GetValueOrDefault(Number(character, "Head", 1), "caucasian");

    private static string SkinTone(IReadOnlyDictionary<string, string> character)
    {
        var tone = Number(character, "SkinTone", 1);

        return $"skintone{(tone == 0 ? 1 : tone)}";
    }

    /// <summary><c>c.get(key) or default</c>: a missing or empty value gives the default.</summary>
    private static string Text(IReadOnlyDictionary<string, string> character, string key, string fallback) =>
        character.TryGetValue(key, out var text) && text.Length > 0 ? text : fallback;
}
