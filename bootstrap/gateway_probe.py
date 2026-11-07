"""Minimal SOE UdpLibrary listener for the PS3 client's game-server (gateway) connection.

After character login the client opens a UDP socket (thread "UdpLibrary") and connects to the address
in CharacterLoginReply/GatewayAddress. This listener completes the UdpLibrary handshake with no CRC and
no encryption, acknowledges reliable packets, and records what the client sends, so the application
protocol on top can be worked out. It is a probe, not a game server: it never sends game data.

Application layer (from the first packets the PS3 client sent, matching Clover's ClientGateway packets):
  Login (client -> server): u16 LE opcode 1 | string ticket | u64 guid | string version | string unknown
  LoginReply (server -> client): u16 LE opcode 2 | u8 success         (strings are u32 LE length + bytes)
The probe answers a Login with LoginReply(success) so the client moves on, then sends the first world packets
(see world_packets) and logs whatever the client sends next.
  Tunneled (either direction): u16 LE opcode 5 | u8 reliable | i32 LE size | inner packet (u16 LE opcode + fields)
  ZoneDetails (inner opcode 43, layout read from the PS3 client): string name | i32 type | u8 | u8 | string sky
                                                                    | u8 in-arena | i32 zone id
  SendSelfToClient (inner opcode 12): i32 length | player blob. The blob's field widths were found by running the
  client's own deserializer (tools/ppc_trace.py); with every list empty it is 273 bytes.

Packet layouts follow the UdpLibrary port in the Clover server (Sanctuary.UdpLibrary, AGPL-3.0):
  Connect : 00 01 | protocol version i32 | connect code i32 | max raw packet size i32 | name\\0
  Confirm : 00 02 | connect code i32 | encrypt code i32 | crc bytes u8 | encrypt methods u8 x2
            | max raw packet size i32 | protocol version i32            (all big endian)
  Reliable: 00 09+ch | sequence u16 | payload       Ack: 00 11+ch | sequence u16
"""

import json
import logging
import os
import socket
import struct
import threading
import time
from datetime import datetime, timezone

import character_mappings
import clothing
import inventory
import items
import jobs
import sanctuary_flow
import tracing

LOG = logging.getLogger("gateway")
TRACE_FILE = tracing.trace_path("ps3_gateway_trace.jsonl")
PACKET_NAMES = {
    0: "ZeroEscape", 1: "Connect", 2: "Confirm", 3: "Multi", 4: "Big", 5: "Terminate", 6: "KeepAlive",
    7: "ClockSync", 8: "ClockReflect", 9: "Reliable1", 10: "Reliable2", 11: "Reliable3", 12: "Reliable4",
    13: "Fragment1", 14: "Fragment2", 15: "Fragment3", 16: "Fragment4", 17: "Ack1", 18: "Ack2",
    19: "Ack3", 20: "Ack4", 21: "AckAll1", 22: "AckAll2", 23: "AckAll3", 24: "AckAll4", 25: "Group",
    26: "Ordered", 27: "Ordered2", 28: "PortAlive", 29: "UnreachableConnection", 30: "RequestRemap",
}
MAX_LOGGED_BYTES = 160
READY_FALLBACK_SECONDS = 20
KEEPALIVE_SECONDS = 5


def _trace(event, **fields):
    record = {"time_utc": datetime.now(timezone.utc).isoformat(), "event": event, **fields}
    tracing.append_line(TRACE_FILE, json.dumps(record))


def _read_string(buf, pos):
    if pos + 4 > len(buf):
        raise ValueError("truncated string length")
    length = struct.unpack_from("<I", buf, pos)[0]
    pos += 4
    if length > 4096 or pos + length > len(buf):
        raise ValueError("bad string length")
    return buf[pos:pos + length].decode("latin-1"), pos + length


def parse_login(payload):
    """Decode a gateway Login packet; returns (ticket, guid, version, unknown) or None."""
    try:
        ticket, pos = _read_string(payload, 2)
        if pos + 8 > len(payload):
            return None
        guid = struct.unpack_from("<Q", payload, pos)[0]
        version, pos = _read_string(payload, pos + 8)
        unknown, pos = _read_string(payload, pos)
        return ticket, guid, version, unknown
    except ValueError:
        return None


def _string(value):
    raw = value.encode("utf-8")
    return struct.pack("<I", len(raw)) + raw


def tunneled(inner, reliable=True):
    return struct.pack("<HBI", 5, 1 if reliable else 0, len(inner)) + inner


# The client turns this name into the asset requests <name>.gzne, <name>.cob and <name>.odd, which must exist in
# assets_ps3_manifest.bin and the packs, or the loading screen never goes away (docs/re/zone-loading.md).
# Order of choice: FR_ZONE (PowerShell: $env:FR_ZONE="Name"), then the zone picked from the install's own packs by
# choose_zone_from_install(), then DEFAULT_ZONE (the PC game's name, unverified for the PS3 packs).
# FR_ZONE_TYPE picks the terrain implementation (0 TileStatic, 1 TileSeamless, 2 RuntimeSeamless, 3 Mesh; only
# 2 is set up at startup).
DEFAULT_ZONE = "FabledRealms"
_install_zone = None
_install_spawn = None   # (x, z, tile origin, note) from assetlib.pick_spawn()


