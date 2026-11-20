using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One packet of the Sanctuary flow and the name it is logged and skipped (<c>FR_SKIP</c>) by.</summary>
public readonly record struct NamedPacket(string Name, byte[] Inner);

/// <summary>
/// The packets Sanctuary (the PC server) sends when a player enters the world, rebuilt one for one for the PS3 server, like
/// <c>bootstrap/sanctuary_flow.py</c>. Turned on with <c>FR_SANCTUARY=1</c> (<see cref="GatewayOptions.Sanctuary"/>). Each flow is a list
/// of named inner packets in the order Sanctuary sends them; the layouts and values are Sanctuary's for a new character with no
/// guild, friends, quests, pet or items. ZoneDetails and SendSelfToClient keep the PS3 layouts, and DoneSendingPreloadCharacters is
/// sent with its bool set, because the PS3 client needs them so.
/// </summary>
public static class SanctuaryFlow
{
    /// <summary>The 1.77 dispatcher only handles inner opcodes 12 to 180.</summary>
    public const int DispatchFirst = 12;

    public const int DispatchLast = 180;

    /// <summary>Sanctuary PacketGameTimeSyncHandler: the game clock runs <see cref="GameTimeRate"/> times as fast from this epoch.</summary>
    public const long GameTimeEpoch = 1221361670;

    public const int GameTimeRate = 8;

    /// <summary>Sanctuary Player.WeaponSlot.</summary>
    public const int WeaponSlot = 7;

    /// <summary>Sanctuary AbilitySet.MaxAbilitySlots.</summary>
    public const int ToolbarSlots = 8;

    /// <summary>FabledRealms.json TileSize.</summary>
    public const int ZoneTileSize = 64;

    /// <summary>Sanctuary gateway.json Environment.</summary>
    public const string Environment = "live";

    /// <summary>SecondsSinceLastLogin for a character's first login.</summary>
    public const int FirstLoginSeconds = -1;

    private const double OnFootSpeed = 8.0;

    /// <summary>Sanctuary's CharacterStatId numbers with their values; a float value is sent as a float, the others as ints.</summary>
    private static readonly (int Id, bool IsFloat, double Value)[] Stats =
    [
        (1, false, 2500), // MaxHealth
        (2, true, OnFootSpeed), // MaxMovementSpeed
        (3, true, 5.0), // WeaponRange
        (4, false, 25), // HitPointRegen
        (5, false, 100), // MaxMana
        (6, false, 4), // ManaRegen
        (11, false, 100), // MeleeChanceToHit
        (12, true, 1.0), // MeleeWeaponDamageMultiplier
        (13, false, 1), // MeleeHandToHandDamage
        (14, false, 1), // EquippedMeleeWeaponDamage
        (15, false, 2000), // MeleeAttackIntervalMs
        (20, true, 1.0), // DamageMultiplier
        (22, true, 1.0), // HealingMultiplier
        (34, true, 1.0), // AbilityCriticalHitMultiplier
        (36, false, 100), // HeadInflationPercent
        (53, true, 1.0), // RangeMultiplier
        (56, true, 1.0), // FactoryProductionModifier
        (57, true, 1.0), // FactoryYieldModifier
        (65, false, 6), // InCombatHitPointRegen
        (66, false, 4), // InCombatManaRegen
    ];

    // Sanctuary WelcomeScreenHelper
    private static readonly (int NameId, int DescriptionId)[] WelcomeContents = [(6185, 6186), (6187, 6188), (6189, 6190)];

    private static readonly (string Code, int NameId, int DescriptionId, int IconId, string Tint)[] WelcomeClaimCodes =
    [
        ("MMMDONUT", 401519, 401534, 929, ""),
        ("BERRYCUPCAKE", 401517, 401532, 939, ""),
        ("SKELETAL", 409157, 109132, 3459, "navy"),
        ("STRAWBERRIES", 409158, 108948, 3441, "concrete"),
        ("FROGGY", 409159, 3141, 1258, ""),
        ("SANDWICH", 409160, 2430, 949, ""),
    ];

    // FabledRealms.json
    private static readonly int[] EnabledClientAreas = [4173];

