# Sanctuary.Ps3 — the PS3 server

The server side of the PlayStation 3 *Free Realms* client, in C#. It is written in the conventions of the Sanctuary solution (the C#
*Free Realms* server emulator behind Clover: `net9.0`, central package management, MSTest, file-scoped namespaces) so that it can
later be moved into it as a second client option.

| Service | Port | Code |
| :--- | :--- | :--- |
| DNS stand-in: points the game's host names at this PC | 53/udp | [`Web/Dns.cs`](Sanctuary.Ps3/Web/Dns.cs) |
| Web server: `AppConfig`, patch digest, `/ps3ws/*.action` replies, `.zs` asset downloads | 80/tcp | [`Web/`](Sanctuary.Ps3/Web), [`Install/`](Sanctuary.Ps3/Install), [`Assets/`](Sanctuary.Ps3/Assets) |
| Game server: SOE UdpLibrary transport, login, zone, player data, items and jobs, Sanctuary's world packets, inventory and job switches | 20260/udp | [`Gateway/`](Sanctuary.Ps3/Gateway) |

## Projects

| Project | What it is |
| :--- | :--- |
| `Sanctuary.Ps3` | The library: DNS, web front, digest and `.zs` container, pack, manifest and terrain readers, the game server, and the host (`Ps3Server.StartAsync`) that starts everything. Embeds Sanctuary's item definitions and item class and category tables (`Data/`). |
| `Sanctuary.Ps3.Server` | The console program around the library. |
| `Sanctuary.Ps3.Tests` | MSTest: unit tests, tests on loopback sockets, and the recorded reference conversations described below. |

The [Server Manager](../README.md) runs the library in-process.

### Inside the game server

| File | What it does |
| :--- | :--- |
| `Ps3Gateway.cs` | The UDP state machine: handshake, reliable channel and fragments, keep-alives, login, and the flow after it; a session snapshot for the manager |
| `Ps3Packets.cs` | The PS3 1.77 layouts: `ZoneDetails`, the player data and profile entries, `AddPc`, item definitions and `ItemAdd`, the clothes |
| `SanctuaryFlow.cs` | What Sanctuary sends at login, `ClientIsReady` and `ClientFinishedLoading` (stats, reference data, journal, welcome screen, areas, toolbar) |
| `Inventory.cs` | One session's items, each job's equipment and the active job; equip (42/3, 42/8), unequip (42/2) and job switch (26/13) with Sanctuary's checks |
| `Items.cs`, `Jobs.cs`, `Clothing.cs`, `ReferenceData.cs` | The starter items Sanctuary gives a new character, its 17 PS3 jobs, the creator's clothes, the item class and category tables |
| `CharacterMappings.cs` | The character creator's choices as the asset names the client draws (Sanctuary's mapping tables) |
| `GatewayWorld.cs`, `GatewayOptions.cs` | The zone and spawn picked from your install, and the gameplay switches below |

## Run it

Needs the .NET 10 SDK and your own `NPUA30048` folder. From an Administrator PowerShell (ports 53 and 80):

```powershell
dotnet run --project src\ps3\Sanctuary.Ps3.Server -- --client "C:\Path\To\NPUA30048" --match-local
```

`--help` lists every option. The main ones:

| Option | Effect |
| :--- | :--- |
| `--client DIR` | The `NPUA30048` folder (required) |
| `--match-local` | Serve a digest that describes the files actually in your install (for a modified or partial install) |
| `--digest FILE` | The original patch digest (default: `USRDIR\Digests\NPUA30048.soe` in the client folder) |
| `--extra-client DIR` | Another `NPUA30048` folder searched for a file whose CRC32 matches (repeatable) |
| `--bind IP`, `--advertise IP` | Listen address (`127.0.0.1`, or `0.0.0.0` for the network) and the address the PS3 connects to |
| `--port N`, `--gateway-port N`, `--no-dns` | Web server port (80), game server port (20260, `0` turns it off), no DNS stand-in |

A self-contained `Sanctuary.Ps3.Server.exe` is attached to each release and to each successful
[Windows build](../../.github/workflows/windows-build.yml).

## Gameplay switches

The console server reads these environment variables; the Server Manager sets them from its **Gameplay** page.

