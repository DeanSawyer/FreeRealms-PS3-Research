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
*Free Realms* server emulator, and sends what Sanctuary sends wherever the PS3 build agrees. Where the 2011 PS3 build differs
from the PC client Sanctuary was written for, the difference is found in the client's code and written down. Every finding
carries the `game.elf` address or the test run that supports it.

Today the PS3 client installs, logs in, creates a character, loads into FabledRealms, dresses the character, shows every job's
starter items and switches jobs, all against this server.

> [!IMPORTANT]
> **This is a research project, not a finished game server.** It is single-player, nothing is saved yet, and several
> features are still in progress (see [Current status](#current-status) and [Known issues](#known-issues)).
> No game client, asset, executable or key is distributed here: you need your own legally obtained copy.

## Current status

Last updated: **13 November 2026.** Everything marked working was observed in RPCS3 with the server in this repository.

| Area | State | Notes | Documented in |
| :--- | :--- | :--- | :--- |
| DNS redirect, `AppConfig`, patch digest, `.zs` asset downloads | ✅ Working | The installer reaches 100 % | [`digest-format.md`](docs/install/digest-format.md) |
| Web services: server list, account, character create and login | ✅ Working | | [`ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| Character creator: appearance and clothes | ✅ Working | What you pick is what you wear in the world | [`zone-loading.md`](docs/re/zone-loading.md) |
| Character creator: **custom names** | ❌ Not working | Typing your own name fails; use a suggested name for now | [`ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| Game-server connection: SOE UDP gateway, login, zone, player data | ✅ Working | | [`gateway.md`](docs/protocol/gateway.md) |
| **Entering the world** | ✅ Working | The loading screen drops and your character stands in FabledRealms | [`zone-loading.md`](docs/re/zone-loading.md) |
| The sky | ✅ Fixed | Looking up used to crash the renderer; the zone now names its sky | [`zone-loading.md`](docs/re/zone-loading.md) |
| Jobs | ✅ Working | The 17 jobs Sanctuary gives a new character, at rank 20 (no Card Duelist: the PS3 game had no card game) | [`gateway.md`](docs/protocol/gateway.md) |
| Starter items | ✅ Working | Every job's outfit and weapon, equipped on that job, listed in the equipment screen | [`gateway.md`](docs/protocol/gateway.md) |
| **Job switching** | ✅ Working | Your character re-dresses in the new job's outfit | [`gateway.md`](docs/protocol/gateway.md) |
| **Inventory** | 🔶 In progress | Equip and unequip are answered as Sanctuary does; the rest of the inventory is being worked on | [`gateway.md`](docs/protocol/gateway.md) |
| **Coin store** (buy and sell for coins) | ⏭ Next | Comes after the inventory | — |
| Marketplace (Station Cash) | 🔶 Partly mapped | The PS3 build uses an older store protocol than Sanctuary; resumes after the coin store | — |
| Other players, movement relay, chat | ⬜ Not started | Single player only | — |
| Quests, combat, mini-games | ⬜ Not started | | — |
| Saving characters and items, accounts | ⬜ Not started | Everything is kept in memory until the server stops | — |

### What a session looks like today

1. Start the server in the [Server Manager](#the-server-manager) and the game in RPCS3. The game's host names resolve to your PC.
2. The client checks its install against the patch digest and downloads anything missing.
3. You see one server and an account with no characters, so the game opens the character creator.
4. You create a character with a **suggested** name. The game connects to the game server and loads FabledRealms.
5. Your character appears wearing what you picked. With the Sanctuary flow on (the default), the server sends what Sanctuary
   sends: your stats, the item classes and categories, the adventurer's journal, the welcome screen, your area and the toolbar.
6. Open the equipment screen to see every job's starter items, and switch jobs to change outfits.

### Known issues

* **Custom names don't work.** Typing your own name in the character creator fails. The server's answer to the name check
  (`character/checkName`) is the first suspect; what the client expects there is listed as open in
  [`ps3ws-replies.md`](docs/protocol/ps3ws-replies.md#not-known-yet).
* **The inventory is unfinished.** Equip (by item and by item record), unequip and job switch requests are handled with
  Sanctuary's checks. The action bar, style cards, combat abilities and the rest of the inventory screen are not answered yet.
* **Nothing is saved.** The character, its items and its equipment live in memory: stopping the server forgets them, and every
  run starts with a new character.
* **Single player only.** The client's movement updates arrive but are not relayed; there are no other players or chat.
* **The character creator can be slow to appear** the first time it loads in RPCS3. Give it a minute before closing the game.
* Keep the sky setting (`sky_wilds24.xml`): without it the game crashes as soon as you look up.

## Quick start

**You need**

* Windows with [RPCS3](https://rpcs3.net/) and **your own** Free Realms PS3 client folder (`NPUA30048`, with
  `USRDIR\Digests\NPUA30048.soe`). Tests so far used a decrypted `game.elf` (RPCS3 → *Utilities → Decrypt PS3 Binaries*) and
  RPCS3's game patch *“Free Realms PS3 select exceptfds compatibility”*.
* Administrator rights: the server binds UDP 53 (DNS) and TCP 80 (web). Use an isolated test machine or network.

**With the Server Manager (recommended)**

1. Download the `FreeRealmsPS3Bootstrap-Windows-Setup` artifact from the newest successful
   [Windows build](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml) (or the setup file
   from [Releases](https://github.com/DeanSawyer/FreeRealms-PS3-Research/releases) once one is published), and install it.
   From source instead: `dotnet run --project src\FreeRealms.Bootstrap` in an Administrator PowerShell.
2. Open **Free Realms PS3 Server Manager**. In **Settings**, choose `rpcs3.exe` (the game RPCS3 has installed is filled in),
   check the client folder, and press **Save settings**.
3. On the **Dashboard**, press **Test run in RPCS3**. The server starts, then RPCS3 starts on the game.
4. Create a character with a suggested name and play. When you close RPCS3, a report zip is saved to your Desktop.

**With the console server**

```powershell
dotnet run --project src\ps3\Sanctuary.Ps3.Server -- --client "C:\Path\To\NPUA30048" --match-local
```

Set `FR_SANCTUARY=1` first for the Sanctuary flow with inventory and job switching (the manager turns it on by default), then
start the game in RPCS3. Every option and gameplay switch is in [`src/ps3/README.md`](src/ps3/README.md). A self-contained
`Sanctuary.Ps3.Server.exe` (no .NET needed) is built by each Windows build.

> [!CAUTION]
> Never expose ports 53, 80 or 20260 to the internet. The server has no authentication and is not hardened.

## The Server Manager

A Windows app (WPF, .NET 10) that runs the server in-process and shows what it is doing. It asks for administrator rights when
it starts.

| Page | What it does |
| :--- | :--- |
| **Dashboard** | Start and stop the server; the state of the DNS, web and game services with their ports; uptime, players, packets received and your character; recent activity. **Test run in RPCS3** starts everything and saves a report when RPCS3 closes. |
| **Players** | Every connected PS3: online id and character, address, current job, state (connecting, logged in, in world), packets and timings, refreshed every second. |
| **Gameplay** | The server's behaviour without environment variables: the Sanctuary flow, starter items, jobs, sky, spawn point and how the creator's clothes go on. **Save and restart server** applies them. |
| **Settings** | Client folder, an optional second client folder and original digest, `rpcs3.exe` and `game.elf`, network addresses and ports, the DNS stand-in. Choosing `rpcs3.exe` fills in the game RPCS3 has installed. |
| **Logs** | The live log of every service with search and filters by level and source; pause, copy, clear. |
| **Diagnostics** | Start a test run; export the logs and traces to a zip at any time; open the trace and settings folders; check for updates. |

A test report holds the server log and its most important lines, the end of RPCS3's log, RPCS3's settings and patches, and the
server's trace files. Settings and traces live in `%LOCALAPPDATA%\FreeRealmsPS3Server`. More in [`src/README.md`](src/README.md).

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

The game server has two flows. The **Sanctuary flow** (`FR_SANCTUARY=1`, the manager's default) sends what the PC server
Sanctuary sends at login, when the client is ready and when it finishes loading, and answers the inventory and job switches the
way Sanctuary's handlers do. The **basic flow** is the set of PS3 packets the world was first brought up with; it is kept for
comparison and for the tests.

## Repository layout

```text
.
├── src/
│   ├── ps3/                     The server (see src/ps3/README.md)
│   │   ├── Sanctuary.Ps3/         Library: DNS, web, digest and .zs, pack readers, game server, Sanctuary flow, inventory
│   │   │   └── Data/                Sanctuary's item definitions, item classes and categories
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
dotnet test src\ps3\Sanctuary.Ps3.slnx                                         # build and run the server's tests
dotnet run --project src\FreeRealms.Bootstrap                                   # the Server Manager (Windows, as administrator)
dotnet publish src\FreeRealms.Bootstrap -c Release -r win-x64 --self-contained  # a standalone build
```

Besides unit tests, the tests replay **recorded conversations** (`*Vectors.g.cs`): byte-for-byte answers to real client
packets, including whole sessions with the Sanctuary flow, inventory requests and job switches. A change in what the server
sends turns the build red. Every pull request is built and tested on Linux, and the Server Manager and its installer are built on
Windows.

The project began with a Python reference server and research tools (a PowerPC tracer for `game.elf`, pack and digest readers,
the original test script). They are kept, unchanged, on the
[`python-archive`](https://github.com/DeanSawyer/FreeRealms-PS3-Research/tree/python-archive) branch; `main` is C# only.

## Documentation

Start at the [documentation index](docs/README.md).

| Topic | Document |
| :--- | :--- |
| How the installer and the patch digest work | [`docs/install/digest-format.md`](docs/install/digest-format.md) |
| HTTP web-service formats, including what is still unknown about the name check | [`docs/protocol/ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| UDP game-server protocol, items, jobs, inventory, and the session log | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| Packs, manifest, name hash, request path | [`docs/re/assets.md`](docs/re/assets.md) |
| Zone loading, the loading overlay, the sky crash | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| Every code address the notes rely on | [`docs/re/addresses.csv`](docs/re/addresses.csv) |
| The server: projects, options, gameplay switches, tests | [`src/ps3/README.md`](src/ps3/README.md) |
| The Server Manager | [`src/README.md`](src/README.md) |

## Roadmap

- [x] Answer the client's start-up requests; decode the patch digest and `.zs` container; complete the install in RPCS3
- [x] Complete the UDP gateway handshake and the game login
- [x] Drop the loading screen and enter the world
- [x] Dress the character in the creator's clothes; fix the sky crash
- [x] Give every job Sanctuary's starter items; equip and switch jobs
- [x] One C# server with a Windows Server Manager
- [ ] **Inventory**: the rest of the inventory screen, the action bar and style cards *(in progress)*
- [ ] **Coin store**: buy and sell for coins *(next)*
- [ ] Custom character names
- [ ] Marketplace: browse and buy with Station Cash
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