    private static readonly (string Name, (int Month, int Day) Start, (int Month, int Day) End, int[] Areas)[] SeasonalClientAreas =
    [
        ("Halloween", (9, 20), (11, 11), Enumerable.Range(3992, 13).ToArray()),
    ];

    /// <summary>Area definitions with a point of interest: id, name id, area id, chat area name id, POI position (x, z).</summary>
    public static readonly (int Id, int NameId, int AreaId, int ChatNameId, (double X, double Z) Poi)[] ZoneAreas =
    [
        (2, 3500, 0, 3500, (-2140.764, 1041.53)), // Blackspore
        (3, 18140, 0, 18140, (-808.386, 1631.789)), // Briarwood
        (4, 4428, 0, 4428, (-1179.131, -434.421)), // Merryvale
        (5, 3499, 291, 3499, (-1414.636, 351.567)), // Sanctuary
        (6, 3501, 0, 3501, (-636.96, -1041.615)), // Seaside
        (7, 3502, 0, 3502, (1.167, 1114.959)), // Shrouded Glades
        (8, 3961, 290, 3961, (51.391, 363.156)), // Snowhill
        (9, 3505, 0, 3505, (-70.419, -445.165)), // Wugachug
        (10, 74159, 1178, 74159, (902.0522, 2235.3176)), // Sunstone Valley
    ];

    private const int SanctuaryAreaId = 5;

    /// <summary>The Sanctuary area's tiles: (lon = floor(z / 64), lat = floor(x / 64)).</summary>
    private static readonly HashSet<(int Lon, int Lat)> SanctuaryTiles = BuildSanctuaryTiles();

    // Sanctuary LobbyGameDefinitionPacketDefinitionsRequestHandler: one test entry (id, type, unknown, name id).
    private static readonly int[] LobbyEntry = [1, 1, 37, 3030];

    // Adventurer's journal, hard-coded in Sanctuary WorldZone.SendAdventurersJournalInfo.
    // Id, NameId, DescriptionId, TabImageId, ChapterMapImageId, GeometryId, CompletedStringId
    private static readonly int[][] JournalRegions =
    [
        [1, 5100069, 5100031, 35449, 0, 244, 5101408], [2, 442123, 5100032, 9532, 0, 5, 442681],
        [3, 3501, 2129, 9538, 0, 8, 5101409], [4, 3505, 442685, 9529, 0, 1, 442686],
    ];

    // Id, RegionId, DisplayOrder, NameId, ActiveImageSetId, ImageSetId, CompletedImageSetId, CompletedDescriptionId, MapX, MapY
    private static readonly int[][] JournalHubs =
    [
        [1, 1, 1, 442216, 19, 44308, 44310, 5100071, 0, 0], [2, 1, 2, 18735, 19, 44308, 44311, 5100072, 0, 0],
        [3, 1, 3, 5100069, 19, 44308, 44309, 5100073, 0, 0], [4, 2, 1, 7262, 19, 44308, 44941, 442125, 0, 0],
        [5, 2, 2, 428995, 19, 44308, 44942, 442126, 0, 0], [6, 2, 3, 442124, 19, 44308, 44945, 442127, 0, 0],
        [7, 2, 4, 4428, 19, 44308, 44943, 442128, 0, 0], [8, 3, 1, 5101823, 19, 44308, 45267, 5101824, 0, 0],
        [9, 3, 2, 5101825, 19, 44308, 45268, 5101826, 0, 0], [10, 4, 1, 442623, 19, 44308, 45600, 442687, 0, 0],
    ];

    // HubId, Id, Unknown (keyed by Id, the second field)
    private static readonly int[][] JournalHubQuests =
    [
        [1, 2514, 2], [1, 2513, 1], [2, 2521, 2], [2, 2526, 7], [2, 2522, 3], [2, 2523, 4], [2, 2524, 5], [2, 2525, 6],
        [3, 2529, 3], [3, 2528, 2], [3, 2527, 1], [3, 2566, 5], [3, 2530, 4], [4, 2493, 6], [4, 2492, 5], [4, 2491, 4],
        [4, 2490, 3], [4, 2489, 2], [4, 2538, 1], [5, 2498, 6], [5, 2497, 5], [5, 2496, 4], [5, 2495, 3], [5, 2494, 2],
        [5, 2531, 1], [6, 2502, 4], [6, 2501, 3], [6, 2500, 2], [6, 2499, 1], [6, 2503, 5], [7, 2533, 7], [7, 2532, 1],
        [7, 2504, 2], [7, 2508, 6], [7, 2507, 5], [7, 2505, 3], [7, 2506, 4], [8, 2580, 5], [8, 2578, 3], [8, 2579, 4],
        [8, 2577, 2], [8, 2576, 1], [9, 2585, 10], [9, 2584, 9], [9, 2583, 8], [9, 2582, 7], [9, 2581, 6], [9, 2600, 11],
        [10, 2595, 6], [10, 2594, 5], [10, 2591, 4], [10, 2590, 3], [10, 2596, 7], [10, 2588, 1], [10, 2599, 10],
        [10, 2598, 9], [10, 2597, 8], [10, 2589, 2],
    ];