def choose_zone_from_install(usrdir):
    """Pick the ZoneDetails zone from the PS3 install's packs (bootstrap calls this once at startup)."""
    global _install_zone
    try:
        import assetlib
        zone, complete, note = assetlib.pick_zone(str(usrdir), DEFAULT_ZONE)
    except Exception as err:   # never stop the server over this; the default stays
        LOG.warning("could not read the packs in %s to pick a zone: %s", usrdir, err)
        return None
    if zone is None:
        LOG.warning("zone: %s; sending %r", note, DEFAULT_ZONE)
        return None
    _install_zone = zone
    LOG.info("zone: %s; %d complete zones in the packs: %s", note, len(complete), ", ".join(complete[:12]))
    global _install_spawn
    try:
        picked = assetlib.pick_spawn(str(usrdir), zone)
    except Exception as err:
        LOG.warning("could not pick a spawn position from the tiles: %s", err)
        picked = None
    if picked:
        _install_spawn = picked
        LOG.info("spawn: x=%.1f z=%.1f in tile %s: %s", picked[0], picked[1], picked[2], picked[3])
    else:
        LOG.warning("spawn: no local terrain tile found for %r; using the PC game's position, whose tile may have "
                    "to be downloaded", zone)
    if zone.lower() != DEFAULT_ZONE.lower():
        LOG.warning("zone: %r is not in the PS3 packs, using %r instead (set FR_ZONE to choose another)",
                    DEFAULT_ZONE, zone)
    return zone


def configured_zone():
    return os.environ.get("FR_ZONE", "").strip() or _install_zone or DEFAULT_ZONE


def configured_zone_type():
    try:
        return int(os.environ.get("FR_ZONE_TYPE", "2").strip())
    except ValueError:
        return 2


DEFAULT_SKY = "sky_wilds24.xml"   # the FabledRealms sky the client opens; naming it in ZoneDetails stops the sky render crash


def zone_details(name=None, zone_type=None, tutorial=0, unknown=0, sky=None, arena=0, zone_id=1):
    """Inner packet 43 as the PS3 client (build 1.77) reads it; it must consume every byte.

    The sky is DEFAULT_SKY unless FR_SKY names another (FR_SKY= with nothing sends none). Without a sky the client's renderer
    crashes as soon as the camera looks up (docs/re/zone-loading.md, After the loading screen)."""
    sky = os.environ.get("FR_SKY", DEFAULT_SKY).strip() if sky is None else sky
    name = configured_zone() if name is None else name
    zone_type = configured_zone_type() if zone_type is None else zone_type
    return (struct.pack("<H", 43) + _string(name) + struct.pack("<iBB", zone_type, tutorial, unknown)
            + _string(sky) + struct.pack("<Bi", arena, zone_id))


CHARACTER = {}   # values from the last character/create request, shared with the HTTP side

HEAD_NAMES = {1: "caucasian", 2: "african", 3: "asian"}


def _int(value, default=0):
    try:
        return int(str(value).strip() or default)
    except ValueError:
        return default


TERMINATE_REASONS = {0: "none", 1: "ICMP error", 2: "timeout (heard nothing)", 3: "other side terminated",
                     4: "manager deleted", 5: "connect failed", 6: "application closed it",
                     8: "unacknowledged timeout", 15: "corrupt packet", 16: "protocol mismatch"}

INNER_NAMES = {10: "FinishedLoading", 13: "ClientIsReady", 88: "SetLocale", 109: "ClientLog",
               169: "ClientInitializationDetails"}


def describe_inner(opcode, inner):
    """A readable note for the inner client packets we know, so the log shows what the client said."""
    name = INNER_NAMES.get(opcode)
    if name is None:
        return ""
    detail = ""
    try:
        if opcode == 88 and len(inner) >= 6:                     # SetLocale: string
            n = struct.unpack_from("<I", inner, 2)[0]
            detail = " " + repr(inner[6:6 + n].decode("utf-8", "replace"))
        elif opcode == 169 and len(inner) >= 6:                  # i32, a timezone offset in seconds
            detail = " %d" % struct.unpack_from("<i", inner, 2)[0]
        elif opcode == 109 and len(inner) >= 6:                  # ClientLog: file name, text
            n = struct.unpack_from("<I", inner, 2)[0]
            name_end = 6 + n
            m = struct.unpack_from("<I", inner, name_end)[0]
            detail = " %r: %r" % (inner[6:name_end].decode("utf-8", "replace"),
                                  inner[name_end + 4:name_end + 4 + m].decode("utf-8", "replace"))
    except (struct.error, IndexError):
        pass
    return " (%s%s)" % (name, detail)


PROFILE_ID = 1          # Clover's DefaultProfileId (the first job a new character has)
PROFILE_SIZE = 179      # bytes the client reads per profile entry with an empty item class list
PROFILE_RANK_OFFSET = 0x33   # the job's rank inside an entry: the reader stores it at profile+20, which the job list's Rank column returns
PROFILE_RANK = jobs.JOB_RANK   # Sanctuary's level 20; rank 0 makes the HUD's GetNextJob loop forever


