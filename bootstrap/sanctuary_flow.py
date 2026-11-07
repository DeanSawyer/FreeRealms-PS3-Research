"""The packets Sanctuary (the PC server, github.com/Open-Source-Free-Realms/Sanctuary) sends when a player enters
the world, rebuilt one for one for the PS3 test server. Turned on with FR_SANCTUARY=1 (gateway_probe.py).

Each function returns a list of (name, inner packet) in the order Sanctuary sends them. The layouts are copied from
Sanctuary's own serializers (Sanctuary.Packet, commit of 2026-11-03); the values are the ones Sanctuary uses for a new
character with no guild, friends, quests, pet or items. Where Sanctuary reads its resource files (quick chats, item
classes, profiles, points of interest, the coin store), the lists are sent empty for now: the record layouts of those
files have not been checked against build 1.77.

Kept from the PS3 server instead of Sanctuary, because the PS3 client is known to need them:
  * ZoneDetails and SendSelfToClient keep the PS3 layouts (read from the client, docs/protocol/gateway.md);
  * DoneSendingPreloadCharacters is sent with its bool set (the PS3 client only counts it then; Sanctuary sends 0).

The 1.77 dispatcher only handles inner opcodes 12..180 (docs/protocol/gateway.md, Inner packet 93). Sanctuary's
packets above that range (189, 193, 209) are still sent, as Sanctuary does, and logged as out of range.

FR_SKIP="Name,Name" leaves out single packets by the names used here (for finding one that upsets the client).
"""

import datetime
import os
import struct

import reference_data

PS3_DISPATCH_RANGE = range(12, 181)

GAME_TIME_EPOCH = 1221361670    # Sanctuary PacketGameTimeSyncHandler
GAME_TIME_RATE = 8

WEAPON_SLOT = 7                 # Sanctuary Player.WeaponSlot
ON_FOOT_SPEED = 8.0             # Sanctuary Player.OnFootMovementSpeed
TOOLBAR_SLOTS = 8               # Sanctuary AbilitySet.MaxAbilitySlots
ZONE_TILE_SIZE = 64             # FabledRealms.json TileSize
ENVIRONMENT = "live"            # Sanctuary gateway.json Environment

# Stat id numbers from Sanctuary's CharacterStatId enum; (id, value) with int or float values as Sanctuary sends them.
STATS = (
    (1, 2500),          # MaxHealth
    (2, ON_FOOT_SPEED), # MaxMovementSpeed (player.Stats, on foot)
    (3, 5.0),           # WeaponRange
    (4, 25),            # HitPointRegen
    (5, 100),           # MaxMana
    (6, 4),             # ManaRegen
    (11, 100),          # MeleeChanceToHit
    (12, 1.0),          # MeleeWeaponDamageMultiplier
    (13, 1),            # MeleeHandToHandDamage
    (14, 1),            # EquippedMeleeWeaponDamage
    (15, 2000),         # MeleeAttackIntervalMs
    (20, 1.0),          # DamageMultiplier
    (22, 1.0),          # HealingMultiplier
    (34, 1.0),          # AbilityCriticalHitMultiplier
    (36, 100),          # HeadInflationPercent
    (53, 1.0),          # RangeMultiplier
    (56, 1.0),          # FactoryProductionModifier
    (57, 1.0),          # FactoryYieldModifier
    (65, 6),            # InCombatHitPointRegen
    (66, 4),            # InCombatManaRegen
)

# Sanctuary WelcomeScreenHelper
ANNOUNCEMENT = dict(id=1, priority=1, icon=38443, title=414, body=415, button=436323, lua_call="Marketplace", param2=31415)
WELCOME_CONTENTS = ((6185, 6186), (6187, 6188), (6189, 6190))
WELCOME_CLAIM_CODES = (      # code, name id, description id, icon id, tint alias
    ("MMMDONUT", 401519, 401534, 929, ""),
    ("BERRYCUPCAKE", 401517, 401532, 939, ""),
    ("SKELETAL", 409157, 109132, 3459, "navy"),
    ("STRAWBERRIES", 409158, 108948, 3441, "concrete"),
    ("FROGGY", 409159, 3141, 1258, ""),
    ("SANDWICH", 409160, 2430, 949, ""),
)
FIRST_LOGIN_SECONDS = -1     # SecondsSinceLastLogin for a character's first login