    // Id, RegionId, DisplayOrder, QuestId, NameId, DescriptionId, CompletedImageSetId, ImageSetId, Unknown
    private static readonly int[][] JournalStickers =
    [
        [1, 1, 1, 2563, 5100479, 5100480, 43279, 43278, 0], [2, 1, 2, 2564, 5100483, 5100484, 43287, 43286, 0],
        [3, 1, 3, 2565, 5100487, 5100488, 43273, 43272, 0], [4, 1, 4, 2572, 5100772, 5100773, 43281, 43280, 0],
        [5, 1, 5, 2573, 5100776, 5100777, 43291, 43290, 0], [6, 1, 6, 2587, 5101187, 5101188, 43283, 43282, 0],
        [16, 2, 1, 2568, 5100756, 5100757, 43305, 43304, 0], [17, 2, 2, 2569, 5100760, 5100761, 43287, 43286, 0],
        [18, 2, 3, 2570, 5100764, 5100765, 43273, 43272, 0], [19, 2, 4, 2571, 5100768, 5100769, 43279, 43278, 0],
        [20, 2, 5, 2574, 5100780, 5100781, 43277, 43276, 0], [21, 2, 6, 2575, 5100784, 5100785, 43283, 43282, 0],
        [32, 3, 2, 2602, 442851, 442857, 43287, 43286, 0], [35, 3, 5, 2605, 442854, 442860, 43279, 43278, 0],
        [36, 3, 6, 2606, 442855, 442861, 43305, 43304, 0], [37, 4, 1, 2592, 0, 0, 0, 0, 0],
    ];

    /// <summary>
    /// After LoginReply (Sanctuary PacketLoginHandler): combat flag, initialization parameters, zone, game settings, item definitions,
    /// player data, then one ItemAdd per item (<paramref name="itemAdds"/>; the PS3 client only creates items from them, once the player
    /// exists). <paramref name="zoneDetails"/> and <paramref name="selfToClient"/> are the PS3 server's own packets.
    /// </summary>
    public static List<NamedPacket> LoginPackets(byte[] zoneDetails, byte[] selfToClient, IReadOnlyList<byte[]> itemAdds)
    {
        var packets = new List<NamedPacket>
        {
            new NamedPacket("EncounterOverworldCombat", new PacketWriter().U16(41).U16(132).U8(1).ToArray()),
            new NamedPacket("InitializationParameters", new PacketWriter().U16(166).Str(Environment).ToArray()),
            new NamedPacket("ZoneDetails", zoneDetails),
            new NamedPacket("ClientGameSettings", new PacketWriter().U16(144).I32(4).I32(7).I32(268).U8(1).F32(1.0).ToArray()),
            new NamedPacket("ItemDefinitions", new PacketWriter().U16(35).U16(37).I32(4).I32(0).ToArray()),
            new NamedPacket("SendSelfToClient", selfToClient),
        };

        foreach (var add in itemAdds)
            packets.Add(new NamedPacket($"ItemAdd {BinaryPrimitives.ReadInt32LittleEndian(add.AsSpan(16, 4))}", add));

        return packets;
    }