def profile(profile_id, equipment=None):
    """One entry of the profile list (a job).

    The client reads 179 bytes per entry: id, name id, description id, type, icon, three image sets, a members-only
    byte, is-combat, an item-class list, a few flags and integers (rank at 0x33, rank percent, stars), 17 equipment
    words, 8 ability lists (each a count; zero here) and an ability-experience list. The fields Sanctuary fills from
    Resources/Profiles.json come from bootstrap/jobs.py (an id it does not have gets only the id); the rank is
    Sanctuary's level 20; the lists are empty. `equipment` is {slot: item id} (bootstrap/items.py): slot n's item id
    goes at 0x43 + 4 * n: the client indexes the 17 words by slot number (the equipment screen's 0x46e04 passes the
    definition's slot, 1 to 16, to 0x5911c, and RequestEquippedAttachments at 0xeba4c walks 0 to 16), so word 0 is
    never used. The job's item classes
    (what it can equip, bootstrap/jobs.py) are the list at 0x25, so with them the entry is longer than 179 bytes and
    every later offset moves by its size. FR_ITEMS=0 leaves the list empty.

    The rank must be above 0: after the zone loads, the HUD script GroupHandler:GetNextJob (called from HUD:showMain
    through SetJobSwitchOnOrOff) loops until it finds a job with a rank, so with rank 0 the main thread never leaves
    WaitForWorldReady and the loading screen stays up (docs/re/zone-loading.md).
    """
    entry = bytearray(PROFILE_SIZE)
    job = jobs.BY_ID.get(profile_id)
    if job:
        struct.pack_into("<8iB", entry, 0, *job[:8], 1 if job[8] else 0)
    else:
        struct.pack_into("<i", entry, 0, profile_id)
    struct.pack_into("<i", entry, PROFILE_RANK_OFFSET, PROFILE_RANK)
    for slot, item_id in (equipment or {}).items():
        struct.pack_into("<i", entry, PROFILE_EQUIPMENT_OFFSET + 4 * slot, item_id)
    classes = jobs.JOB_ITEM_CLASSES.get(profile_id, ()) if items.enabled() else ()
    item_classes = struct.pack("<i", len(classes)) + b"".join(struct.pack("<iii", c, c, u) for c, u in classes)
    return bytes(entry[:PROFILE_ITEM_CLASSES_OFFSET]) + item_classes + bytes(entry[PROFILE_ITEM_CLASSES_OFFSET + 4:])


PROFILE_EQUIPMENT_OFFSET = 0x43   # 17 item ids indexed by slot, so word 0 is unused and the head (slot 1) is the second
                                  # (offsets as if the item class list were empty)
PROFILE_ITEM_CLASSES_OFFSET = 0x25   # the job's item class list (count, then 12 bytes each); everything after moves


def character_items(character=None):
    """(items, equipment) the character owns and wears (bootstrap/items.py), or nothing with FR_ITEMS=0."""
    if not items.enabled():
        return [], {}
    return items.inventory(character or {}, jobs.selected())


def profile_list(character=None):
    """The profile list of the player data: a count, then every job Sanctuary gives a new character (FR_JOBS), each
    wearing its starter outfit."""
    chosen = jobs.selected()
    _owned, equipment = character_items(character)
    return struct.pack("<i", len(chosen)) + b"".join(profile(job[0], equipment.get(job[0])) for job in chosen)


ITEM_LIST_OFFSET = 0x31   # the item list's count, counted from the end of the active profile id


def item_definition_packets(character=None):
    """One ClientUpdate ItemAdd (38/2) per item the character owns, each with its definition (bootstrap/items.py).
    Sent right after the player data: the client only takes them once the player exists. These, not the player
    data's item list, are what put the items in the inventory and equipment screens."""
    owned, _equipment = character_items(character)
    return items.item_add_packets(owned)


def appearance(character):
    """Head, hair, skin tone, face paint and body option (facial hair) the character creator chose, as the asset names
    the client draws (Sanctuary's mapping tables, bootstrap/character_mappings.py)."""
    c = character or {}
    return (character_mappings.head(c), character_mappings.hair(c), character_mappings.skin_tone(c),
            character_mappings.face_paint(c), character_mappings.model_customization(c))


def player_blob(character=None, guid=1):
    """The player data for SendSelfToClient, laid out like the PS3 client reads it (build 1.77).

    Names follow Clover's ClientPcData where the order matches. The profile list holds the jobs Sanctuary gives a new
    character (all of them; bootstrap/jobs.py, FR_JOBS). It must not be empty: the client looks the active profile up
    right after loading the player and dereferences null when the list is empty (found with tools/ppc_trace.py: the
    lookup at 0xeba4c reads the node it did not find). The item list holds the character's starter items
    (bootstrap/items.py, FR_ITEMS); every other list is sent empty, so there are no pets, quests or stats yet.
    """
    c = character or {}
    model = _int(c.get("Model"), 1)
    head, hair, skin, face_paint, model_customization = appearance(c)
    out = bytearray()
    out += struct.pack("<QQ", 0, guid)                       # launch ticket, guid
    out += struct.pack("<i", model)
    out += _string(head) + _string(hair)
    out += struct.pack("<ii", _int(c.get("HairColor")), _int(c.get("EyeColor")))
    out += _string(skin) + _string(face_paint) + _string(model_customization)
    out += struct.pack("<4f", *configured_spawn())          # position (the client's camera follows this player)
    out += struct.pack("<4f", *SPAWN_ROTATION)               # rotation
    out += struct.pack("<iii", 0, 0, 0)                      # name ids
    out += _string(c.get("FirstName") or "Test") + _string(c.get("LastName") or "")
    out += struct.pack("<iQii", 0, int(time.time()), 0, 0)   # coins, birthday, age, play time
    out += struct.pack("<BiB", 0, 2, 0)                      # underage, membership status, member nag screen
    out += struct.pack("<iiiii", 0, 0, 0, 0, 1)              # chat country, chat language, preferred language, chat language, login count
    out += struct.pack("<B", 0)                              # grandfathered
    out += struct.pack("<ii", 0, 0)                          # two vehicle loadouts (kart race, demo derby)
    out += profile_list(c)                                   # profile list: the jobs (bootstrap/jobs.py)
    out += struct.pack("<i", PROFILE_ID)                     # active profile id
    tail = bytes(0x111 - 0xa3)                               # remaining fields: empty lists and zeros...
    owned, _equipment = character_items(c)                   # ...except the item list
    out += tail[:ITEM_LIST_OFFSET] + items.item_list(owned) + tail[ITEM_LIST_OFFSET + 4:]
    return bytes(out)


