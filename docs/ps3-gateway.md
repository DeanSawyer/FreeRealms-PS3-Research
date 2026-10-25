# PS3 game-server (gateway) connection

What the PS3 client does after `character/login`, observed with `bootstrap/gateway_probe.py` in RPCS3.

## Transport

- The client reads `GatewayAddress="host:port"` from the `CharacterLoginReply` and opens a UDP socket to it (confirmed:
  `127.0.0.1:20260` is used as given).
- It speaks the SOE UdpLibrary. The `Connect` packet has protocol version 3, a maximum raw packet size of 512 and
  the protocol name **`CGAPI_490`**. (Clover's PC gateway uses `CGAPI_527`.)
- A `Confirm` with no CRC bytes and no encryption methods is accepted. The client then sends its first reliable packet
  on channel 1.

## First application packet (client to server)

Same layout as Clover's `PacketLogin` (`Sanctuary.Packet/ClientGateway`). Strings are a `u32` little-endian length followed by the bytes.

| Field | Type | Seen |
|---|---|---|
| opcode | `u16` LE | `1` |
| ticket | string | `"0"`, the `GatewayTicket` from the login reply, echoed back |
| guid | `u64` LE | `1` |
| version | string | `1.77.389.188.2194.374262` (the PC client in Clover reports `1.910.1.530630`) |
| unknown | string | 14 characters, looks like the PSN or account name |

The packet is not wrapped or encrypted (no `0xF3D4A5C9` trailer).

## Reply

`PacketLoginReply` is `u16 LE opcode 2` followed by a `u8` success flag. The probe sends `02 00 01` on reliable channel 1,
then a ZoneDetails packet for zone `FabledRealms` (id 1, the starting zone in Clover), and logs whatever the client sends next. This is the first step beyond the handshake; what the PS3 client expects after
a successful login (initialization details, zone details, game settings, item definitions, the player itself in the
PC protocol) is not known yet and the PS3 build (1.77) is much older than the PC build in Clover, so those packets will
probably differ.

## Outer packets (read from the PS3 binary)

The client's gateway receive function (virtual method at `0x14624c`) switches on a `u16` LE opcode (0 to 9):

| Opcode | Meaning | Layout after the opcode |
|---|---|---|
| 2 | `LoginReply` | `u8` success |
| 5 | tunneled packet | `u8` reliable, `i32` LE size, payload; the payload goes to the game dispatcher |

The game dispatcher (`0x105000`) reads the payload's own `u16` LE opcode and uses a jump table for opcodes 12 to 180.
Each handler checks that its fields consume the payload exactly, as Clover's `RemainingLength == 0` check does.
The client's logging calls are compiled out (`0x11450` is `li 3,0; blr`), so a rejected packet gives no message.

## Inner packet 43, ZoneDetails

The handler near `0x106b14` (string `RECEIVED=ZoneDetails - %s`) reads exactly:

`string name | i32 type | u8 | u8 | string sky | u8 | i32 id`

Strings are a `u32` LE length followed by the bytes. This is the PC layout without the trailing `GeometryId` and two
bools, so the PS3 build is older. After this the client branches on `type` (1, 2 or 3).

Other inner opcodes that do **not** match the PC layout: 144 starts with a `u8` that selects one of 11 cases, whereas
Clover's `PacketClientGameSettings` (also opcode 144) is `i32, i32, i32, bool, float`. Opcode numbers match the PC game
in at least one case (43), but each packet has to be checked against the binary.

## Not known yet

- Whether `Key` from the login reply is used to encrypt later packets (Clover hardcodes encryption off).
- Everything after ZoneDetails: the layouts of the other packets the client waits for (initialization details, item
  definitions, the player itself) and what the client sends back.

## Inner packet 12, SendSelfToClient (player data)

Layout: `i32` length, then the player blob. The blob's field widths come from running the client's own deserializer
(`0xb1e460`) in `tools/ppc_trace.py` and recording every step of the reader's cursor. The order follows Clover's
`ClientPcData.Serialize` for the first part and then drops some PC-only fields (the five `*Id` appearance ints and
`IsOpenChatEnabled`). With every list empty the blob is 273 bytes:

| Offset | Width | Field (name from Clover where it matches) |
|---|---|---|
| 0x00 | u64 | launch ticket |
| 0x08 | u64 | guid |
| 0x10 | u32 | model |
| 0x14, 0x18 | string | head, hair |
| 0x1c, 0x20 | u32 | hair colour, eye colour |
| 0x24, 0x28, 0x2c | string | skin tone, face paint, model customization |
| 0x30 | 8 x f32 | position (x, y, z, w) and rotation |
| 0x50 | 3 x u32, 2 strings | name ids, first name, last name |
| 0x64 | u32 | coins |
| 0x68 | u64 | birthday |
| 0x70, 0x74 | u32 | age, play time |
| 0x78 | u8 | is underage |
| 0x79 | u32 | membership status |
| 0x7d | u8 | show member nag screen |
| 0x7e | 5 x u32 | chat country, chat language, preferred language, chat language, login count |
| 0x92 | u8 | grandfathered |
| 0x93, 0x97 | u32 | the two vehicle loadouts (kart race, demo derby) |
| 0x9b | u32 + n x 179 bytes | profile list: a count, then one entry per profile (below) |
| after the profiles | u32 | active profile id |
| the rest | | counts of the other lists (collections, items, pets, mounts, stats, ...) and a few scalars, all zero for now |

The generated blob is checked against the client's deserializer: it consumes every byte (`tools/ppc_trace.py
--file blob.bin`). That proves the widths line up; it does not prove the values make sense to the game.

### The profile list must not be empty

With an empty profile list the client crashed right after the player data arrived ("Access violation reading
location 0x0" in RPCS3). The cause, read out of the client: after loading the player it runs a routine at `0xeba4c`
that looks the active profile up in a small hash table inside the player object (5 buckets, key = profile id, taken
from the profile entries as they are read) and then reads through the node it found. With no profile there is no
node, so the read goes through a null pointer. One profile is enough. The emulator reproduces both cases
(`tools/ppc_trace.py` records reads below `0x1000` as `null_reads`): the empty list reads address 0 at `0xebb30`; one
profile with id 1 does not.

A profile entry is 179 bytes in this build: nine integers (id, name id, description id, type, icon, three image
sets, is-combat), a few flags and integers (rank, rank percent, stars), 17 equipment words, eight ability lists
(each starts with a count) and an ability-experience list. Only the id is filled in so far; everything else is zero.
This differs from Clover's `ClientPcProfile` (no item-class or item maps), so the PS3 build is an older layout.

## Packets larger than 512 bytes must be fragmented

The client reads each datagram into a buffer of exactly the negotiated max raw size (512). A bigger datagram makes
the console's network layer fail the read, and RPCS3 logs `sys_net_bnet_recvfrom failed ... SYS_NET_EMSGSIZE` /
`Socket error EMSGSIZE` once per resend. With a profile in it the player data packet is 560 bytes, so it never arrived:
the client waited two minutes and then logged `Error# G4 - Connected to the server at: ... but failed to load player
data` (ClientLog, opcode 109). The same message appeared in the very first test when no player data was sent at all,
so it only says that the player data did not arrive, not why.

The probe now sends such payloads as UdpLibrary fragments: type `00 0d` (13 + channel), the reliable sequence number,
and a 4 byte big endian total length in front of the data of the first fragment. Each fragment is acked like a normal
reliable packet. This follows UdpLibrary's reliable channel as remembered, so if the client still reports the player
data as missing after the EMSGSIZE errors are gone, check this format first.

## Observed: the loading step completes

With the fragmented player data and the world-ready packets, a run went: Login, LoginReply, ZoneDetails, player data
(fragmented, 2 pieces), the client's SetLocale and ClientInitializationDetails, ClientIsReady 3.5 s later, our 14 and
38/26, then the client's ClientFinishedLoading, two ClientLog lines, and requests 102/1 and 141/1 for the mini-game
list and matchmaking queues. So the client believes it is in the world. Not answered yet: 102/1, 141/1 and everything
that makes the character appear. After three minutes of silence the client sent Terminate.

## The connection times out after three minutes of silence

In the run above the client sent `Terminate` with reason 2 (UdpLibrary's timeout: it heard nothing from us) exactly three
minutes after our last packet, then exited; RPCS3 then reported an unhandled Win32 exception (`0xC0000005`, on a host
thread, not a PPU thread) while tearing the game down, which looks like an emulator exit crash rather than a game
bug. The probe now sends a KeepAlive packet (`00 06`) every five seconds to every connected peer, and logs the
reason of any Terminate it receives (6 = the client closed it itself).

## The loading step: WaitForWorldReady

The client has a loading step that logs (format string at `0xdec970`): `WaitForWorldReady Status:
InitialZoneDataComplete=%d ReceivedPreloadDonePacket=%d RequiredCharactersLoaded=%d`. It does not continue until all
three are 1.

| Flag | Set by |
|---|---|
| InitialZoneDataComplete (manager byte `+672`) | inner opcode 14, ZoneDoneSendingInitialData, no fields |
| ReceivedPreloadDonePacket (`+673`) | inner opcode 38 (ClientUpdate), `u16` sub-opcode 26, `u8` bool; only a true bool sets the flag |
| RequiredCharactersLoaded | the client itself, once the characters it needs are loaded |

Opcode 38 / 26 is Clover's `ClientUpdatePacketDoneSendingPreloadCharacters` (Clover sends the bool as false; this build
needs true). The probe sends 14 and then 38/26 when the client says ClientIsReady (opcode 13), and, if that never
comes, 20 seconds after the player data.

## Packets the client sends after the player data

They arrive bundled: one reliable payload that starts `00 19` (a UdpLibrary Group packet), then a length byte and a
tunneled packet for each, repeated. Seen so far, in order:

| Inner opcode | Name | Content |
|---|---|---|
| 88 | SetLocale | string, `en_US` |
| 169 | ClientInitializationDetails | `i32`, `-18000` (a timezone offset in seconds) |
| 13 | ClientIsReady | no fields; arrives about 3.5 s after the player data (it loads the zone first) |
| 102 | LobbyGameDefinition, sub-opcode 1 | `66 00 01 00`: Clover's DefinitionsRequest, the mini-game list. Outer packet type 6 |
| 141 | matchmaking, sub-opcode 1 | `8d 00 01 00 01 00 00 00 00 00 00 00`: Clover's ListQueuesRequest. Outer packet type 6 |
| 10 | ClientFinishedLoading | no fields; sent right after our 14 and 38/26 |
| 109 | ClientLog | `ClientZoneTime.log` (`3272 ms`) and `MarketingData.log` ("Viewed loading screen loadingscreen_memberupgrade.gfx for 3.27 seconds. Member=true, Zoning=true, Minigame Type=0") |

The probe unwraps Group and Multi packets, logs each inner packet by name, and answers ClientIsReady with
ZoneDoneSendingInitialData (opcode 14) and the preload-done update (see WaitForWorldReady above).

RPCS3 also logs `CELL_ENOENT` for `assets_ps3_078.pack`, `assets_ps3_079.pack` and `assets_ps3w_001.pack`: the client
probes for packs past the last one it ships and for a `w` set. They are not the cause of the loading hang.

## tools/ppc_trace.py

A small PowerPC emulator that runs one function from `game.elf` on a chosen input buffer and prints each field the
function reads (offset and width), found by watching the reader's cursor move. Because the game prints no logs, this
is how packet layouts are read out of the binary. Example for the player blob:

    python3 tools/ppc_trace.py game.elf 0xb1e460 --reader --object 33200 --buffer-size 4096 \
        --stub 0xa44aac=alloc --stub 0x11834=alloc --stub 0x1dbe4=alloc

To explore a list, put a count of 1 in the buffer at the list's offset (`--set OFFSET=01000000`) and run again; the
extra reads are the element layout.

## tools/ppc_explore.py

Finds the lists for you. It runs the same deserializer once per 4-byte field with that field set to 1 and prints the
fields that turn out to be counts, with the number and widths of the extra reads each one adds. For the player blob
it lists about a dozen counts (the profile list is the one at `0x9b`: 47 reads, 179 bytes). Use `--base HEX` to start
from a partly built buffer, so you can look inside one list element, and `--only OFFSET` to test one field.

    python3 tools/ppc_explore.py game.elf 0xb1e460 --reader --object 33200 \
        --stub 0xa44aac=alloc --stub 0x11834=alloc --stub 0x1dbe4=alloc