    /// <summary>Sanctuary SendPlayerCustomizations (PlayerUpdate 35/65): id, string param, param, item id (always 0) per row.</summary>
    public static byte[] Customizations(
        IReadOnlyDictionary<string, string> character,
        string head,
        string hair,
        string skin,
        string facePaint = "none",
        string modelCustomization = "")
    {
        int Num(string key) => CharacterMappings.Number(character, key, 0);

        var model = Num("Model");
        var rows = new (int Id, string Text, int Param)[]
        {
            (0, head, Num("Head")),
            (1, skin, Num("SkinTone")),
            (2, hair, Num("Hair")),
            (3, string.Empty, Num("HairColor")),
            (4, string.Empty, Num("EyeColor")),
            (5, modelCustomization, Num("ModelCustomization")),
            (6, facePaint, Num("FacePaint")),
            (8, string.Empty, model != 0 ? model : 1),
        };

        var writer = new PacketWriter().U16(35).U16(65).I32(rows.Length);

        foreach (var (id, text, param) in rows)
            writer.I32(id).Str(text).I32(param).I32(0);

        return writer.ToArray();
    }

    /// <summary>Adventurer's journal (209): regions, hubs, hub quests and stickers, each a dictionary (count, then key and record).</summary>
    public static byte[] AdventurersJournal()
    {
        var writer = new PacketWriter().U16(209).I32(1);

        WriteDictionary(writer, JournalRegions, 0);
        WriteDictionary(writer, JournalHubs, 0);
        WriteDictionary(writer, JournalHubQuests, 1);
        WriteDictionary(writer, JournalStickers, 0);

        return writer.ToArray();
    }

    /// <summary>AnnouncementDataSend (193, sub 2) with Sanctuary's one announcement.</summary>
    public static byte[] Announcements() =>
        new PacketWriter().U16(193).U8(2).I32(1)
            .I32(1).I32(1).I32(38443).I32(414).I32(415).I32(436323) // id, priority, icon, title, body, button
            .Str("Marketplace").Str(string.Empty)
            .I32(0).I32(31415).I32(0)
            .Str(string.Empty)
            .ToArray();

    /// <summary>LoadWelcomeScreen (93) as Sanctuary fills it: show flag, contents, claim codes, seconds since last login, station cash.</summary>
    public static byte[] WelcomeScreen(int secondsSinceLastLogin = FirstLoginSeconds, int stationCash = 0)
    {
        var writer = new PacketWriter().U16(93).U8(1).I32(WelcomeContents.Length);

        foreach (var (nameId, descriptionId) in WelcomeContents)
            writer.I32(nameId).I32(descriptionId);

        writer.I32(WelcomeClaimCodes.Length);

        foreach (var (code, nameId, descriptionId, iconId, tint) in WelcomeClaimCodes)
            writer.Str(code).I32(nameId).I32(descriptionId).I32(iconId).Str(tint);

        return writer.I32(secondsSinceLastLogin).I32(stationCash).ToArray();
    }

