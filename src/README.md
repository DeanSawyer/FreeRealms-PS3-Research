# Free Realms PS3 Server Manager

A Windows app (**.NET 10, WPF**) that runs the PS3 server ([`ps3/`](ps3/README.md)) in-process and shows what it is doing:
start and stop, the state of each service, connected players, the gameplay switches, a filtered live log, test runs in RPCS3
and diagnostics reports.

## Install

1. Download `FreeRealmsPS3Bootstrap-Setup.exe` from the [latest release](https://github.com/DeanSawyer/FreeRealms-PS3-Research/releases).
   Between releases, open a successful run of the
   [Windows build](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml) and download the
   artifact `FreeRealmsPS3Bootstrap-Windows-Setup` (GitHub requires a login for artifacts).
2. Run the installer. The manager asks for administrator rights when it starts, because the DNS stand-in binds UDP 53 and the
   web server TCP 80.

No game files are packaged; you need your own `NPUA30048` client folder.

## Pages

| Page | What it does |
| :--- | :--- |
| **Dashboard** | **Start server** / **Stop server**; a card for each service (DNS, web server, game server) with its state and port; uptime, connected players, packets received and the last character created; the recent activity. **Test run in RPCS3** starts the server if needed, then RPCS3 on the game, and saves a report when RPCS3 is closed. |
| **Players** | Every connected PS3 client, refreshed each second: online id and character, address, current job, state (connecting, logged in, in world), packets received, time since the last packet and since it connected. |
| **Gameplay** | **Sanctuary flow** (what the PC server Sanctuary sends, with inventory and job switching; on by default), **starter items**, **jobs** (`all` or a list of ids), **sky** (keep `sky_wilds24.xml`), **spawn point**, how the creator's clothes go on, and the basic flow's welcome screen and game time. **Save and restart server** applies them. |
| **Settings** | Client folder; an optional second client folder and original patch digest; whether the digest describes your local files; `rpcs3.exe` and `game.elf`; restoring the original game scripts before a test run; listen address, the address the PS3 connects to, the web and game server ports, and the DNS stand-in. |
| **Logs** | The live log of every service. Search, filter by level (all, warnings, errors) and by source (game server, web and DNS); pause, copy, clear. |
| **Diagnostics** | Start a test run; **Export now** saves the logs and traces to a zip on the Desktop without RPCS3; open the trace and settings folders; check GitHub for a newer release. |

Nothing is assumed about where your files are. When you choose `rpcs3.exe`, the manager fills in the game RPCS3 has
installed, if those fields are still empty: `dev_hdd0\game\NPUA30048` as the client folder, and its `USRDIR\game.elf` (or
`EBOOT.BIN`) as the game. Change either if your client folder is elsewhere. Your choices are saved for next time.

## Test reports

A test run (or **Export now**) writes a folder and a zip named `fr_test_<date>_<time>` (or `fr_diagnostics_…`) to the Desktop with:

| File | Contents |
| :--- | :--- |
| `steps.txt` | What the test run did, and the gameplay switches it used |
| `server.log`, `lines_of_interest.txt` | The server's log during the run, and the lines that matter most |
| `rpcs3_log_tail.txt`, `rpcs3_log_filtered.txt` | The end of RPCS3's log, and its lines about packs, patches and crashes |
| `config_*.yml`, `patches_*.yml` | RPCS3's settings and game patches |
| `ps3_gateway_trace_tail.jsonl`, `ps3_protocol_trace_tail.jsonl` | The end of the server's trace files |

Redact before sharing: the files can contain local paths and your online id.

## Files

`%LOCALAPPDATA%\FreeRealmsPS3Server` holds `settings.json` and the `traces` folder.

## Run from source

With the .NET 10 SDK, from an Administrator PowerShell:

```powershell
dotnet run --project src\FreeRealms.Bootstrap
```

## Build pipeline

The [Windows build](../.github/workflows/windows-build.yml) runs on every pull request that touches `src/` and on every push to
`main`. It publishes the manager and the console server as self-contained single files and builds the Inno Setup installer
([`installer/FreeRealmsBootstrap.iss`](../installer/FreeRealmsBootstrap.iss)). A `v*` tag publishes both as a GitHub release, which
**Check for updates** reads.