# FabledRealms.json
ENABLED_CLIENT_AREAS = (4173,)
SEASONAL_CLIENT_AREAS = (    # name, (start month, day), (end month, day), area ids
    ("Halloween", (9, 20), (11, 11), tuple(range(3992, 4005))),
)
# Area definitions that have a point of interest: id, name id, area id, chat area name id, POI position (x, z).
# Positions from PointOfInterests.json. The Sanctuary area is also matched by its tiles (lon = floor(z/64),
# lat = floor(x/64)); the areas Sanctuary matches by terrain environment are only reached through the nearest POI
# here, because the environment map is not part of this repository.
ZONE_AREAS = (
    (2, 3500, 0, 3500, (-2140.764, 1041.53)),        # Blackspore
    (3, 18140, 0, 18140, (-808.386, 1631.789)),      # Briarwood
    (4, 4428, 0, 4428, (-1179.131, -434.421)),       # Merryvale
    (5, 3499, 291, 3499, (-1414.636, 351.567)),      # Sanctuary
    (6, 3501, 0, 3501, (-636.96, -1041.615)),        # Seaside
    (7, 3502, 0, 3502, (1.167, 1114.959)),           # Shrouded Glades
    (8, 3961, 290, 3961, (51.391, 363.156)),         # Snowhill
    (9, 3505, 0, 3505, (-70.419, -445.165)),         # Wugachug
    (10, 74159, 1178, 74159, (902.0522, 2235.3176)), # Sunstone Valley
)
SANCTUARY_AREA_ID = 5
SANCTUARY_TILES = frozenset(
    [(-1, lat) for lat in range(-23, -19)] + [(0, lat) for lat in range(-24, -17)] + [(1, lat) for lat in range(-24, -17)]
    + [(lon, lat) for lon in range(2, 8) for lat in range(-24, -13)]
    + [(8, lat) for lat in range(-24, -14)] + [(9, lat) for lat in range(-24, -15)] + [(10, lat) for lat in range(-24, -20)])

# Sanctuary LobbyGameDefinitionPacketDefinitionsRequestHandler: one test entry
LOBBY_ENTRY = (1, 1, 37, 3030)   # id, type, unknown, name id

# Adventurer's journal, hard-coded in Sanctuary WorldZone.SendAdventurersJournalInfo ("needed due to an Area
# Definition called Newbiezone in FabledRealmsAreas.xml"). Extracted from Sanctuary's source, in its order.
# fields: Id, NameId, DescriptionId, TabImageId, ChapterMapImageId, GeometryId, CompletedStringId
AJ_REGIONS = (
    (1, 5100069, 5100031, 35449, 0, 244, 5101408), (2, 442123, 5100032, 9532, 0, 5, 442681),
    (3, 3501, 2129, 9538, 0, 8, 5101409), (4, 3505, 442685, 9529, 0, 1, 442686),
)

# fields: Id, RegionId, DisplayOrder, NameId, ActiveImageSetId, ImageSetId, CompletedImageSetId, CompletedDescriptionId, MapX, MapY
AJ_HUBS = (
    (1, 1, 1, 442216, 19, 44308, 44310, 5100071, 0, 0), (2, 1, 2, 18735, 19, 44308, 44311, 5100072, 0, 0),
    (3, 1, 3, 5100069, 19, 44308, 44309, 5100073, 0, 0), (4, 2, 1, 7262, 19, 44308, 44941, 442125, 0, 0),
    (5, 2, 2, 428995, 19, 44308, 44942, 442126, 0, 0), (6, 2, 3, 442124, 19, 44308, 44945, 442127, 0, 0),
    (7, 2, 4, 4428, 19, 44308, 44943, 442128, 0, 0), (8, 3, 1, 5101823, 19, 44308, 45267, 5101824, 0, 0),
    (9, 3, 2, 5101825, 19, 44308, 45268, 5101826, 0, 0), (10, 4, 1, 442623, 19, 44308, 45600, 442687, 0, 0),
)

# fields: HubId, Id, Unknown
AJ_HUB_QUESTS = (
    (1, 2514, 2), (1, 2513, 1), (2, 2521, 2), (2, 2526, 7), (2, 2522, 3), (2, 2523, 4), (2, 2524, 5), (2, 2525, 6),
    (3, 2529, 3), (3, 2528, 2), (3, 2527, 1), (3, 2566, 5), (3, 2530, 4), (4, 2493, 6), (4, 2492, 5), (4, 2491, 4),
    (4, 2490, 3), (4, 2489, 2), (4, 2538, 1), (5, 2498, 6), (5, 2497, 5), (5, 2496, 4), (5, 2495, 3), (5, 2494, 2),
    (5, 2531, 1), (6, 2502, 4), (6, 2501, 3), (6, 2500, 2), (6, 2499, 1), (6, 2503, 5), (7, 2533, 7), (7, 2532, 1),
    (7, 2504, 2), (7, 2508, 6), (7, 2507, 5), (7, 2505, 3), (7, 2506, 4), (8, 2580, 5), (8, 2578, 3), (8, 2579, 4),
    (8, 2577, 2), (8, 2576, 1), (9, 2585, 10), (9, 2584, 9), (9, 2583, 8), (9, 2582, 7), (9, 2581, 6), (9, 2600, 11),
    (10, 2595, 6), (10, 2594, 5), (10, 2591, 4), (10, 2590, 3), (10, 2596, 7), (10, 2588, 1), (10, 2599, 10),
    (10, 2598, 9), (10, 2597, 8), (10, 2589, 2),
)