def equipment_packets(character=None, guid=1):
    """The packets that dress the character in the clothes the character creator picked (bootstrap/clothing.py). Default
    (or FR_EQUIP=7): one EquippedItemsChange (PlayerUpdate sub-opcode 7), the only one seen to work in RPCS3; the client
    plays the job change effect with it. FR_EQUIP=5: one EquipItem (ClientUpdate sub-opcode 5) per piece, item ids 1, 2,
    3... as Sanctuary numbers a new character's items (sent in RPCS3, nothing went on). FR_EQUIP=0 sends nothing. The player data carries no items yet, so this is what dresses the character; it is sent
    after ClientFinishedLoading, once the character exists."""
    mode = os.environ.get("FR_EQUIP", "1").strip()
    pieces = clothing.attachments(character or {})
    if mode == "0" or not pieces:
        return []
    if mode == "5":
        return [clothing.equip_item(n + 1, piece, PROFILE_ID) for n, piece in enumerate(pieces)]
    return [clothing.equipped_items_change(guid, pieces)]


def send_self_to_client(character=None):
    blob = player_blob(character)
    return struct.pack("<Hi", 12, len(blob)) + blob


SPAWN = (-1904.883, -39.7098, 412.6024, 1.0)   # Sanctuary's FabledRealms.json SpawnPosition (the PC game's)
DEFAULT_SPAWN_Y = 400.0   # height used with a position picked from the packs; the terrain height there is unknown


def configured_spawn():
    """Where the player data and AddPc put the player. Default: Sanctuary's spawn (position, height and, with
    SPAWN_ROTATION, rotation). Its terrain tile, FabledRealms_-32_4.gcnk, is only in the manifest of a typical PS3
    install, so the ground there may be missing. FR_SPAWN=local uses the centre of a tile whose neighbourhood is in the
    packs instead (height FR_SPAWN_Y, default 400; the ground on tile (-4, -4) is at about 36). FR_SPAWN="x,y,z" sets
    it outright."""
    raw = os.environ.get("FR_SPAWN", "").strip()
    if raw.lower() == "local":
        if _install_spawn:
            try:
                y = float(os.environ.get("FR_SPAWN_Y", DEFAULT_SPAWN_Y))
            except ValueError:
                y = DEFAULT_SPAWN_Y
            return (_install_spawn[0], y, _install_spawn[1], 1.0)
        return SPAWN
    if raw:
        try:
            x, y, z = (float(v) for v in raw.replace(";", ",").split(","))
            return (x, y, z, 1.0)
        except ValueError:
            LOG.warning("FR_SPAWN=%r is not 'x,y,z'; ignored", raw)
    return SPAWN


SPAWN_ROTATION = (0.5531712, 0.0, 0.8330676, 0.0)


def add_pc(character=None, guid=1):
    """PlayerUpdate (opcode 35) sub-opcode 1, AddPc: put your own character (guid 1, the creator's name, at the spawn) into
    the world. The default flow needs it: without an AddPc the client never leaves the loading screen (its WaitForWorldReady
    waits for the characters it needs), and the second test character that used to stand next to the player is gone.

    The field order was read from the PS3 client's own parser with tools/ppc_trace.py. It follows Clover's
    PlayerUpdatePacketAddPc except that this build has no IsReferee flag, a three field title (no icon id) and
    nothing after the name offset (no wield type or VIP fields). The client consumes exactly these bytes.
    Equipment (the attachment list) is empty for now, so the character is drawn without clothes.
    """
    c = character or {}
    first_name = c.get("FirstName") or "Test"
    last_name = c.get("LastName") or ""
    head = f"<race>_<gender>_head_{HEAD_NAMES.get(_int(c.get('Head'), 1), 'caucasian')}.adr"
    hair = "<race>_<gender>_hair_eyebrows_bald.adr"
    skin = f"skintone{_int(c.get('SkinTone'), 1) or 1}"
    x, y, z, w = configured_spawn()
    out = bytearray()
    out += struct.pack("<HHQ", 35, 1, guid)
    out += struct.pack("<iii", 0, 0, 0) + _string(first_name) + _string(last_name)   # name ids, names
    out += struct.pack("<i", _int(c.get("Model"), 1))
    out += struct.pack("<iii", 0, 0, 0)                      # chat bubble: foreground, background, size
    out += struct.pack("<4f", x, y, z, w)
    out += struct.pack("<4f", *SPAWN_ROTATION)
    out += struct.pack("<i", 0)                              # attachments (equipment): none yet
    out += _string(head) + _string(hair)
    out += struct.pack("<iii", _int(c.get("HairColor")), _int(c.get("EyeColor")), 0)   # hair colour, eye colour, unused
    out += _string(skin) + _string("none") + _string("")     # skin tone, face paint, model customization
    out += struct.pack("<f", 8.0)                            # max movement speed
    out += struct.pack("<BBi", 0, 0, 0)                      # underage, member, temporary appearance
    out += struct.pack("<i", 0)                              # guilds: none
    out += struct.pack("<i", PROFILE_ID)                     # active profile id
    out += struct.pack("<iii", 0, 0, 0)                      # title
    out += struct.pack("<i", 0)                              # effect tags: none
    out += struct.pack("<Qiif", 0, -1, -1, 0.0)              # mount guid, seat, queue position, name offset
    return bytes(out)


