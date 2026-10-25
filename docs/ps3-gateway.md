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

`PacketLoginReply` is `u16 LE opcode 2` followed by a `u8` success flag. The probe sends `02 00 01` on reliable channel 1
and logs whatever the client sends next. This is the first step beyond the handshake; what the PS3 client expects after
a successful login (initialization details, zone details, game settings, item definitions, the player itself in the
PC protocol) is not known yet and the PS3 build (1.77) is much older than the PC build in Clover, so those packets will
probably differ.

## Not known yet

- Whether `Key` from the login reply is used to encrypt later packets (Clover hardcodes encryption off).
- Everything after the login reply.