# fields: Id, RegionId, DisplayOrder, QuestId, NameId, DescriptionId, CompletedImageSetId, ImageSetId, Unknown
AJ_STICKERS = (
    (1, 1, 1, 2563, 5100479, 5100480, 43279, 43278, 0), (2, 1, 2, 2564, 5100483, 5100484, 43287, 43286, 0),
    (3, 1, 3, 2565, 5100487, 5100488, 43273, 43272, 0), (4, 1, 4, 2572, 5100772, 5100773, 43281, 43280, 0),
    (5, 1, 5, 2573, 5100776, 5100777, 43291, 43290, 0), (6, 1, 6, 2587, 5101187, 5101188, 43283, 43282, 0),
    (16, 2, 1, 2568, 5100756, 5100757, 43305, 43304, 0), (17, 2, 2, 2569, 5100760, 5100761, 43287, 43286, 0),
    (18, 2, 3, 2570, 5100764, 5100765, 43273, 43272, 0), (19, 2, 4, 2571, 5100768, 5100769, 43279, 43278, 0),
    (20, 2, 5, 2574, 5100780, 5100781, 43277, 43276, 0), (21, 2, 6, 2575, 5100784, 5100785, 43283, 43282, 0),
    (32, 3, 2, 2602, 442851, 442857, 43287, 43286, 0), (35, 3, 5, 2605, 442854, 442860, 43279, 43278, 0),
    (36, 3, 6, 2606, 442855, 442861, 43305, 43304, 0), (37, 4, 1, 2592, 0, 0, 0, 0, 0),
)


# --- writer helpers (Sanctuary PacketWriter: little endian, strings are i32 length + UTF-8, empty string = 0) ---

def _s(value):
    raw = (value or "").encode("utf-8")
    return struct.pack("<i", len(raw)) + raw


def _b(value):
    return struct.pack("<B", 1 if value else 0)


def _payload(data):
    return struct.pack("<i", len(data)) + data


def _stat(stat_id, value):
    if isinstance(value, float):
        return struct.pack("<iif", stat_id, 1, value)
    return struct.pack("<iii", stat_id, 0, value)


def _dict(records):
    """Dictionary<int, T>: count, then key and record for each (the key is the record's first field here)."""
    out = struct.pack("<i", len(records))
    for rec in records:
        out += struct.pack("<i", rec[0]) + struct.pack("<%di" % len(rec), *rec)
    return out


def _hub_quests(records):
    """HubQuests are keyed by Id, the second field (the record starts with HubId)."""
    out = struct.pack("<i", len(records))
    for rec in records:
        out += struct.pack("<i", rec[1]) + struct.pack("<3i", *rec)
    return out


# --- the flows -----------------------------------------------------------------------------------------------

def login_packets(zone_details, self_to_client, item_definitions=()):
    """After LoginReply (Sanctuary PacketLoginHandler): combat flag, initialization parameters, zone, game
    settings, item definitions, player data. zone_details and self_to_client are the PS3 server's own packets.
    item_definitions (PlayerUpdate sub-opcode 33, one per definition) come after the player data: Sanctuary sends its
    definitions in one sub-opcode 37 list before it, but the PS3 client only takes one once the player exists."""
    return [
        ("EncounterOverworldCombat", struct.pack("<HHB", 41, 132, 1)),
        ("InitializationParameters", struct.pack("<H", 166) + _s(ENVIRONMENT)),
        ("ZoneDetails", zone_details),
        ("ClientGameSettings", struct.pack("<HiiiBf", 144, 4, 7, 268, 1, 1.0)),
        ("ItemDefinitions", struct.pack("<HH", 35, 37) + _payload(struct.pack("<i", 0))),
        ("SendSelfToClient", self_to_client),
    ] + [("ItemDefinition %d" % struct.unpack_from("<i", p, 8)[0], p) for p in item_definitions]


