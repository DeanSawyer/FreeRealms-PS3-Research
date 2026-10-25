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
