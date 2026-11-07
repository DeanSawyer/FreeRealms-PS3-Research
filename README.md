<div align="center">

# Free Realms — PlayStation 3 Research & Preservation

**An independently written server for the PlayStation 3 release of *Free Realms*, and the research behind it.**
A C# server and a Windows server manager, documented protocols, and nothing proprietary.

[![Status](https://img.shields.io/badge/status-active%20research-2563eb?style=flat-square)](#current-status)
![Platform](https://img.shields.io/badge/platform-PlayStation%203%20%2F%20RPCS3-374151?style=flat-square)
![Title ID](https://img.shields.io/badge/title%20ID-NPUA30048%20(build%201.77)-374151?style=flat-square)
![C#](https://img.shields.io/badge/C%23-.NET%209%20%2F%2010-512bd4?style=flat-square)
![License](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue?style=flat-square)
[![Server](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/dotnet.yml)
[![Windows](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml/badge.svg)](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml)

[Status](#current-status) · [Quick start](#quick-start) · [Server Manager](#the-server-manager) · [How it works](#how-it-fits-together) · [Development](#development) · [Documentation](#documentation) · [Roadmap](#roadmap)

</div>

---

## Overview

*Free Realms* (Sony Online Entertainment, 2009) was an online multiplayer game that also shipped on the PlayStation 3. Its
servers are gone. This project works out, from the client itself, **what the PS3 client expects from its servers**, and
provides a server that answers it, so the game can be studied and played again in [RPCS3](https://rpcs3.net/).

The server is written in C# in the conventions of [Sanctuary](https://github.com/Open-Source-Free-Realms/Sanctuary), the C#
*Free Realms* server emulator, and sends what Sanctuary sends wherever the PS3 build agrees. Every finding is written down with
the `game.elf` address or the test run that supports it.

> [!IMPORTANT]
> **This is a research project, not a finished game server.** You can create a character, enter the world, see yourself in
> your clothes and switch jobs. Other players, chat, quests and saving are not there yet (see [Current status](#current-status)).
> No game client, asset, executable or key is distributed here: you need your own legally obtained copy.

## Current status

Last updated: **7 November 2026.** Everything below was observed in RPCS3 with the server in this repository.

| Stage | State | Where it is documented |
| :--- | :--- | :--- |
| DNS redirect, `AppConfig`, patch digest and `.zs` asset downloads | ✅ Working: the installer reaches 100 % | [`docs/install/digest-format.md`](docs/install/digest-format.md) |
| Web services (`serverList`, `account/info`, character create and login) | ✅ Working | [`docs/protocol/ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| Game-server connection (SOE UDP gateway, login, zone, player data) | ✅ Working | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| **Entering the world**: the loading screen drops, your character stands in FabledRealms | ✅ Working | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| The clothes picked in the character creator | ✅ Working | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| 17 jobs with Sanctuary's starter outfits and weapons, items in the equipment screen | ✅ Working | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| Equip, unequip and **job switching** | ✅ Working | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| Looking at the sky (it used to crash the renderer) | ✅ Fixed | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| Marketplace (buying with Station Cash) | 🔶 Being mapped | — |
| Other players, movement relay, chat, quests | ⬜ Not started | — |
| Saving characters, accounts | ⬜ Not started | — |

## Quick start

**You need**

* Windows with [RPCS3](https://rpcs3.net/) and **your own** Free Realms PS3 client folder (`NPUA30048`, with
  `USRDIR\Digests\NPUA30048.soe`). Tests so far used a decrypted `game.elf` (RPCS3 → *Utilities → Decrypt PS3 Binaries*) and
  RPCS3's game patch *“Free Realms PS3 select exceptfds compatibility”*.
* Administrator rights: the server binds UDP 53 (DNS) and TCP 80 (web). Use an isolated test machine or network.

**With the Server Manager (recommended)**

1. Download `FreeRealmsPS3Bootstrap-Setup.exe` from the [latest release](https://github.com/DeanSawyer/FreeRealms-PS3-Research/releases),
   or the `FreeRealmsPS3Bootstrap-Windows-Setup` artifact of a successful
   [Windows build](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml), and install it.
2. Open **Free Realms PS3 Server Manager**. In **Settings**, choose `rpcs3.exe` (the game it has installed is filled in), check
   the client folder, and press **Save settings**.
3. On the **Dashboard**, press **Test run in RPCS3**. The server starts, then RPCS3 starts on the game.
4. Create a character and play. When you close RPCS3, a report zip is saved to your Desktop.

**With the console server**

```powershell
dotnet run --project src\ps3\Sanctuary.Ps3.Server -- --client "C:\Path\To\NPUA30048" --match-local
```

Then start the game in RPCS3. Options and gameplay switches are in [`src/ps3/README.md`](src/ps3/README.md). A self-contained
`Sanctuary.Ps3.Server.exe` (no .NET needed) is attached to each release and Windows build.

> [!CAUTION]
> Never expose ports 53, 80 or 20260 to the internet. The server has no authentication and is not hardened.

## The Server Manager

A Windows app (WPF, .NET 10) that runs the server in-process and shows what it is doing.

| Page | What it does |
| :--- | :--- |
| **Dashboard** | Start and stop the server; the state of the DNS, web and game services with their ports; uptime, players, packets, your character; recent activity. **Test run in RPCS3** starts everything and saves a report when RPCS3 closes. |
| **Players** | Every connected PS3: online id, address, current job, state (connecting, logged in, in world), packets and timings. |
| **Gameplay** | The server's behaviour without environment variables: the Sanctuary flow, starter items, jobs, sky, spawn point and how the creator's clothes go on. |
| **Settings** | Client folder, an optional second client folder and original digest, `rpcs3.exe` and `game.elf`, network addresses and ports. Choosing `rpcs3.exe` fills in the game RPCS3 has installed. |
| **Logs** | The live server log with search and filters by level and source; pause, copy, clear. |
| **Diagnostics** | Export logs and traces to a zip at any time; open the trace and settings folders; check for updates. |

Settings, traces and logs live in `%LOCALAPPDATA%\FreeRealmsPS3Server`. More in [`src/README.md`](src/README.md).

## How it fits together

```text
 PS3 client (in RPCS3)                        Sanctuary.Ps3 (run by the Server Manager or the console server)
 ─────────────────────                        ───────────────────────────────────────────────────────────────
 resolves ps3.freerealms.com  ──DNS :53──▶    Web/Dns.cs            points the game's host names at this PC
 AppConfig, digest, .zs files ──HTTP :80──▶   Web/, Install/        patch digest and assets from your own install
 /ps3ws/*.action calls                        Web/Ps3WebService.cs  server list, account, character create and login
 SOE UdpLibrary connection    ──UDP :20260─▶  Gateway/              handshake, login, zone, player data, items, jobs,
                                                                    Sanctuary's world packets, inventory and job switches
 terrain, models, textures    (read from its own packs)             Assets/ reads the same packs to pick a zone and spawn
```

## Repository layout

```text
.
├── src/
│   ├── ps3/                     The server (see src/ps3/README.md)
│   │   ├── Sanctuary.Ps3/         Library: DNS, web, digest and .zs, pack readers, game server, Sanctuary flow, inventory
│   │   ├── Sanctuary.Ps3.Server/  Console program
│   │   └── Sanctuary.Ps3.Tests/   MSTest: unit tests and recorded reference conversations
│   └── FreeRealms.Bootstrap/    The Server Manager (WPF)
├── docs/                        Findings by topic: install, protocol, reverse engineering
├── installer/                   Inno Setup script for the Server Manager
└── .github/                     CI (server build and tests, Windows build and installer), issue and PR templates
```

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The server library targets `net9.0` like the Sanctuary
solution; the Server Manager targets `net10.0-windows`.

```powershell
dotnet test src\ps3\Sanctuary.Ps3.slnx                                       # build and run the server's tests
dotnet run --project src\FreeRealms.Bootstrap                                 # the Server Manager (Windows)
dotnet publish src\FreeRealms.Bootstrap -c Release -r win-x64 --self-contained  # a standalone build
```

Besides unit tests, the tests replay **recorded conversations** (`*Vectors.g.cs`): byte-for-byte answers to real client
packets, including whole sessions with the Sanctuary flow, inventory requests and job switches. A change in what the server
sends turns the build red.

The project began with a Python reference server and research tools (a PowerPC tracer for `game.elf`, pack and digest readers,
the original test script). They are kept, unchanged, on the
[`python-archive`](https://github.com/DeanSawyer/FreeRealms-PS3-Research/tree/python-archive) branch; `main` is C# only.

## Documentation

Start at the [documentation index](docs/README.md).

| Topic | Document |
| :--- | :--- |
| How the installer and the patch digest work | [`docs/install/digest-format.md`](docs/install/digest-format.md) |
| HTTP web-service formats | [`docs/protocol/ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| UDP game-server protocol, items, inventory, and the session log | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| Packs, manifest, name hash, request path | [`docs/re/assets.md`](docs/re/assets.md) |
| Zone loading, the loading overlay, the sky crash | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| Every code address the notes rely on | [`docs/re/addresses.csv`](docs/re/addresses.csv) |
| The server: projects, options, gameplay switches, tests | [`src/ps3/README.md`](src/ps3/README.md) |
| The Server Manager | [`src/README.md`](src/README.md) |

## Roadmap

- [x] Answer the client's start-up requests; decode the patch digest and `.zs` container; complete the install in RPCS3
- [x] Complete the UDP gateway handshake and the game login
- [x] Drop the loading screen and enter the world
- [x] Dress the character, give every job Sanctuary's starter items, equip and switch jobs
- [x] One C# server with a Windows manager
- [ ] Marketplace: browse and buy
- [ ] Movement, other players and chat
- [ ] Save characters and items (Sanctuary's database)
- [ ] Accounts linked to the Clover server
- [ ] Offer the PS3 client as a second client option in Sanctuary / Clover

A tick means something observed working in RPCS3, not a finished game feature.

## Contributing

Evidence-backed findings, fixes and features are welcome. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for how to report a
finding, what to include, and what must never be committed. Security notes are in [`SECURITY.md`](SECURITY.md).

## License

Copyright © 2026 the FreeRealms-PS3-Research contributors. Licensed under the **GNU Affero General Public License v3.0 or later**
([`LICENSE`](LICENSE)), consistent with its sources: the packet layouts and game data follow Sanctuary / Clover (AGPL-3.0). See
[`CREDITS.md`](CREDITS.md). The license covers this repository's code and text, not *Free Realms* itself.

## Legal & preservation notice

*Free Realms* and related trademarks, content and intellectual property belong to their respective rights holders. This is an
**unofficial, independent, non-commercial** research effort, not affiliated with or endorsed by Sony Online Entertainment,
Daybreak Game Company, Sony Interactive Entertainment or any related entity.

**No game client, copyrighted asset, proprietary executable, game-specific key or patch payload is included.** Users and
contributors must obtain any software lawfully and follow applicable law.

---

<div align="center"><sub>Preserve the evidence · Reproduce the behaviour · Document what we learn</sub></div>