def customizations(character, head, hair, skin, face_paint="none", model_customization=""):
    """Sanctuary SendPlayerCustomizations: id, string param, param, item id (always 0)."""
    c = character or {}

    def num(key):
        try:
            return int(c.get(key) or 0)
        except (TypeError, ValueError):
            return 0

    rows = (
        (0, head, num("Head")),
        (1, skin, num("SkinTone")),
        (2, hair, num("Hair")),
        (3, "", num("HairColor")),
        (4, "", num("EyeColor")),
        (5, model_customization, num("ModelCustomization")),
        (6, face_paint, num("FacePaint")),
        (8, "", num("Model") or 1),
    )
    out = struct.pack("<HHi", 35, 65, len(rows))
    for row_id, text, param in rows:
        out += struct.pack("<i", row_id) + _s(text) + struct.pack("<ii", param, 0)
    return out


def adventurers_journal():
    return (struct.pack("<Hi", 209, 1) + _dict(AJ_REGIONS) + _dict(AJ_HUBS) + _hub_quests(AJ_HUB_QUESTS)
            + _dict(AJ_STICKERS))


def announcements():
    a = ANNOUNCEMENT
    record = (struct.pack("<6i", a["id"], a["priority"], a["icon"], a["title"], a["body"], a["button"])
              + _s(a["lua_call"]) + _s("") + struct.pack("<3i", 0, a["param2"], 0) + _s(""))
    return struct.pack("<HBi", 193, 2, 1) + record


def welcome_screen(seconds_since_last_login=FIRST_LOGIN_SECONDS, station_cash=0):
    """LoadWelcomeScreen (93) as Sanctuary fills it. Same layout the PS3 handler accepts (show flag, pairs, claim
    codes, two integers; docs/protocol/gateway.md)."""
    out = struct.pack("<HB", 93, 1)
    out += struct.pack("<i", len(WELCOME_CONTENTS))
    for name_id, description_id in WELCOME_CONTENTS:
        out += struct.pack("<ii", name_id, description_id)
    out += struct.pack("<i", len(WELCOME_CLAIM_CODES))
    for code, name_id, description_id, icon_id, tint in WELCOME_CLAIM_CODES:
        out += _s(code) + struct.pack("<iii", name_id, description_id, icon_id) + _s(tint)
    return out + struct.pack("<ii", seconds_since_last_login, station_cash)


def client_is_ready_packets(guid, character, head, hair, skin, face_paint="none", model_customization="", is_member=True):
    """Sanctuary BaseZone.OnClientIsReady followed by WorldZone.OnClientIsReady, for a new character."""
    stats = b"".join(_stat(i, v) for i, v in STATS)
    return [
        # BaseZone
        ("QuickChatSendData", struct.pack("<HHi", 67, 1, 0)),
        ("UpdateStat", struct.pack("<HHQi", 38, 7, guid, len(STATS)) + stats),
        ("Hitpoints", struct.pack("<HHii", 38, 1, 2500, 2500)),
        ("Mana", struct.pack("<HHiiB", 38, 13, 100, 100, 0)),
        ("UpdateActiveWieldType", struct.pack("<HHQi", 35, 61, guid, 0)),
        ("GuildCanCreateGuild", struct.pack("<HHB", 129, 32, 0)),
        ("ItemClassDefinitions", reference_data.item_class_definitions()),
        ("ItemCategoryDefinitions", reference_data.item_category_definitions()),
        ("ClientProfileData", struct.pack("<HHi", 44, 3, 0)),
        ("CustomizationData", customizations(character, head, hair, skin, face_paint, model_customization)),
        ("MembershipSubscriptionInfo", struct.pack("<HBB", 189, 1 if is_member else 0, 0)),
        ("EffectTagCompositeEffectsEnable", struct.pack("<HHB", 35, 43, 1)),
        ("ZoneDoneSendingInitialData", struct.pack("<H", 14)),
        ("DoneSendingPreloadCharacters", struct.pack("<HHB", 38, 26, 1)),
        ("FriendList", struct.pack("<HBi", 74, 1, 0)),
        ("IgnoreList", struct.pack("<HBi", 112, 1, 0)),
        # WorldZone
        ("PointOfInterestDefinitionReply", struct.pack("<H", 57) + _payload(_b(False))),
        ("AdventurersJournalInfo", adventurers_journal()),
        ("CoinStoreItemList", struct.pack("<HHii", 165, 1, 0, 0)),
        ("InGamePurchaseEnableMarketplace", struct.pack("<HHB", 66, 24, 1)),
        ("InGamePurchaseEnablePaymentSources", struct.pack("<HHBB", 66, 33, 1, 1)),
        ("InGamePurchaseBundleCategoryGroups", struct.pack("<HHi", 66, 6, 0)),
        ("InGamePurchaseBundleCategories", struct.pack("<HHiB", 66, 7, 0, 0)),
        ("InGamePurchaseBundleGroups", struct.pack("<HHi", 66, 9, 0)),
        ("AnnouncementDataSend", announcements()),
        ("LoadWelcomeScreen", welcome_screen()),
        ("ObjectiveTargetUpdate", struct.pack("<HBB", 47, 14, 0)),
    ]


