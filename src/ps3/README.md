# Sanctuary.Ps3 — the C# port of the PS3 server

A C# implementation of the server side of the PlayStation 3 *Free Realms* client, written in the conventions of the
Sanctuary solution (the C# *Free Realms* server emulator behind Clover) so that it can later be moved into it as a second
client option. It does the same job as the Python scripts in [`bootstrap/`](../../bootstrap/README.md):

| Layer | Port | Code |
| :--- | :--- | :--- |
| DNS redirect for the game's host names | 53/udp | [`Web/Dns.cs`](Sanctuary.Ps3/Web/Dns.cs) |
| HTTP: `AppConfig`, patch digest, `/ps3ws/*.action` replies, `.zs` asset downloads | 80/tcp | [`Web/`](Sanctuary.Ps3/Web), [`Install/`](Sanctuary.Ps3/Install), [`Assets/`](Sanctuary.Ps3/Assets) |
| UDP game-server stand-in (SOE UdpLibrary handshake, `Login`, `ZoneDetails`, player data, `AddPc`) | 20260/udp | [`Gateway/`](Sanctuary.Ps3/Gateway) |

> [!IMPORTANT]
> **The Python scripts are the reference; this is the port.** Findings are made and tested in Python first
> ([`docs/`](../../docs/README.md) describes that behaviour), and the C# code is checked against it. The port has not yet been run
> against the real PS3 client in RPCS3 as often as the Python server has, so if the two ever disagree on a real run, Python is right.

## Projects

| Project | What it is |
| :--- | :--- |
| `Sanctuary.Ps3` | The library: web front, DNS, digest and `.zs` container, pack/manifest/terrain readers, gateway state machine and packet builders, and the host (`Ps3Server.StartAsync`) that starts everything. Targets `net9.0` like the Sanctuary solution. |
| `Sanctuary.Ps3.Server` | A console program around the library. Same options, defaults and log wording as the Python script. |
| `Sanctuary.Ps3.Tests` | MSTest project: unit tests, tests on loopback sockets, and the comparison with Python described below. |

The Windows manager in [`src/FreeRealms.Bootstrap`](../FreeRealms.Bootstrap) runs this library in-process by default.

## Run it

Needs the .NET 10 SDK (the projects build for `net9.0`, which the 10 SDK can target) and your own `NPUA30048` folder.
From an Administrator PowerShell (ports 53 and 80):

```powershell
dotnet run --project src\ps3\Sanctuary.Ps3.Server -- --client "C:\Path\To\NPUA30048" --match-local
```

The options are those of the Python script (`--client`, `--digest`, `--match-local`, `--extra-client`, `--codec`, `--serve-mismatch`,
`--server-list-variant`, `--existing-character`, `--account-status`, `--xml-style`, `--gateway-port`, `--advertise`, `--upstream`,
`--no-dns`, `--bind`, `--port`) and so are the gateway switches `FR_ZONE`, `FR_ZONE_TYPE`, `FR_SPAWN`, `FR_SPAWN_Y`, `FR_ADDPC`,
`FR_WELCOME`, `FR_EXTRA`, `FR_TIME`. [`bootstrap/README.md`](../../bootstrap/README.md) explains them; `--help` lists them.

To test it with RPCS3 the way the Python server is tested, download the artifact `Sanctuary.Ps3.Server-win-x64` of a successful run of the
[Windows installer workflow](../../.github/workflows/windows-build.yml) (a single `.exe`, no .NET needed) and run
`python tools\run_test.py --server-exe C:\Path\To\Sanctuary.Ps3.Server.exe`; the report then comes from the C# server's log and traces.

### Differences from the Python script

* **Codecs.** `--codec` accepts `zs-zlib-zero-trailer` (the default, the container the client accepts), `zs-zlib-digest-trailer` and
  `plain`. The early experiments that the client rejected (LZMA variants and the like) stay in `bootstrap/codec_candidates.py`,
  and so does `serve_lzma_terrain`, an early experiment of the Python server that is deliberately not ported.
* **Trace files.** `ps3_protocol_trace.jsonl` and `ps3_gateway_trace.jsonl` have the same events and fields. Without `FR_TRACE_DIR`
  they go to the *current directory* (Python: next to the scripts); the manager sets `FR_TRACE_DIR` to
  `%LOCALAPPDATA%\FreeRealmsPS3Bootstrap\traces`. `tools/run_test.py` reads the Python location.
* **HTTP stack.** Python's `http.server` accepts requests that Kestrel (the C# web server) may be stricter about. Routing uses the raw,
  still percent-encoded request target and every answer is `Connection: close`, as in Python, but a real PS3 client may send something
  Kestrel rejects before the code sees it. If a run works with the Python server and not with this one, that is the first
  place to look; please report it with the trace.

## How it is checked against Python

No C# toolchain is needed to keep the port honest. The Python code writes down what it answers, and the tests require C# to answer
the same:

| Generator (run by CI with `--check`) | Writes | What it records |
| :--- | :--- | :--- |
| [`tools/gen_ps3_vectors.py`](../../tools/gen_ps3_vectors.py) | `ReferenceVectors.g.cs` | name hash, manifests, packs, zone and spawn choice, digest rewriting, `.zs` container |
| [`tools/gen_ps3_web_vectors.py`](../../tools/gen_ps3_web_vectors.py) | `WebVectors.g.cs` | `AppConfig`, every ps3ws reply, DNS questions and answers, form decoding, the digest and asset routes |
| [`tools/gen_ps3_gateway_vectors.py`](../../tools/gen_ps3_gateway_vectors.py) | `GatewayVectors.g.cs` | every packet builder for several characters, zones and spawns, `Login` parsing, packet descriptions, and **whole UDP conversations** (connect, login, fragments, acks, resends, the ready fallback) replayed on a virtual clock |

The `*.g.cs` files are generated and committed; CI regenerates them and fails if they differ, so a change to the Python reference
that is not mirrored in C# turns the build red. Compressed `.zs` output is produced with
[ZLibDotNet](https://www.nuget.org/packages/ZLibDotNet), because .NET's own `ZLibStream` does not reproduce Python's zlib byte for byte
and the client checks the length.

## Using it from another server (Clover)

The pieces are separable on purpose:

* `Ps3WebService.Handle(WebRequest) → WebResponse` is a pure function with no sockets; any web host can call it.
  `Ps3WebHost` is only the Kestrel wrapper.
* `Ps3Gateway` has no threads and no clock of its own: feed it `Receive(datagram, peer, now)` and `Tick(now)` and it sends through an
  `IGatewayTransport`. `UdpGatewayHost` is the plain UDP socket implementation; a host that already owns a UdpLibrary can supply its
  own transport instead.
* Packet builders (`Ps3Packets`) are static and take the character as data.

## Tests and CI

CI ([`.github/workflows/dotnet.yml`](../../.github/workflows/dotnet.yml)) restores, builds with compiler errors as annotations, and runs
the tests on every change to `src/ps3`, `bootstrap` or the generators. Locally: `dotnet test src/ps3/Sanctuary.Ps3.slnx`.

## Credits

See [`CREDITS.md`](../../CREDITS.md).