def lobby_definitions_reply():
    """LobbyGameDefinition (opcode 102) sub-opcode 2: the mini-game list, empty. i32 payload length, then five
    counts (game entries and four unknown lists), all zero; the client reads exactly this."""
    payload = struct.pack("<5i", 0, 0, 0, 0, 0)
    return struct.pack("<HHi", 102, 2, len(payload)) + payload


def list_queues_reply():
    """Matchmaking (opcode 141) sub-opcode 2, ListQueuesResponse: an unknown u64 and a count of queues (zero)."""
    return struct.pack("<HHQi", 141, 2, 0, 0)


def game_time_sync():
    """GameTimeSync (52), layout and values from Sanctuary: i64 time, i32 server rate (8), u8 use client time (0).
    The zone's sky is a 24 hour sky, so the client probably needs the game clock to finish setting it.
    FR_TIME=0 turns it off."""
    return tunneled(struct.pack("<HqiB", 52, int(time.time()), 8, 0))


def zone_done_packets():
    """What the client's WaitForWorldReady step waits for besides loading: ZoneDoneSendingInitialData (opcode 14)
    and the "done sending preload characters" ClientUpdate (opcode 38, sub-opcode 26, bool true; the client only
    counts it as received when the bool is set)."""
    # LoadWelcomeScreen (93): Clover notes the client will not render the rest of the UI unless this is sent with
    # ShowWelcomeScreen true. Layout from Clover, untested on the PS3 client. FR_WELCOME=0 turns it off.
    welcome = []
    if os.environ.get("FR_WELCOME", "1").strip() != "0":
        welcome = [tunneled(struct.pack("<HBiiii", 93, 1, 0, 0, 0, 0))]
    if os.environ.get("FR_EXTRA", "").strip() == "1":
        # ClientUpdate (38) sub 1 Hitpoints: i32 current, i32 max (Clover layout, untested on the PS3 client).
        welcome.append(tunneled(struct.pack("<HHii", 38, 1, 2500, 2500)))
        # ClientUpdate (38) sub 13 Mana: i32 current, i32 max, u8 show-over-head (Clover layout, untested).
        welcome.append(tunneled(struct.pack("<HHiiB", 38, 13, 100, 100, 0)))
    if os.environ.get("FR_TIME", "1").strip() != "0":
        welcome.insert(0, game_time_sync())
    return welcome + [tunneled(struct.pack("<H", 14)), tunneled(struct.pack("<HHB", 38, 26, 1))]


def world_packets():
    """Packets sent right after the login reply, in order (each already tunneled)."""
    extra = []
    if os.environ.get("FR_EXTRA", "").strip() == "1":
        # Untested: Clover's ClientGameSettings (144) layout, sent before ZoneDetails as Clover does.
        # InitializationParameters (166): u32 length + environment name; the client's handler consumes it exactly.
        extra.append(tunneled(struct.pack("<Hi", 166, 4) + b"Live"))
        extra.append(tunneled(struct.pack("<HiiiBf", 144, 4, 7, 268, 1, 1.0)))
    if os.environ.get("FR_EXTRA", "").strip() == "1":
        # Untested: PlayerUpdate sub 37 ItemDefinitions, empty list (u32 payload length 4, u32 count 0).
        item_defs = tunneled(struct.pack("<HHii", 35, 37, 4, 0))
    else:
        item_defs = None
    return extra + [tunneled(zone_details())] + ([item_defs] if item_defs else [])\
        + [tunneled(send_self_to_client(CHARACTER))] + [tunneled(p) for p in item_definition_packets(CHARACTER)]