def _season_active(start, end, today):
    day = (today.month, today.day)
    if start <= end:
        return start <= day <= end
    return day >= start or day <= end


def client_areas(today=None):
    """WorldZone.SendEnabledClientAreas: the zone's areas, then the seasonal ones that are active today."""
    today = today or datetime.date.today()
    ids = list(ENABLED_CLIENT_AREAS)
    for _name, start, end, area_ids in SEASONAL_CLIENT_AREAS:
        if _season_active(start, end, today):
            ids += area_ids
    return ids


def nearest_area(x, z):
    """WorldZone.GetNearestZoneArea, without the terrain-environment lookup (see ZONE_AREAS)."""
    tile = (int(z // ZONE_TILE_SIZE), int(x // ZONE_TILE_SIZE))
    if tile in SANCTUARY_TILES:
        return next(a for a in ZONE_AREAS if a[0] == SANCTUARY_AREA_ID)
    return min(ZONE_AREAS, key=lambda a: (a[4][0] - x) ** 2 + (a[4][1] - z) ** 2)


def finished_loading_packets(guid, x, z, profile_id, today=None):
    """Sanctuary PacketClientFinishedLoadingHandler for a lone player with no mount, pet or weapon: client areas
    and point of interest (WorldZone.OnClientFinishedLoading), weapon flair override, toolbar. No AddPc: Sanctuary
    only sends AddPc for other players."""
    packets = [("SetClientArea %d" % area_id, struct.pack("<HiBi", 89, area_id, 1, 0))
               for area_id in client_areas(today)]
    area_id, name_id, map_area_id, chat_name_id, _pos = nearest_area(x, z)
    packets.append(("POIChangeMessage", struct.pack("<Hiii", 104, name_id, area_id, map_area_id)))
    packets.append(("ChatEnterArea", struct.pack("<HHi", 15, 2, chat_name_id)))
    packets.append(("SlotCompositeEffectOverride", struct.pack("<HHQii", 35, 31, guid, WEAPON_SLOT, 0)))
    # Profile 1 has no combat job in Sanctuary's CombatJobs.json, so SendToolbar sends the empty set.
    packets.append(("AbilitySetDefinition", struct.pack("<HHii", 36, 5, profile_id, TOOLBAR_SLOTS)
                    + struct.pack("<%di" % TOOLBAR_SLOTS, *([0] * TOOLBAR_SLOTS))))
    return packets


def game_time_sync(now=None):
    """Sanctuary PacketGameTimeSyncHandler: the game clock runs 8x from its epoch; use client time false."""
    now = int(now if now is not None else datetime.datetime.now(datetime.timezone.utc).timestamp())
    return struct.pack("<HqiB", 52, GAME_TIME_RATE * (now - GAME_TIME_EPOCH), GAME_TIME_RATE, 0)


def lobby_definitions_reply():
    """Sanctuary LobbyGameDefinitionPacketDefinitionsRequestHandler: one test lobby game, four empty lists."""
    entry_id = LOBBY_ENTRY[0]
    body = struct.pack("<i", 1) + struct.pack("<i", entry_id) + struct.pack("<4i", *LOBBY_ENTRY) + struct.pack("<4i", 0, 0, 0, 0)
    return struct.pack("<HH", 102, 2) + _payload(body)


# --- selection -----------------------------------------------------------------------------------------------

def enabled():
    return os.environ.get("FR_SANCTUARY", "").strip() == "1"


def skipped():
    return {name.strip().lower() for name in os.environ.get("FR_SKIP", "").split(",") if name.strip()}


def select(packets):
    """Drop the packets named in FR_SKIP (case-insensitive; 'SetClientArea' drops every area packet)."""
    skip = skipped()
    return [(name, inner) for name, inner in packets
            if name.lower() not in skip and name.split(" ")[0].lower() not in skip]


def opcode_note(inner):
    opcode = struct.unpack_from("<H", inner)[0]
    return "" if opcode in PS3_DISPATCH_RANGE else " (opcode %d is outside the 1.77 dispatcher's 12-180)" % opcode
