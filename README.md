<div align="center">

# Free Realms — PlayStation 3 Research & Preservation

**Independent reverse-engineering and preservation work on the PlayStation 3 release of *Free Realms*.**
A local test server, documented protocols, and tools to study the client — nothing proprietary included.

[![Status](https://img.shields.io/badge/status-active%20research-2563eb?style=flat-square)](#current-status)
![Platform](https://img.shields.io/badge/platform-PlayStation%203%20%2F%20RPCS3-374151?style=flat-square)
![Title ID](https://img.shields.io/badge/title%20ID-NPUA30048%20(build%201.77)-374151?style=flat-square)
![Python](https://img.shields.io/badge/python-3.10%2B-3776ab?style=flat-square)
![Focus](https://img.shields.io/badge/focus-digital%20preservation-16803c?style=flat-square)
![License](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue?style=flat-square)
[![Checks](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/checks.yml/badge.svg)](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/checks.yml)

[Status](#current-status) · [How it works](#how-it-fits-together) · [Quick start](#quick-start) · [Layout](#repository-layout) · [Documentation](#documentation) · [Roadmap](#roadmap) · [Contributing](#contributing)

</div>

---

## Overview

*Free Realms* (Sony Online Entertainment, 2009) was an online multiplayer game that also shipped on the PlayStation 3.
Its servers are gone. This project works out, from the client itself, **what the PS3 client expects from its servers** and
provides an independently written stand-in so the client can be studied and eventually run again.

The approach is evidence-driven: findings are read from the client binary or measured in an emulator, written down with
addresses and test runs, and separated clearly from hypotheses.

> [!IMPORTANT]
> **This is a research repository, not a playable release.** The client gets as far as logging in, picking a character and
> reaching the loading screen of the game world; the world itself does not appear yet (see [Current status](#current-status)).
> No game client, asset, executable or key is distributed here — you need your own legally obtained copy.

## Current status

Last updated: **25 October 2026.** Everything below was observed in RPCS3 with the local server in this repository.

| Stage | State | Where it is documented |
| :--- | :--- | :--- |
| DNS redirect and HTTP bootstrap (`AppConfig`, digest, web-service calls) | ✅ Working | [`bootstrap/README.md`](bootstrap/README.md) |
| Patch digest and `.zs` asset container (zlib stream + 129-byte trailer) decoded | ✅ Working — installer reaches 100 % | [`docs/install/digest-format.md`](docs/install/digest-format.md) |
| Client boots after install (`game.elf` decrypted with RPCS3) | ✅ Working | [`docs/install/digest-format.md`](docs/install/digest-format.md) |
| Web-service replies (`serverList`, `account/info`, characters, login) | ✅ Working | [`docs/protocol/ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| Game-server connection (SOE UDP gateway, login, zone, player data, `AddPc`) | ✅ Client reaches *FinishedLoading* | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| Pack and manifest formats, asset-name hash | ✅ Verified on a real install (54,146 of 54,146 names) | [`docs/re/assets.md`](docs/re/assets.md) |
| **Loading screen drops and the world appears** | 🔶 **Open** — screen stays up after *FinishedLoading* | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| Character visible, movement, chat, gameplay | ⬜ Not started | — |

**Where it stops today.** After *FinishedLoading* the client keeps its loading overlay until a per-frame check
(`IsLoaded` around the camera) succeeds. The zone name and the spawn tile have been ruled out; the remaining suspects and
the next diagnostic step are written up in [`docs/re/zone-loading.md`](docs/re/zone-loading.md).

## How it fits together

```text
 PS3 client (in RPCS3)                         this repository: bootstrap/
 ─────────────────────                         ───────────────────────────
 resolves ps3.freerealms.com   ──DNS :53──▶    fr_ps3_bootstrap.py   DNS redirect for the game's hostnames
 AppConfig / digest / .zs      ──HTTP :80──▶   fr_ps3_bootstrap.py   serves the patch digest and assets from
 /ps3ws/*.action calls                                                your own install, answers web-service calls
 SOE UdpLibrary connection     ──UDP :20260─▶  gateway_probe.py      handshake, Login, ZoneDetails, player data
 terrain, models, textures     (read from its own packs)             assetlib.py reads the same packs to pick a
                                                                      zone and spawn the client can really load
```

## Quick start

**You need**

* Windows with [RPCS3](https://rpcs3.net/) and **your own** Free Realms PS3 client folder (`NPUA30048`, with `USRDIR\Digests\NPUA30048.soe`).
  Tests so far used a decrypted `game.elf` (RPCS3 → *Utilities → Decrypt PS3 Binaries*) and RPCS3's game patch
  *“Free Realms PS3 select exceptfds compatibility”*.
* Python 3.10 or newer. The server and reports use only the standard library; two SDAT helpers also need `pip install cryptography`.
* An **Administrator** PowerShell (the server binds ports 53 and 80) on an isolated test network.

**Run one complete test** — starts the server, starts RPCS3, and packs the logs into a zip on your Desktop when you close RPCS3:

```powershell
python tools\run_test.py --install "C:\Path\To\TestFolder"      # folder that holds NPUA30048 and RPCS3_TEST
```

**Or start the server by hand**

```powershell
python bootstrap\fr_ps3_bootstrap.py --client "C:\Path\To\NPUA30048" --codec zs-zlib-zero-trailer --match-local
```

All options, the zone/spawn switches and the safety notes are in [`bootstrap/README.md`](bootstrap/README.md).
Never expose ports 53 or 80 to the internet; the server is a research tool with no hardening.

## Repository layout

```text
.
├── bootstrap/     Local test server (DNS, HTTP, UDP gateway) and the readers for the client's packs
├── tools/         Command-line utilities: install checks, asset and zone reports, binary analysis, Ghidra workspace
├── docs/          Findings, grouped by topic (see the index below)
│   ├── install/     Patch digest, .zs container, installer behaviour
│   ├── protocol/    Web-service replies and the UDP gateway protocol
│   ├── re/          Reverse engineering of game.elf: assets, zone loading, address table
│   └── history/     Earlier experiments kept for the record
├── src/           Windows manager (C#/WPF preview) that wraps the Python server
├── installer/     Inno Setup script for the Windows manager
└── .github/       CI workflow, issue and pull-request templates
```

## Documentation

Start at the [documentation index](docs/README.md). The main documents:

| Topic | Document |
| :--- | :--- |
| How the installer and the patch digest work | [`docs/install/digest-format.md`](docs/install/digest-format.md) |
| HTTP web-service formats | [`docs/protocol/ps3ws-replies.md`](docs/protocol/ps3ws-replies.md) |
| UDP gateway protocol and the session log | [`docs/protocol/gateway.md`](docs/protocol/gateway.md) |
| Packs, manifest, name hash, request path | [`docs/re/assets.md`](docs/re/assets.md) |
| Zone loading and the loading overlay | [`docs/re/zone-loading.md`](docs/re/zone-loading.md) |
| Every code address the notes rely on (machine-checked) | [`docs/re/addresses.csv`](docs/re/addresses.csv) |
| The first failed experiment (installer error 1004) | [`docs/history/installer-baseline.md`](docs/history/installer-baseline.md) |

Tools are indexed in [`tools/README.md`](tools/README.md).

## Roadmap

- [x] Intercept the client's startup requests locally
- [x] Decode the patch digest and `.zs` container; complete the install in RPCS3
- [x] Boot the client and answer its web-service calls
- [x] Complete the UDP gateway handshake and reach *FinishedLoading*
- [x] Read the pack and manifest formats and choose a zone and spawn from the real install
- [ ] **Find what holds the loading overlay and bring the world up**
- [ ] Make the character appear and respond (movement, basic world packets)
- [ ] Decode the remaining server packets the client sends after loading
- [ ] Package a documented, repeatable setup for other owners of the game

A tick means a specific observation, not a finished game feature.

## Contributing

Evidence-backed findings, corrections and tooling are welcome — see [`CONTRIBUTING.md`](CONTRIBUTING.md) for how to report a
finding, what to include, and what must never be committed. Security notes for the test server are in [`SECURITY.md`](SECURITY.md).

## License

Copyright © 2026 the FreeRealms-PS3-Research contributors. Licensed under the **GNU Affero General Public License v3.0 or later**
([`LICENSE`](LICENSE)). The same terms keep the repository consistent with its sources: two helpers
([`tools/undat.py`](tools/undat.py), [`tools/sdat_patch_loading.py`](tools/sdat_patch_loading.py)) are derived from RPCS3
(GPL-2.0-or-later, which allows use under version 3), and the packet layouts follow the Clover server (AGPL-3.0). The helpers use only the
generic container constants published in RPCS3's source. The license covers this repository's code and text, not *Free Realms* itself.

## Legal & preservation notice

*Free Realms* and related trademarks, content and intellectual property belong to their respective rights holders. This is an
**unofficial, independent, non-commercial** research effort, not affiliated with or endorsed by Sony Online Entertainment,
Daybreak Game Company, Sony Interactive Entertainment or any related entity.

**No game client, copyrighted asset, proprietary executable, game-specific key or patch payload is included.** Users and
contributors must obtain any software lawfully and follow applicable law.

---

<div align="center"><sub>Preserve the evidence · Reproduce the behaviour · Document what we learn</sub></div>