class GatewayProbe:
    def __init__(self, host, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((host, port))
        self.host, self.port = host, port
        self.sessions = {}   # addr -> connect code
        self.max_raw = {}    # addr -> largest datagram the client accepts (512 for the PS3 client)
        self.client_ready = set()   # peers that sent ClientIsReady
        self.inventories = {}       # peer -> inventory.Inventory (FR_SANCTUARY=1)
        self.count = 0
        self.send_seq = {}   # (addr, channel) -> next reliable sequence to send
        self.pending = {}    # (addr, channel, seq) -> [packet, last_send_time, tries]
        self.login_reply = True

    def start(self):
        threading.Thread(target=self.serve, name="gateway-probe", daemon=True).start()
        threading.Thread(target=self.resend_loop, name="gateway-resend", daemon=True).start()
        LOG.info("UDP gateway probe listening on %s:%d (handshake + reliable acks, no game data)",
                 self.host, self.port)

    def serve(self):
        while True:
            try:
                data, addr = self.sock.recvfrom(65535)
            except ConnectionResetError:
                continue      # Windows reports an ICMP "port unreachable" for an earlier send as an error here
            except OSError:
                return
            try:
                self.handle(data, addr)
            except Exception as exc:  # a probe must never die on a odd packet
                LOG.warning("gateway packet from %s failed: %s", addr, exc)

    def handle(self, data, addr, depth=0):
        self.count += 1
        if len(data) < 2 or data[0] != 0:
            self.record(addr, "raw", data)
            return
        kind = data[1]
        name = PACKET_NAMES.get(kind, f"type{kind}")
        if kind == 1 and len(data) >= 14:
            version, code, max_raw = struct.unpack(">iii", data[2:14])
            proto = data[14:].split(b"\0", 1)[0].decode("latin-1") if version > 2 else ""
            self.sessions[addr] = code
            self.max_raw[addr] = max_raw
            LOG.info("gateway Connect from %s: protocol v%d name=%r code=0x%08x max_raw=%d",
                     addr, version, proto, code & 0xffffffff, max_raw)
            _trace("connect", peer=addr[0], version=version, name=proto, code=code, max_raw=max_raw,
                   hex=data[:MAX_LOGGED_BYTES].hex())
            encrypt_code = int.from_bytes(os.urandom(4), "big") & 0x7fffffff
            confirm = struct.pack(">BBiiBBBii", 0, 2, code, encrypt_code, 0, 0, 0, max_raw, 3)
            self.sock.sendto(confirm, addr)
        elif 9 <= kind <= 12:                            # Reliable: acknowledge, record, interpret
            channel = kind - 9
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                self.sock.sendto(struct.pack(">BBH", 0, 17 + channel, seq), addr)
                self.record(addr, f"{name} seq={seq}", data[4:])
                self.on_application_packet(addr, channel, data[4:])
            else:
                self.record(addr, name, data)
        elif 13 <= kind <= 16:                           # Fragment: acknowledge and record
            channel = kind - 13
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                self.sock.sendto(struct.pack(">BBH", 0, 17 + channel, seq), addr)
            self.record(addr, name, data[4:])
        elif 17 <= kind <= 24:                           # Ack / AckAll for what we sent
            channel = (kind - 17) % 4
            if len(data) >= 4:
                seq = struct.unpack(">H", data[2:4])[0]
                for key in [k for k in self.pending if k[0] == addr and k[1] == channel
                            and (k[2] == seq or (kind >= 21 and k[2] <= seq))]:
                    self.pending.pop(key, None)
            LOG.info("gateway %s from %s: %s", name, addr, data[2:].hex())
            _trace("ack", peer=addr[0], label=name, hex=data[2:].hex())
        elif kind == 3 and depth < 3:                    # Multi: [len][packet]...
            pos = 2
            while pos < len(data):
                length = data[pos]
                pos += 1
                if length == 0xff and pos + 2 <= len(data):
                    length = struct.unpack(">H", data[pos:pos + 2])[0]
                    pos += 2
                self.handle(data[pos:pos + length], addr, depth + 1)
                pos += length
        elif kind == 5:
            self.sessions.pop(addr, None)                # stop keeping this peer alive
            reason = struct.unpack(">H", data[6:8])[0] if len(data) >= 8 else -1
            LOG.info("gateway Terminate from %s: %s (reason %d: %s)", addr, data[2:].hex(), reason,
                     TERMINATE_REASONS.get(reason, "?"))
            _trace("terminate", peer=addr[0], hex=data.hex())
        elif kind in (6, 28):
            pass                                          # KeepAlive / PortAlive
        else:
            self.record(addr, name, data)

    def send_reliable(self, addr, payload, channel=0):
        """Send one application payload reliably, splitting it into fragments if it does not fit one datagram.

        The PS3 client reads datagrams into a buffer of exactly the negotiated max raw size (512). A bigger one
        is rejected by the console's network layer (RPCS3 logs "Socket error EMSGSIZE") and never arrives, so
        the 560 byte player data packet was lost and the game gave up after two minutes. UdpLibrary's answer is
        a fragmented reliable packet: type 13 + channel, then the sequence number, with a 4 byte big endian total
        length in front of the data of the first fragment.
        """
        room = self.max_raw.get(addr, 512) - 4 - 4          # header (4) and a little slack
        if len(payload) <= room:
            self._send_one(addr, channel, 9 + channel, payload, "")
            return
        data = struct.pack(">I", len(payload)) + payload
        pieces = [data[i:i + room] for i in range(0, len(data), room)]
        for number, piece in enumerate(pieces, 1):
            self._send_one(addr, channel, 13 + channel, piece, " fragment %d/%d of %d bytes" % (number, len(pieces), len(payload)))

    def _send_one(self, addr, channel, kind, body, note):
        seq = self.send_seq.get((addr, channel), 0)
        self.send_seq[(addr, channel)] = (seq + 1) & 0xffff
        packet = struct.pack(">BBH", 0, kind, seq) + body
        self.pending[(addr, channel, seq)] = [packet, time.monotonic(), 1]
        self.sock.sendto(packet, addr)
        LOG.info("gateway -> %s Reliable%d seq=%d (%d bytes)%s: %s", addr, channel + 1, seq, len(body), note,
                 body[:MAX_LOGGED_BYTES].hex())
        _trace("send", peer=addr[0], channel=channel, seq=seq, kind=kind, size=len(body), hex=body[:512].hex())

    def send_named(self, addr, channel, stage, packets):
        """Send (name, inner) packets tunneled, logging each by name (FR_SANCTUARY=1, bootstrap/sanctuary_flow.py)."""
        packets = sanctuary_flow.select(packets)
        LOG.info("sanctuary: %s -> %d packets: %s", stage, len(packets), ", ".join(name for name, _ in packets))
        for name, inner in packets:
            note = sanctuary_flow.opcode_note(inner)
            if note:
                LOG.info("sanctuary: %s%s", name, note)
            _trace("sanctuary", peer=addr[0], stage=stage, name=name, size=len(inner))
            self.send_reliable(addr, tunneled(inner), channel)

    def spawn_characters(self, addr, channel):
        """Send your character's AddPc after the client finished loading (the server marks the player visible then),
        then the creator's clothes."""
        if addr not in self.sessions:
            return
        packet = add_pc(CHARACTER)
        LOG.info("gateway: AddPc for your character (guid 1) (%d bytes)", len(packet))
        self.send_reliable(addr, tunneled(packet), channel)
        self.send_equipment(addr, channel)

    def send_equipment(self, addr, channel):
        """Dress your character (guid 1) in the clothes picked in the character creator (equipment_packets)."""
        if addr not in self.sessions:
            return
        packets = equipment_packets(CHARACTER)
        if not packets:
            return
        LOG.info("gateway: dressing your character (guid 1) with %d packet(s) of opcode %d/%d: %s", len(packets),
                 *struct.unpack_from("<HH", packets[0]), ", ".join(
                     "slot %d %s" % (slot, model) for model, _t, _a, _tint, _c, slot in clothing.attachments(CHARACTER)))
        for packet in packets:
            _trace("equipment", peer=addr[0], size=len(packet))
            self.send_reliable(addr, tunneled(packet), channel)

    def ready_fallback(self, addr, channel):
        """If the client never says ClientIsReady, send the world-ready packets anyway so the log shows what it
        does next (it may be waiting for them before it says anything)."""
        if addr in self.client_ready or sanctuary_flow.enabled():   # Sanctuary has no such fallback
            return
        LOG.info("gateway: no ClientIsReady from %s after %d s; sending ZoneDoneSendingInitialData and "
                 "DoneSendingPreloadCharacters anyway", addr, READY_FALLBACK_SECONDS)
        _trace("fallback", peer=addr[0])
        for packet in zone_done_packets():
            self.send_reliable(addr, packet, channel)

    def resend_loop(self):
        last_keepalive = time.monotonic()
        while True:
            time.sleep(0.5)
            now = time.monotonic()
            if now - last_keepalive >= KEEPALIVE_SECONDS:
                # The client's UdpLibrary closes the connection (Terminate, reason 2 = timeout) when it hears
                # nothing for three minutes; a real server keeps talking. A KeepAlive packet is just 00 06.
                last_keepalive = now
                for peer in list(self.sessions):
                    try:
                        self.sock.sendto(b"\x00\x06", peer)
                    except ConnectionResetError:
                        pass  # the peer's port is closed (Windows reports it on the next call)
                    except OSError:
                        return
            for key, item in list(self.pending.items()):
                if now - item[1] >= 1.0:
                    if item[2] >= 8:
                        self.pending.pop(key, None)
                        continue
                    item[1], item[2] = now, item[2] + 1
                    try:
                        self.sock.sendto(item[0], key[0])
                    except ConnectionResetError:
                        pass
                    except OSError:
                        return

    def on_application_packet(self, addr, channel, payload, depth=0):
        if len(payload) < 2:
            return
        if payload[0] == 0 and payload[1] in (3, 0x19) and depth < 3:
            # The client bundles small application packets into one reliable payload: a Multi (3) or Group (25)
            # packet, i.e. 00 19, then [length][packet] for each one (0xff starts a two byte big endian length).
            pos = 2
            while pos < len(payload):
                length = payload[pos]
                pos += 1
                if length == 0xff and pos + 2 <= len(payload):
                    length = struct.unpack(">H", payload[pos:pos + 2])[0]
                    pos += 2
                self.on_application_packet(addr, channel, payload[pos:pos + length], depth + 1)
                pos += length
            return
        opcode = struct.unpack("<H", payload[:2])[0]
        if opcode == 1:                                  # gateway Login
            fields = parse_login(payload)
            if fields is None:
                LOG.warning("gateway Login from %s did not parse as ticket/guid/version/unknown", addr)
                return
            LOG.info("gateway Login: ticket=%r guid=%d version=%r unknown=%r", *fields)
            _trace("login", ticket=fields[0], guid=fields[1], version=fields[2], unknown=fields[3])
            if self.login_reply:
                self.send_reliable(addr, struct.pack("<HB", 2, 1), channel)   # LoginReply(success)
                LOG.info("ZoneDetails: zone=%r type=%d (set FR_ZONE / FR_ZONE_TYPE to change)", configured_zone(), configured_zone_type())
                if sanctuary_flow.enabled():
                    self.send_named(addr, channel, "login", sanctuary_flow.login_packets(
                        zone_details(), send_self_to_client(CHARACTER), item_definition_packets(CHARACTER)))
                    if not hasattr(self, "inventories"):
                        self.inventories = {}
                    self.inventories[addr] = inventory.Inventory(CHARACTER, profile)
                else:
                    for packet in world_packets():
                        self.send_reliable(addr, packet, channel)
                self.client_ready.discard(addr)
                timer = threading.Timer(READY_FALLBACK_SECONDS, self.ready_fallback, (addr, channel))
                timer.daemon = True
                timer.start()
        elif opcode in (5, 6) and len(payload) >= 7:     # tunneled client packet
            size = struct.unpack_from("<I", payload, 3)[0]
            inner = payload[7:7 + size]
            inner_op = struct.unpack_from("<H", inner, 0)[0] if len(inner) >= 2 else -1
            LOG.info("gateway tunneled (%d) inner opcode %d%s from %s (%d bytes): %s", opcode, inner_op,
                     describe_inner(inner_op, inner), addr, len(inner), inner[:MAX_LOGGED_BYTES].hex())
            _trace("tunneled", peer=addr[0], outer=opcode, inner_opcode=inner_op, hex=inner[:512].hex())
            if sanctuary_flow.enabled():
                self.on_sanctuary_packet(addr, channel, opcode, inner_op, inner)
                return
            if opcode == 6 and inner_op == 102 and inner[2:4] == b"\x01\x00":   # mini-game list request
                self.send_reliable(addr, tunneled(lobby_definitions_reply(), reliable=True), channel)
            if opcode == 6 and inner_op == 141 and inner[2:4] == b"\x01\x00":   # matchmaking queue list request
                self.send_reliable(addr, tunneled(list_queues_reply(), reliable=True), channel)
            if inner_op == 52 and os.environ.get("FR_TIME", "1").strip() != "0":   # client asks for the game time
                self.send_reliable(addr, game_time_sync(), channel)
            if inner_op == 10:                           # ClientFinishedLoading: now put characters in the world
                threading.Timer(1.0, self.spawn_characters, (addr, channel)).start()
            if inner_op == 13:                           # ClientIsReady: the initial data is complete
                self.client_ready.add(addr)
                for packet in zone_done_packets():
                    self.send_reliable(addr, packet, channel)
        else:
            LOG.info("gateway application opcode %d from %s (%d bytes)", opcode, addr, len(payload))

    def on_sanctuary_packet(self, addr, channel, opcode, inner_op, inner):
        """FR_SANCTUARY=1: answer the client as Sanctuary does (bootstrap/sanctuary_flow.py). Sanctuary answers the
        mini-game list and the game time, never the matchmaking queue list (141/1), and sends no AddPc for a lone
        player. Inventory requests (42) and job switches (26/13)
        go to the session's bootstrap/inventory.py, as Sanctuary's handlers."""
        if inner_op in (42, 26):
            inv = getattr(self, "inventories", {}).get(addr)
            replies = inv.handle(inner) if inv else None
            if replies is not None:
                sub = struct.unpack_from("<H", inner, 2)[0] if len(inner) >= 4 else -1
                LOG.info("sanctuary: inventory request %d/%d %s", inner_op, sub, inner.hex())
                _trace("inventory", peer=addr[0], opcode=inner_op, sub=sub, hex=inner.hex())
                self.send_named(addr, channel, "inventory %d/%d" % (inner_op, sub), replies)
                return
        if opcode == 6 and inner_op == 102 and inner[2:4] == b"\x01\x00":
            self.send_named(addr, channel, "lobby request", [("LobbyGameDefinitions", sanctuary_flow.lobby_definitions_reply())])
        if inner_op == 141:
            LOG.info("sanctuary: no reply to opcode 141 (Sanctuary has no handler for it)")
        if inner_op == 52:
            self.send_named(addr, channel, "game time request", [("GameTimeSync", sanctuary_flow.game_time_sync(time.time()))])
        if inner_op == 13:
            self.client_ready.add(addr)
            head, hair, skin, face_paint, model_customization = appearance(CHARACTER)
            self.send_named(addr, channel, "ClientIsReady", sanctuary_flow.client_is_ready_packets(
                1, CHARACTER, head, hair, skin, face_paint, model_customization))
        if inner_op == 10:
            x, _y, z, _w = configured_spawn()
            self.send_named(addr, channel, "ClientFinishedLoading", sanctuary_flow.finished_loading_packets(
                1, x, z, PROFILE_ID))
            # The clothes are not Sanctuary's: its PC client reads them from the items in the player data.
            threading.Timer(1.0, self.send_equipment, (addr, channel)).start()

    def record(self, addr, label, payload):
        shown = payload[:MAX_LOGGED_BYTES]
        LOG.info("gateway %s from %s (%d bytes): %s%s", label, addr, len(payload), shown.hex(),
                 " ..." if len(payload) > len(shown) else "")
        _trace("packet", peer=addr[0], label=label, size=len(payload), hex=payload[:512].hex())