| Variable | Default | Effect |
| :--- | :--- | :--- |
| `FR_SANCTUARY` | off (manager: on) | `1`: send exactly what Sanctuary sends at login, `ClientIsReady` and `ClientFinishedLoading`, and answer the client as it does, including the inventory and job switches |
| `FR_SKIP` | — | With `FR_SANCTUARY=1`: leave out packets by name (`SetClientArea` drops every area) |
| `FR_ITEMS` | `1` | `0`: give the character no items. By default it owns the creator's clothes and every job's starter outfit and weapon, each job wearing its own |
| `FR_JOBS` | `all` | Which jobs the character has: `all` (17, at level 20) or ids such as `1,11,35`; Adventurer (1) is always included |
| `FR_SKY` | `sky_wilds24.xml` | The sky named in `ZoneDetails`. Without one the client crashes when the camera looks up; `FR_SKY=` with nothing sends none |
| `FR_EQUIP` | `7` | How the creator's clothes go on after loading: `7` all at once (EquippedItemsChange), `5` one piece at a time (EquipItem), `0` not at all |
| `FR_SPAWN` | Sanctuary's spawn | `local`: the centre of a tile stored in your packs (height `FR_SPAWN_Y`, default 400); `x,y,z`: exactly there |
| `FR_ZONE`, `FR_ZONE_TYPE` | from your install, `2` | Override the zone name and type sent in `ZoneDetails` |
| `FR_WELCOME`, `FR_TIME` | `1` | `0`: leave out the welcome screen or the game-time reply (basic flow) |
| `FR_EXTRA` | `0` | `1`: also send hit points, mana and game settings in the basic flow |
| `FR_TRACE_DIR` | current folder | Where the trace files `ps3_protocol_trace.jsonl` and `ps3_gateway_trace.jsonl` are written |

Without `FR_SANCTUARY` the server runs the **basic flow**: the PS3 layouts it was first brought up with, which also sends your own
`AddPc` after loading (the client does not leave the loading screen without one there).

## Tests

```powershell
dotnet test src\ps3\Sanctuary.Ps3.slnx
```

Besides unit tests, the suite replays **recorded reference answers**, committed as generated files:

| File | What it holds |
| :--- | :--- |
| `ReferenceVectors.g.cs` | Name hash, manifests, packs, zone and spawn choice, digest rewriting, the `.zs` container |
| `WebVectors.g.cs` | `AppConfig`, every ps3ws reply, DNS questions and answers, form decoding, the digest and asset routes |
| `GatewayVectors.g.cs` | Every packet builder for several characters, zones and spawns, `Login` parsing, and **whole UDP conversations** replayed on a virtual clock: connect, login, fragments, acks, resends, the ready fallback, and the Sanctuary flow with inventory requests and job switches |

They were recorded from the original Python reference server, which RPCS3 runs confirmed, and the server must keep answering
byte for byte the same. When a change to what the server sends is intended, update the affected vectors in the same change and
say why in the pull request. The recorder scripts are on the
[`python-archive`](https://github.com/DeanSawyer/FreeRealms-PS3-Research/tree/python-archive) branch.

CI ([`.github/workflows/dotnet.yml`](../../.github/workflows/dotnet.yml)) builds with compiler errors as annotations, runs the tests,
and smoke-tests the console program (argument errors, start-up, an HTTP answer, a clean stop).

## Notes

* **Codecs.** `--codec` accepts `zs-zlib-zero-trailer` (the default, the container the client accepts), `zs-zlib-digest-trailer` and
  `plain`. Compressed `.zs` output uses [ZLibDotNet](https://www.nuget.org/packages/ZLibDotNet), because .NET's own `ZLibStream` does
  not produce the exact bytes the client's length check expects.
* **HTTP stack.** Routing uses the raw, still percent-encoded request target and every answer is `Connection: close`. Kestrel may
  reject a malformed request before the code sees it; report it with the trace if that happens.

## Using it from another server (Clover)

The pieces are separable on purpose:

* `Ps3WebService.Handle(WebRequest) → WebResponse` is a pure function with no sockets; any web host can call it. `Ps3WebHost` is only
  the Kestrel wrapper.
* `Ps3Gateway` has no threads and no clock of its own: feed it `Receive(datagram, peer, now)` and `Tick(now)` and it sends through an
  `IGatewayTransport`. `UdpGatewayHost` is the plain UDP socket implementation; a host that already owns a UdpLibrary can supply its
  own transport instead.
* Packet builders (`Ps3Packets`, `SanctuaryFlow`) are static and take the character as data.

## Credits

See [`CREDITS.md`](../../CREDITS.md).