    /// <summary>Sanctuary BaseZone.OnClientIsReady followed by WorldZone.OnClientIsReady, for a new character.</summary>
    public static List<NamedPacket> ClientIsReadyPackets(
        ulong guid,
        IReadOnlyDictionary<string, string> character,
        string head,
        string hair,
        string skin,
        string facePaint = "none",
        string modelCustomization = "",
        bool isMember = true)
    {
        var stats = new PacketWriter().U16(38).U16(7).U64(guid).I32(Stats.Length);

        foreach (var (id, isFloat, value) in Stats)
        {
            if (isFloat)
                stats.I32(id).I32(1).F32(value);
            else
                stats.I32(id).I32(0).I32((int)value);
        }

        return
        [
            // BaseZone
            new NamedPacket("QuickChatSendData", new PacketWriter().U16(67).U16(1).I32(0).ToArray()),
            new NamedPacket("UpdateStat", stats.ToArray()),
            new NamedPacket("Hitpoints", new PacketWriter().U16(38).U16(1).I32(2500).I32(2500).ToArray()),
            new NamedPacket("Mana", new PacketWriter().U16(38).U16(13).I32(100).I32(100).U8(0).ToArray()),
            new NamedPacket("UpdateActiveWieldType", new PacketWriter().U16(35).U16(61).U64(guid).I32(0).ToArray()),
            new NamedPacket("GuildCanCreateGuild", new PacketWriter().U16(129).U16(32).U8(0).ToArray()),
            new NamedPacket("ItemClassDefinitions", ReferenceData.ItemClassDefinitions()),
            new NamedPacket("ItemCategoryDefinitions", ReferenceData.ItemCategoryDefinitions()),
            new NamedPacket("ClientProfileData", new PacketWriter().U16(44).U16(3).I32(0).ToArray()),
            new NamedPacket("CustomizationData", Customizations(character, head, hair, skin, facePaint, modelCustomization)),
            new NamedPacket("MembershipSubscriptionInfo", new PacketWriter().U16(189).U8(isMember ? 1 : 0).U8(0).ToArray()),
            new NamedPacket("EffectTagCompositeEffectsEnable", new PacketWriter().U16(35).U16(43).U8(1).ToArray()),
            new NamedPacket("ZoneDoneSendingInitialData", new PacketWriter().U16(14).ToArray()),
            new NamedPacket("DoneSendingPreloadCharacters", new PacketWriter().U16(38).U16(26).U8(1).ToArray()),
            new NamedPacket("FriendList", new PacketWriter().U16(74).U8(1).I32(0).ToArray()),
            new NamedPacket("IgnoreList", new PacketWriter().U16(112).U8(1).I32(0).ToArray()),

            // WorldZone
            new NamedPacket("PointOfInterestDefinitionReply", new PacketWriter().U16(57).I32(1).U8(0).ToArray()),
            new NamedPacket("AdventurersJournalInfo", AdventurersJournal()),
            new NamedPacket("CoinStoreItemList", new PacketWriter().U16(165).U16(1).I32(0).I32(0).ToArray()),
            new NamedPacket("InGamePurchaseEnableMarketplace", new PacketWriter().U16(66).U16(24).U8(1).ToArray()),
            new NamedPacket("InGamePurchaseEnablePaymentSources", new PacketWriter().U16(66).U16(33).U8(1).U8(1).ToArray()),
            new NamedPacket("InGamePurchaseBundleCategoryGroups", new PacketWriter().U16(66).U16(6).I32(0).ToArray()),
            new NamedPacket("InGamePurchaseBundleCategories", new PacketWriter().U16(66).U16(7).I32(0).U8(0).ToArray()),
            new NamedPacket("InGamePurchaseBundleGroups", new PacketWriter().U16(66).U16(9).I32(0).ToArray()),
            new NamedPacket("AnnouncementDataSend", Announcements()),
            new NamedPacket("LoadWelcomeScreen", WelcomeScreen()),
            new NamedPacket("ObjectiveTargetUpdate", new PacketWriter().U16(47).U8(14).U8(0).ToArray()),
        ];
    }

    /// <summary>WorldZone.SendEnabledClientAreas: the zone's areas, then the seasonal ones active on <paramref name="today"/>.</summary>
    public static List<int> ClientAreas(DateOnly today)
    {
        var ids = new List<int>(EnabledClientAreas);
        var day = (today.Month, today.Day);

        foreach (var (_, start, end, areas) in SeasonalClientAreas)
        {
            var active = Compare(start, end) <= 0
                ? Compare(start, day) <= 0 && Compare(day, end) <= 0
                : Compare(day, start) >= 0 || Compare(day, end) <= 0;

            if (active)
                ids.AddRange(areas);
        }

        return ids;
    }

    /// <summary>WorldZone.GetNearestZoneArea without the terrain-environment lookup: the Sanctuary area by its tiles, else the nearest POI.</summary>
    public static (int Id, int NameId, int AreaId, int ChatNameId, (double X, double Z) Poi) NearestArea(double x, double z)
    {
        var tile = ((int)Math.Floor(z / ZoneTileSize), (int)Math.Floor(x / ZoneTileSize));

        if (SanctuaryTiles.Contains(tile))
            return ZoneAreas.First(area => area.Id == SanctuaryAreaId);

        var best = ZoneAreas[0];
        var bestDistance = double.MaxValue;

        foreach (var area in ZoneAreas)
        {
            var dx = area.Poi.X - x;
            var dz = area.Poi.Z - z;
            var distance = dx * dx + dz * dz;

            if (distance < bestDistance)
            {
                best = area;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Sanctuary PacketClientFinishedLoadingHandler for a lone player with no mount, pet or weapon: client areas, point of interest and
    /// chat area, weapon flair override, toolbar. No AddPc: Sanctuary only sends AddPc for other players.
    /// </summary>
    public static List<NamedPacket> FinishedLoadingPackets(ulong guid, double x, double z, int profileId, DateOnly today)
    {
        var packets = ClientAreas(today)
            .Select(area => new NamedPacket($"SetClientArea {area}", new PacketWriter().U16(89).I32(area).U8(1).I32(0).ToArray()))
            .ToList();

        var nearest = NearestArea(x, z);

        packets.Add(new NamedPacket("POIChangeMessage", new PacketWriter().U16(104).I32(nearest.NameId).I32(nearest.Id).I32(nearest.AreaId).ToArray()));
        packets.Add(new NamedPacket("ChatEnterArea", new PacketWriter().U16(15).U16(2).I32(nearest.ChatNameId).ToArray()));
        packets.Add(new NamedPacket("SlotCompositeEffectOverride", new PacketWriter().U16(35).U16(31).U64(guid).I32(WeaponSlot).I32(0).ToArray()));
        packets.Add(new NamedPacket("AbilitySetDefinition", Toolbar(profileId)));

        return packets;
    }

    /// <summary>Ability (36) sub-opcode 5 with an empty ability set: Sanctuary's SendToolbar for a job without a combat kit.</summary>
    public static byte[] Toolbar(int profileId) =>
        new PacketWriter().U16(36).U16(5).I32(profileId).I32(ToolbarSlots).Zeros(4 * ToolbarSlots).ToArray();

    /// <summary>Sanctuary PacketGameTimeSyncHandler: the game clock runs 8 times as fast from its epoch; use client time false.</summary>
    public static byte[] GameTimeSync(long unixTime) =>
        new PacketWriter().U16(52).I64(GameTimeRate * (unixTime - GameTimeEpoch)).I32(GameTimeRate).U8(0).ToArray();

    /// <summary>Sanctuary LobbyGameDefinitionPacketDefinitionsRequestHandler: one test lobby game, four empty lists.</summary>
    public static byte[] LobbyDefinitionsReply()
    {
        var body = new PacketWriter().I32(1).I32(LobbyEntry[0]);

        foreach (var value in LobbyEntry)
            body.I32(value);

        var data = body.I32(0).I32(0).I32(0).I32(0).ToArray();

        return new PacketWriter().U16(102).U16(2).I32(data.Length).Raw(data).ToArray();
    }

    /// <summary>Leaves out the packets named in <paramref name="skip"/> (lower case; "setclientarea" drops every area packet).</summary>
    public static List<NamedPacket> Select(IEnumerable<NamedPacket> packets, IReadOnlySet<string> skip) =>
        packets.Where(packet => !skip.Contains(packet.Name.ToLowerInvariant()) && !skip.Contains(packet.Name.Split(' ')[0].ToLowerInvariant())).ToList();

    /// <summary>A note for the log when the packet's opcode is outside the 1.77 dispatcher's range; empty otherwise.</summary>
    public static string OpcodeNote(byte[] inner)
    {
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(inner);

        return opcode is >= DispatchFirst and <= DispatchLast ? string.Empty : $" (opcode {opcode} is outside the 1.77 dispatcher's 12-180)";
    }

    private static void WriteDictionary(PacketWriter writer, int[][] records, int keyField)
    {
        writer.I32(records.Length);

        foreach (var record in records)
        {
            writer.I32(record[keyField]);

            foreach (var value in record)
                writer.I32(value);
        }
    }

    private static int Compare((int Month, int Day) a, (int Month, int Day) b) =>
        a.Month != b.Month ? a.Month.CompareTo(b.Month) : a.Day.CompareTo(b.Day);

    private static HashSet<(int Lon, int Lat)> BuildSanctuaryTiles()
    {
        var tiles = new HashSet<(int Lon, int Lat)>();

        void Add(int lon, int fromLat, int toLat)
        {
            for (var lat = fromLat; lat < toLat; lat++)
                tiles.Add((lon, lat));
        }

        Add(-1, -23, -19);
        Add(0, -24, -17);
        Add(1, -24, -17);

        for (var lon = 2; lon < 8; lon++)
            Add(lon, -24, -13);

        Add(8, -24, -14);
        Add(9, -24, -15);
        Add(10, -24, -20);

        return tiles;
    }
}
