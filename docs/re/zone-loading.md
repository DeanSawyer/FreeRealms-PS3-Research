# Zone loading: from `ZoneDetails` to the loading overlay dropping

What the client does with the zone name the server sends in inner packet 43 (`ZoneDetails`), and why a wrong name
leaves the loading screen up forever without any visible error. All of it was read from `game.elf`; each address is
in [`addresses.csv`](addresses.csv) and checked by `tools/re/verify_addresses.py`.

## Chain

```
ZoneDetails (inner 43)                      zone name, type, id          handler 0x10cb84
  └─ holder.Activate(type, name)            0x8981c4   picks implementation [type], calls Load(name),
       │                                               stores the result in holder+0x24 ("ok flag")
       ├─ ok flag 0  → handler logs "Failed to init the game world for: %s.  Exiting." (0x10cb84) and returns
       └─ TerrainController::Load(name)     0x7cf7b0   (only implementation 2 "RuntimeSeamless" is created at startup)
            ├─ request <name>.cob                        "%s.cob"   0x7cf7b0
            ├─ Terrain::Open → request <name>.gzne       "%s.gzne"  0x7de0dc → 0x7df72c  (returns false if refused)
            └─ ObjectDeployment: <name>.odd 0x7d13d8, TerrainObjectDeploymentGroups.txt 0x7d1294
  controller ctor 0x7cfb7c also requests TerrainCalendarDefinitions.txt

asset arrives → Terrain listener 0x7db374 → ParseHeader 0x7df87c → sets the ready byte (terrain+0x434)
TerrainController::Update 0x7cf398 creates the cell manager once the ready byte is set

every frame (main loop 0xef5e4..0xef66c and 0xf0494..0xf0520):
  IsLoaded(camera position, radius)  via holder 0x897db4 → controller 0x7cfdd4
     0 → HUD::SetLoadingWorldAssets(true)   0xdced8   (Lua HUD:LoadingWorldAssets(1), flag client+0x2a2)
     1 → HUD::SetLoadingWorldAssets(false)
```

The loading overlay is therefore tied to the *terrain being loaded around the camera*, not to the login handshake.
`WaitForWorldReady` (0xec3e8) has already returned by then.

## Zone type

The `type` field of `ZoneDetails` indexes the holder's implementations (Clover's names): 0 TileStatic,
1 TileSeamless, **2 RuntimeSeamless**, 3 Mesh. Only the controller for type 2 is set up at startup (`0xf2938`, a
176-byte object; `0x7cf7b0` is its `Load`). The bootstrap server sends type 2.

## `IsLoaded` (0x7cfdd4) — when it returns 0

1. the terrain object (`controller+0x18`) does not exist, or
2. its ready byte (`terrain+0x434`) is 0 (header not parsed), or
3. the cell manager (`controller+0x1c`) does not exist, or
4. some cell in the box `position ± radius` is *in range* and not yet loaded (`GetCell` `0x7ded28`).

Cells are `floor(coordinate / cellsize)` with `cellsize` the float at `terrain+0x444`.

## GZNE header (parser `0x7df87c`)

Magic `GZNE`, then numbers copied verbatim into native (big-endian on PS3) fields. `tools/gzne_info.py` prints them
for a real install.

| File off. | Client field | Meaning |
|---|---|---|
| 0x04 | — | version (2 adds one more value at the end) |
| 0x08 | +0x438 | `A` |
| 0x0C | +0x43C | `B`; the parser stores `A / B` at +0x440 (cells per tile edge) |
| 0x10 | +0x444 | cell size, float |
| 0x14 | +0x448 | unknown, copied only |
| 0x18 / 0x1C | +0x44C / +0x450 | minimum cell X / Z |
| 0x20 / 0x24 | +0x454 / +0x458 | extent in cells along X / Z |
| 0x28 | — | unknown, read into a local |
| 0x2C | — | count of entry records that follow |

`GetCell` (`0x7ded28`) rounds the cell down to a multiple of the tile edge (`divwu`, so it is exact for power-of-two
edges) and answers "loaded" at once when that lies outside `min .. min + extent` on either axis. So a camera
position outside the terrain's box is **not** what holds the overlay; only cells inside the box must be streamed.

## Tiles (read from `game.elf`)

`GetCell` (`0x7ded28`) keeps a cache of tile objects. For an in-range cell it creates one (`0x7e5d54`, vtable
`0xee3328`), initialises it with the zone name and the tile origin (`0x7e66c8`) and calls its load slot, which is
`0x7e71bc`. That function requests, through the asset delivery object:

    <zone>_<x>_<z>.gcnk     always                         ("%s_%d_%d.gcnk")
    <zone>_<x>_<z>.gck2     when the tile flag (+0x4278) is set   ("%s_%d_%d.gck2")

`x`, `z` are the tile origin in cells: the cell rounded down to a multiple of the tile edge (`A / B` from the header);
negative values print with a minus sign. The zone name is the one passed to `Load` (`0x7cf7b0`).

`IsLoaded(camera position, radius)` visits every cell from `floor((p - r) / cellsize)` to `floor((p + r) / cellsize)`
on both axes. The two call sites use different radii:

| Site | Radius | Meaning |
|---|---|---|
| `0xef650` (overlay is shown) | 96.0 (float at `0xde9d08`) | the overlay is hidden once every tile within 96 units is loaded |
| `0xf04f8` (overlay is hidden) | 32.0 (float at `0xde9d0c`) | the overlay is shown again if a tile within 32 units is missing |

The camera position at that moment is `*(client[0x20e] + 0x30) + 0x40` (see `Camera::Position`, `0x88ade8`); whether
it already equals the spawn position when the check first runs is **not verified**. `tools/tile_report.py` therefore
reports both the spawn and the world origin.

A tile is loaded (`0x7d3d78`, the same function that writes `Tile at lng:%d lat:%d waiting on ...` into the log
string) only when all of this is true: the `.gcnk` was parsed (`0x7e84a4` -> `0x7e7628`), `CreateAll()` and
`CreateObjects()` have run, and every object placed in the tile is itself loaded. The parser (`0x7e7628`) reads 16
sub-chunks of 0x40c bytes and an object table; the objects' own assets (models, textures) are further requests.
The `.gck2` request is only made when the tile's flag (`+0x4278`) is set, which `0x7e66c8` does when the settings
table passed in has the key `RuntimeTerrainFallback` (a hash lookup in `0x34520`). The user's packs hold no `.gck2`
at all, so in the default setup only `.gcnk` is expected.

The float-to-cell conversion (`0x449030`) behaves like a floor (it adjusts negative values); treat tiles one step away
from the computed ones as relevant too.

## Failure table (what a server-side or content problem looks like)

| Cause | What the client does | Visible result |
|---|---|---|
| Zone name not in the manifest | `0x802828` refuses the `.gzne` request → `Load` false → ok flag 0 → handler gives up | overlay never drops; no error screen |
| In manifest, not in packs / crc differs | not "current" (`0x801020`); a download is the apparent intent (inferred, not traced) | overlay until the asset is served |
| `.gzne` parse fails | listener calls the failure stub (vtable +0x14) | overlay never drops |
| Header ok, tiles not yet streamed | `Bubble not ready` (`0x7da88c`) until the first tiles arrive | overlay, then drops |
| A needed tile is in the manifest but not in the packs | not "current"; download intended (inferred) | overlay until the file is served |
| A needed tile is in neither | request refused (`0x802828`); the tile object is dropped again by `GetCell` | overlay (not traced further) |
| `type` ≠ 2 | the holder has no implementation for it | overlay never drops |

The first row was the leading explanation for the stuck screen while the server still sent `FabledRealms`, a name taken
from the PC game. **It is ruled out**: `tools/zone_report.py` on the real install found `FabledRealms.gzne/.cob/.odd` in the
packs and the manifest (next section).

## What the first real install showed

`tools/zone_report.py` on the user's install (77 packs, 54,146 assets, manifest 136,812 records): all 54,146 pack
names are found in the manifest by the CRC-64 (this confirms the pack and manifest formats and the hash), and
`FabledRealms.gzne/.cob/.odd` are present in packs and manifest. So the zone name is **not** the cause of the stuck
screen. The manifest lists about 82,000 assets that are not in the packs: those are only available by download.
`tools/tile_report.py` then showed: tile edge 4 cells, cell size 64 (a tile is 256 world units), terrain box x -32..48,
z -48..16 cells (320 tiles); 198 `.gcnk` are in the packs and all 320 in the manifest; no `.gck2` is stored.
The PC game's spawn (-1904.9, 412.6) lies in tile (-32, 4), which is **only in the manifest** (23,370 bytes), so the
client has to download it. The four tiles around the world origin are all local. Since an origin camera would
have dropped the overlay, the camera is probably at the player's spawn (not proven). The server therefore now spawns
the player in the centre of a tile whose 3 x 3 neighbourhood is local (`assetlib.pick_spawn`); `FR_SPAWN="x,y,z"`
overrides it. The same position goes into the player's own data (`SendSelfToClient`, opcode 12), not only into `AddPc`: the first v18-v20 runs
still had the old position there, so the camera stayed on the missing tile. The height of the ground there is unknown; the default `y` is 400 (`FR_SPAWN_Y`).

## The per-frame overlay check (`fn_000ee7bc`)

The loading overlay (client flag at `client+0x2a2`) is switched by one block in the client's per-frame function
(`fn_000ee7bc`, decompiled around `0xef5e4`-`0xf0494`). It runs **only when four gate objects are all idle**:

| gate | pointer in client | byte that must be 0 |
|---|---|---|
| 1 | `client[0x21e]` | `+0x18` |
| 2 | `client[0x21b]` | `+0x174` |
| 3 | `client[0x21f]` | `+0x8` |
| 4 | `client[0x21c]` | `+0x390` |

(a null pointer counts as idle). If all four are idle:

* overlay off -> `IsLoaded(camera position, R_show)` (`0x897db4`); if it returns false -> `SetLoadingWorldAssets(1)` (`0xdced8`);
* overlay on  -> `IsLoaded(camera position, R_hide)`; if it returns true -> `SetLoadingWorldAssets(0)`.

`R_show` defaults to 32.0 and `R_hide` to 96.0; each can be replaced by a config-map entry (keys `0x63bfdb08` and `-0x6c4fec45`
as stored in the code). The camera position is `*(client[0x20e] + 0x30) + 0x40` (`0x88ade8`). So the overlay stays up
when **either** a gate byte is set (the check never runs) **or** `IsLoaded` stays false at the camera.

The Lua UI is not what holds it: the Lua `IsLoadingFinished` calls only ask whether individual Flash movies have loaded.

## Test results so far (zone `FabledRealms`)

| run | what the server sent | result |
|---|---|---|
| v17-v19 | zone ok, spawn at the PC game's position (tile only in the manifest) | overlay never drops |
| v20 | `AddPc` at tile (0,-4) centre, 3 x 3 neighbours local | overlay never drops (self data still had the old position) |
| v21 | same position in `AddPc` **and** `SendSelfToClient` | overlay never drops; no tile or extra asset requests reach the server |

So the zone name and the spawn tile are no longer the explanation. What is not known is *which* of the following holds it:
a set gate byte, a terrain object that is never ready, tile objects (models/textures named in the `.odd`/`.gcnk`)
that are not local, or a camera that is not where the player is. Reading more code will not separate these; the next step is
a runtime probe: patch the game in RPCS3 so `IsLoaded` (`0x897db4`) returns true, and see whether the overlay drops
(then the check is the problem and the gate bytes are fine) or not (then a gate byte is set or something else holds it).

## How the server decides

`gateway_probe.choose_zone_from_install()` runs `assetlib.pick_zone()` over the user's packs and manifest and sends the
first zone that has `.gzne`, `.cob` and `.odd` present and manifest-listed (preferring `FabledRealms`). Override with
`FR_ZONE` / `FR_ZONE_TYPE`.

## Test plan

Done: steps 1-3 (zone exists, terrain header read, spawn chosen from tiles stored in the packs, overlay still holds).
Next:

1. Learn which part of the per-frame check fails. Patch the game in RPCS3 so `IsLoaded` (`0x897db4`) returns true: if the
   overlay drops, the check is what fails and the four gate bytes are fine; if not, a gate byte is set or something else holds it.
   `tools/rpcs3_diag_patch.py` adds that patch to a portable RPCS3 folder and removes it again. It replaces the two `IsLoaded` calls
   inside the overlay check (`0xef660`, `0xf0504`) with `li r3,1`; patching `IsLoaded` itself also changed three other callers and
   stopped the character creator from appearing.
2. If the check itself fails: tile streaming and bubble readiness (`0x7da88c`, `0x7dafb0`, `0x7dad04`, `0x7e66c8`) and the
   assets the tile objects need (models and textures named in the `.odd`/`.gcnk`) — these need runtime data, not more static reading.

## Result of the `overlay check forced loaded` run (zone `FabledRealms`)

RPCS3 applied the patch (`PAT: Applied patch ... 'Free Realms overlay check forced loaded'`) and the user still saw the
loading screen. So `IsLoaded` was **not** what held it, and the terrain around the camera is not the explanation.

What the run's logs showed:

* The server did everything it is built to do: server list, character create, login, `ZoneDetails`, player data, the
  client's `ClientIsReady`/`ClientFinishedLoading`, then the `AddPc` helper one second later.
* The client logged `ClientZoneTime.log` (~3.3 s) and `MarketingData.log` ("Viewed loading screen ... Zoning=true")
  every time, with or without the patch. That pair is written by `WaitForWorldReady` (`0xec3e8`, string at `0xed8cc`)
  when it **finishes**, so `WaitForWorldReady` completes normally. It is not evidence about the overlay.
* RPCS3 shows two `LoadingScreenThread`s. The first (startup) exits after ~6 s. **The second one, created at the zone
  change, never exits**: it is still in its `sys_timer_usleep` loop when the game is closed 66 s later, and there is
  only one `sys_ppu_thread_join` in the whole log. After the login the game opens no further files, compiles no
  further shaders and sends no further packets.

Loading screen thread (read from `game.elf`):

| Address | What |
|---|---|
| `0x18698c` | starts the loading screen: creates the thread object (vtable `0xec2858`, name "LoadingScreenThread") and stores it in `this[2]` |
| `0xb64180` | thread body: `while (!stopRequested(this)) { FUN_00185ef8(this) /* draw a frame */; usleep(25 ms) }` |
| `0x7ea068` | `stopRequested`: byte at `*(this+0x94) + 8` |
| `0x7ea164` | the setter for that byte (`stb 1, 8(*(this+0x94))`); no direct caller found yet |

So the loading screen stays up until something sets that stop byte. `WaitForWorldReady` finishing is not enough.
Finding who calls the stop path (probably through a virtual call) is the next lead.

Gate objects (the "four idle objects" above), read from the client constructor `0xf5840`:

| Gate | client field | class ctor | byte | set by |
|---|---|---|---|---|
| 1 | `+0x878` | `0x36481c` | `+0x18` | `0x3672dc`; callers: the actor-flags handler, and `0xe1728` (clears it) |
| 2 | `+0x86c` | `0x2d203c` | `+0x174` | `0x2d7388`, only from the actor-flags handler |
| 3 | `+0x87c` | `0x2c2fd8` | `+0x8`   | `0x2c910c`, only from the actor-flags handler |
| 4 | `+0x870` | `0x2ea710` | `+0x390` | `0x2ece98`, only from the actor-flags handler |

The actor-flags handler is a case of the PlayerUpdate dispatcher (`0x10fda4`, packet 35): it looks up the actor by guid,
calls `SetFlags` (`0x1a0bf0`, stores `actor+0x4a0`) and, for the local player, copies bits 10, 11, 12 and 22 of the new
flags into gates 2, 3, 4 and 1. The server never sends it, so these gates start at 0 and stay idle; the gates are probably
not the cause either.

WaitForWorldReady's own status line (format at `0xed9d0`) is `IsZoneReady, HaveProxiedCharacter, HaveProxiedActor,
ActorReadyToDraw, SkyFinishedLoading, SkyDefinitionSet`; it also writes `BadSkyData.log` when the sky definition is bad.

Not done: the game's own log never reaches RPCS3's TTY, and the free space in the executable for a patch that
prints to the TTY is limited (the only zero padding is a 96-byte gap at `0xea0a20` that an existing patch already uses).

## Who stops the loading screen (callers of `0x7ea164`)

The setter of the stop byte, `0x7ea164`, has five direct callers (an earlier search found none). It is a generic
"ask this thread to stop" call, also used by the destructors of other worker threads (`0x3708c`, `0x37c34`, `0xdf630`) and
`0x7f9ebc`. The loading screen goes through this chain, all read from `game.elf`:

```
Client::ShowLoadingScreen  0xc72f8  -> Manager::Start(w,h) 0x14420c  -> restarts the LoadingScreen of the same size (0x18698c)
                                                                      or builds a new one from "LOADSCREEN.DDS" (ctor 0x18737c)
Client::HideLoadingScreen  0xc5ca8  -> Manager::Stop 0x1441b0 (only if client+0xc3c exists)
                                    -> LoadingScreen::Stop 0x182834 -> stop byte (0x7ea164), then join (0x7ea0f8)
```

`WaitForWorldReady` (`0xec3dc`) shows the screen near its start (call at `0xec4b8`) and **every exit of the function, the success
path included, ends in `Client::HideLoadingScreen`** (label `0xecd68`). On the success path it first writes
`ClientZoneTime.log` and `MarketingData.log`, which is exactly what the server sees. So on a normal run the stop *is* requested
about 3.4 s after the zone change, and still the second `LoadingScreenThread` keeps running. That leaves three explanations, none of
them checked at runtime yet:

1. `client+0xc3c` is null or is not the manager that owns the running thread, so `HideLoadingScreen` does nothing.
2. The screen is shown **again** after the hide (other callers of `0xc72f8`: `0x78e42c`, `0xf2ac0`, `0x151328`, and the modal wait
   below), so the thread in the log is a new one.
3. The stop byte is set but the thread never leaves its loop, or the main thread waits in the join (`0x7ea0f8`).

A second loading mechanism exists: the **modal wait** `Client::ShowLoadingScreenModal` (`0xc73b4`), called from the per-frame
function when `client+0x14bbd` is set. It shows the screen, pumps the network (object at `client+0xc44`) and waits until the
flag clears or `client+0x14bc0` (60 in the setters) runs out. The flag is set by `0xa8a50`, `0xa97ec` and `0xaacb4` (each right
after sending a request) and cleared by `0xaf584`, the handler of the housing-entry result (`HousingServerNotFound`,
`HousingPermissionDenied`, ...), and by `0xc98ac` / `0xf18d4`. **Ruled out** by the shutdown samples below: its loop sleeps 10 ms
per round (`0x1e040`), so the main thread would be inside `sys_timer_usleep` at shutdown and would use little CPU; it is neither.

### Next experiment: end the splash thread by force

`tools/rpcs3_diag_patch.py --patch loading-thread` (or `tools/run_test.py --stop-loading-thread`) replaces the stop check inside the
thread loop (`0xb641a8`, `bl 0x7ea068`) with `li r3,1`, so every `LoadingScreenThread` ends at once. It separates two cases:

| After the patch | Meaning |
|---|---|
| the world appears (or a black/empty world with the player) | the server side is complete enough; only the splash thread's stop path fails (explanations 1-3) |
| still no world | the splash was hiding something else; the HUD overlay (`SetLoadingWorldAssets`) or the world itself is not ready |

## Where the main thread is (RPCS3 shutdown listing, two real runs)

When RPCS3 is closed it lists every thread that is inside a blocking call (`'sys_...' aborted`) and prints the program counter
of the others. Two runs that reached `ClientLog`/`FinishedLoading` (`fr_test_20261025_104133` and `fr_manual`) say the same:

| Thread | State at shutdown |
|---|---|
| `main_thread` | **not** in any system call; PC `0x727768` in one run (inside the opcode loop `0x72714c`) and `0x007200fc` in the other (inside `lua_getfield`, `0x71ff9c`, the C API that C++ code uses to look up a script function); 114.9 s and 93.5 s of CPU time in a run of 138 s and 119 s |
| `LoadingScreenThread` (the second one) | in `sys_timer_usleep` at `0x1e06c`, i.e. the splash loop |
| `UdpLibrary` | in `sys_timer_usleep` at `0x3d600c` |
| `Deep Engine Renderer`, fios threads, SPURS | waiting on semaphores / condition variables / event queues (idle) |

Both PCs lie in the Lua 5.1 runtime (`0x72714c` is the opcode switch with a computed jump, called from `luaD_call`-like `0x714324` and `0x72a2d4`;
`0x71ff9c` is `lua_getfield`). So the main thread is **busy in script calls**, not asleep and not waiting for the server. Two samples cannot
tell a normal per-frame UI update from a loop that never ends; the experiment below separates them. Over the same period the number of
`sys_rsx_context_attribute` calls grows by about 37 per second, which is the splash thread's 40 Hz loop (`usleep(25 ms)`), and
`sys_net_bnet_recvfrom` grows by about 180 per second; that is the `UdpLibrary` thread polling its socket (it sleeps between reads), so
the earlier remark that the main loop "keeps running (about 125 network reads a second)" was wrong: it says nothing about the main thread.

With the splash thread removed (`--stop-loading-thread`, run of 2026-10-25 15:42) the server side was identical, and the screen
**froze on the player's character** after *Start Game*. So nobody but the splash thread presents frames once the zone change begins, and
the splash was only hiding a main thread that does not get back to its render loop.

What `WaitForWorldReady` does after its last wait loop (`0xec3dc`, re-read with the repaired disassembly, see below):

```
"waiting complete" -> ClientZoneTime.log -> MarketingData.log (queued as ClientLog packets, plus FinishedLoading)
  -> FUN_007ced38(holder, 1)                       a call into the zone holder
  -> FUN_000e43ec(client, flag)    0xed91c          Lua call HUD:SetIsInStartingZone(flag)   <- runs UI script code
  -> FUN_00160dcc (only when client[0x517f] == 3)  walks a linked list
  -> release the temporary strings, return 1
  -> 0xecd68: Client::HideLoadingScreen            <- the only thing that ends the splash thread
```

So the two ClientLog lines prove that the wait loops ended, **not** that the function returned. The ordering fits what the server sees:
the request `141/1` follows the logs 40 ms later (sent by UI script code), and after that the client sends nothing. If the main thread
never reaches `0xecd68`, `Hide` never runs, the splash thread is never stopped or joined (the log shows exactly one
`sys_ppu_thread_join`), and the render loop never resumes.

`HUD:SetIsInStartingZone` itself (script function `[40,18]`, read with `tools/luadis.py`) has no loop: it sets two flags, may call
`SetShowNormalState` (`[40,19]`: `hideMain`, `showMain`, `GameDock:SetMountButtonEnabledState`, `OnShowWelcomeScreen(...)`) and starts a client
timer. The bundle has 3012 functions; 56 contain a `while`/`repeat` loop (per-frame candidates: the chat-bubble list in `Bub.lua`, the
HUD indicator update `[40,14]`, the GAQ item list `[57,13]`, ...), none identified yet as the one that spins.

### Next experiment: does the main thread ever reach the exit of WaitForWorldReady?

`tools/run_test.py --fault-at 0xecd68` (or `tools/rpcs3_diag_patch.py --patch fault --address 0xecd68`) replaces the first instruction at
the common exit with `lwz r0,0(0)`. The thread that reaches it dies, and RPCS3 writes its registers and call stack to `RPCS3.log`
(`rpcs3_log_filtered.txt` keeps those lines).

| After the patch | Meaning |
|---|---|
| a fatal-error entry for `main_thread` at `0x000ecd68` with a call stack | the exit is reached; `Hide` is then called and the splash thread must be stopped by something else failing (explanations 1 and 3 above); the call stack shows the caller |
| no such entry in a minute | the main thread is stuck between the ClientLog lines and the exit (Lua call, `0x7ced38`, or `0x160dcc`); repeat with `--fault-at 0xed920` (right after the Lua call) to split the tail |

### Probes, in the order worth running

All use `tools/run_test.py --fault-at ADDR` (same command as above, only the address changes). Read the result in `rpcs3_log_filtered.txt`:
search for the address.

| Run | Address | Question | If it fires |
|---|---|---|---|
| 1 | `0xe1754` | Does the client dispatch opcode 93 (`docs/protocol/gateway.md`, *Inner packet 93*)? The server log already shows it sent and acknowledged, so this should fire a few seconds after the world is entered; it also shows whether the fault patch works on the user's RPCS3 | the thread name and call stack show which thread dispatches packets and from where |
| 2 | `0x1e93e8` | Does a later frame consume the welcome flag (the per-frame update runs after the packet)? | the stack shows whether this happens inside `WaitForWorldReady` or after it |
| 3 | `0xecd68` | Does `WaitForWorldReady` reach its exit (`Hide`)? | see the table above |

If 1 fires and 2 does not, the packet is dispatched but the per-frame update never gets to the flag, which points at the main thread being stuck
before that (run 3 splits it). If 2 fires, the main loop is alive after the packet and `Hide` not running becomes the question.

### Results (2026-11-06, `--sanctuary`)

| Run | Patch | Result |
|---|---|---|
| `fr_test_20261106_163749` | none | Same hang as every earlier run: the second `LoadingScreenThread` starts at the zone change, the game reads files for 3.4 s (the client's `ClientZoneTime.log` says 3345 ms), then opens nothing, starts no thread and never joins the splash thread. The game ignores RPCS3's exit request. |
| `fr_test_20261106_164824` | fault at `0xecd68` (RPCS3 logged `PAT: Applied ... fault at 0x000ecd68`) | **No fault in two minutes**: `WaitForWorldReady` never reaches its common exit, so `HideLoadingScreen` never runs. At shutdown the main thread was at `0x71fda8`, in the Lua C API next to `lua_getfield` (`0x71ff9c`), with 97 s of CPU time in a 120 s run: the third sample in the Lua runtime. |

So the main thread is stuck, busy, between the ClientLog lines and `0xecd68`: in `FUN_007ced38(holder, 1)`, the Lua call
`HUD:SetIsInStartingZone` (`0xe43ec`) or the list walk `0x160dcc`. Next: `--fault-at 0xed920` (right after the Lua call). No fault patch
has been seen to fire on this RPCS3 yet; if `0xed920` stays silent too, a run at `0xe1754` (opcode 93, which the client receives every run) checks
that the method works.

| Run | Patch | Result |
|---|---|---|
| `fr_test_20261106_165400` | fault at `0xed920` (applied) | No fault: the main thread never returns from `HUD:SetIsInStartingZone`. Main thread sampled at `0x71fc18` (Lua). |
| `fr_test_20261106_165838` | fault at `0xe1754` (applied) | No fault. Not a test of the method: in the Sanctuary order opcode 93 arrives after 14 and 38/26, and the main thread hangs before it dispatches it. |
| `fr_test_20261106_170218` | fault at `0xec3dc` (applied) | **Fault**, `Access violation reading location 0x0` on `main_thread` with `LR = 0xf12d0` (RPCS3's recompiler reports the caller's position): the method works. |

## Cause of the stuck loading screen: `GroupHandler:GetNextJob` never finds a job with a rank

Paused in RPCS3's debugger at the stuck screen, the main thread's call stack was (innermost first):

| Return address | Function |
|---|---|
| `0xa332c8`, `0xa334c0` | `0xa33124`, the C library's string-to-number conversion (`strtod`: the exponent limits -1074 / 1075 / 538 / -537 in the float registers) |
| `0x70fd90`, `0x7385d8` | `lua_tonumber` and the function registered as Lua `tonumber` (`0x738350`, name table entry at `0xeb6e88`) |
| `0x71e428`, `0x7288e4` | `luaD_precall` and the one `luaV_execute` (`0x727150`): the spinning code is plain Lua calling `tonumber` |
| `0x71444c` ... `0x7185ac` | the Lua call machinery |
| `0x1f2a0c` | the game's script-call helper |
| `0xe44e4` | `0xe43ec`, which calls `HUD:SetIsInStartingZone` |
| `0xed920` | `WaitForWorldReady`, right after that call |
| `0xf12d0`, `0x10fc4`, `0x104d8`, `0x10250` | the zone-change routine, the main loop and start-up |

The scripts (`tools/undat.py`, `tools/luadis.py`) give the rest of the chain:

```
HUD:SetIsInStartingZone [40,18] -> HUD:SetShowNormalState [40,19] -> HUD:showMain [40,6] (pc 174)
  -> GroupHandler:SetJobSwitchOnOrOff(true) [46,38] -> GroupHandler:GetNextJob(0) [46,40]
```

`GetNextJob` walks the `DS_PROFILES` data source inside `while not done do for i = ... do ... end end`; the only way out is a row after the
active job (wrapping round) whose `tonumber(Rank)` is above 0. The server sent one profile with every field but the id at 0, so the rank was
0 and the loop never ends. The server now sends rank 1 for the starter job (`PROFILE_RANK` in `bootstrap/gateway_probe.py`,
`Ps3Packets.ProfileRank` in the C# port).

**Where the rank is.** The job list is a data source class (method table at `0xec9c0c`): its column names come from `0x264ea4` (Id, Name,
Description, Type, Rank, StarsAvailable, ...: the order of the script's `ProfileDsColumns`), its row count from `0x265020`, its rows are filled by
`0x2661a8` from the player's own profile list (`player+408`, one row per profile, so our one profile is one row), and its values come from
`0x2656c0`, which looks the row's id up in the player's profile table and jumps on the column (table at `0x265844`): **Rank returns
`profile+20`**, RankPercent `profile+24`, Id `profile+0`. The player-data reader stores entry offset `0x33` (blob `0x124`, read at `0xb09788`)
into `profile+20` and `0x37` into `+24`. So the rank is at entry offset **`0x33`**. A first attempt put the 1 at `0x37` by following
Sanctuary's field order (the PS3 entry lacks one of its unknown fields before the rank); that set RankPercent, and the run of 2026-11-06 17:26
still hung in `GetNextJob` (same call stack).

### Repaired disassembly (why some earlier "no caller" results were unreliable)

Ghidra's PowerPC language does not know the Cell-only vector loads `lvlx` / `lvrx`. It stops disassembling at the first one, so every
function that contains one is cut off there (the decompiler prints `halt_baddata`). The executable has 469 of them (463 `lvlx`, 6 `lvrx`),
among others in the per-frame function `fn_000ee7bc` (everything from `0xeed40` to `0xef09c` was missing). `tools/re/ghidra/FrSetup.java` now rewrites
them to the plain AltiVec `lvx` in the Ghidra copy (same registers; each gets an end-of-line comment with the original word; the file on
disk is unchanged) before the functions are created. All 59 rows of `addresses.csv` were verified again on the rebuilt project, and the call
graph of the loading screen (callers of `0x7ea164`, `0x182834`, `0x1441b0`, `0xc5ca8`, `0xc72f8`, `0x14420c`) is the same as before.

## Experiment: the Sanctuary flow one for one

EdenFPS (Clover community, 3 October 2026) showed the PS3 client in the world on RPCS3 with a modified Sanctuary server. Eden named
only one change, an exact `SendSelfToClient` for the build, and the character was drawn without clothes, so empty item data does not
hold the world back. The difference that remains between that setup and this server is everything else Sanctuary sends around the zone
change. `FR_SANCTUARY=1` (or `tools/run_test.py --sanctuary`) sends exactly that, built in [`bootstrap/sanctuary_flow.py`](../../bootstrap/sanctuary_flow.py)
from Sanctuary's own serializers:

| When | Sanctuary sends (and now so does this server) | What this server sent before |
|---|---|---|
| After `LoginReply` | EncounterOverworldCombat (41/132), InitializationParameters (`live`), ZoneDetails, ClientGameSettings, ItemDefinitions (empty), SendSelfToClient | ZoneDetails, SendSelfToClient (the rest only with `FR_EXTRA=1`) |
| `ClientIsReady` | quick chat, 20 stats, hit points, mana, wield type, can-create-guild, item classes / categories / profile reference data, customizations, membership (189), composite effects on, **then** 14 and 38/26, friend and ignore lists, points of interest, adventurer's journal (209), coin store and marketplace (165, 66), announcement (193), welcome screen (93, first login: seconds since last login `-1`), objective target off | GameTimeSync, 93 (empty), 14, 38/26 |
| `ClientFinishedLoading` | client areas (4173, plus the 13 Halloween areas between 20 September and 11 November), POI change and chat area, weapon flair override, empty toolbar for profile 1 | `AddPc` of a second test character one second later |
| 102/1, 141/1, 52 | one test lobby game; **no reply** to 141/1; game clock `8 x (now - 1221361670)` | empty lobby list; a guessed 141/2 reply; Unix time |

Kept from this server: the PS3 layouts of ZoneDetails and SendSelfToClient, and DoneSendingPreloadCharacters with its bool set (the
PS3 client counts it only then; Sanctuary sends 0). Lists that Sanctuary fills from its resource files (quick chats, item classes,
profiles, points of interest, coin store) are sent empty: their record layouts are not checked against build 1.77 yet. Three of
Sanctuary's opcodes (189, 193, 209) lie outside the 12-180 range the 1.77 dispatcher handles, so the client most likely ignores them;
the server logs them. `tools/check_sanctuary_flow.py` checks the order and every layout without a PS3.

| After the run | Meaning |
|---|---|
| the world appears | one of Sanctuary's packets is what the client waits for; `FR_SKIP` narrows down which |
| still stuck, same place | the packet flow is not the cause; the fault probes above locate the main thread |
| the client crashes or disconnects earlier | one packet has a different layout in 1.77; leave packets out with `FR_SKIP=Name,Name` (names in `bootstrap.log`, `sanctuary:` lines) until it stops |

## Not yet verified

* The height of the ground at the chosen spawn (the server uses a fixed `y` of 400 and lets the player fall).
* What holds the overlay now (see *Test results so far*): a gate byte, a terrain object that never becomes ready, tile
  objects that are not local, or the camera position.
* What the client logs at runtime — the client's log sinks are a ring buffer / file / network, not RPCS3's TTY.
* Which UI script function (or C++ call) the main thread is busy in after the ClientLog lines (see *Where the main thread is*).
* Who sends the `141/1` request 40 ms after the ClientLog lines. Two senders of a 141 packet with sub-opcode 1 exist: the script-bound
  function `UpdateAllQueueStats` (`0x202f34`, registered in the table at `0xec4bb4`; its Lua callers `[73,11]` and `[89,0]` are matchmaking
  screens that the player opens) and a handler that raises the script event `MatchmakingServerUp` (`0x252db0`, called from `0x13d260` next to
  the 141 reply parser). Which of them fires here is not known; either would show that script or event code ran after the logs.
* The player data (`SendSelfToClient`) is not a suspect for the stall: the reader (`0xb1e460`) and its finisher (`0x59de8`) contain no wait and
  no loop that depends on the zero counts we send (see `docs/protocol/gateway.md`).

## After the loading screen: the world, and a render crash on the area sky

With the rank fixed (run `fr_test_20261106_173318`) the loading screen drops and the client plays: the player falls from the spawn
height (y 400) and lands at about y 36 on tile (-4, -4), the client sends movement (opcode 125, about 10 a second) and camera updates
(126), asks to join the chat channels `WorldArea`, `WorldTrade`, `WorldLfg` and `WorldMembersOnly` (opcode 26), requests store data (66)
and logs `ClientStartTime.log` and `ClientSystemInfo.log`.

Looking at the sky (or moving until the area sky loads) then breaks the game's render thread. The client opens
`resources/sky/sky_wilds24.xml` and, within seconds, one of these happens:

| Run | RPCS3 settings | Server | Result |
|---|---|---|---|
| `173318` | defaults (FIFO `Atomic`) | AddPc helper sent | `Deep Engine Renderer`: read of `0xc0` in `0x784518` (render command type 6: copy a 16-byte shader constant from the renderer's parameter table, slot 57 holds `0xc0`) |
| 17:44 (`crash_logs`) | defaults | no AddPc | RSX `Dead FIFO commands queue state` |
| `175319` | FIFO `Ordered & Atomic` | | hang: the renderer waits on a semaphore for the GPU, the main thread waits on the renderer |
| `180138` | FIFO `Ordered & Atomic` | no GameTimeSync (`FR_TIME=0`) | read of `0xc0` in `0x784518`, command type 2 (bind the material's 16 texture slots; a slot holds `0xc0`) |
| `180505` | plus `Accurate RSX reservation access` | | same as `180138` |

Ruled out: the helper character, the game clock, missing or outdated sky assets (all 13 files the sky names are in the packs with the
manifest's crc; only 1 of 54,146 local assets differs from the manifest), `Ordered & Atomic` and accurate RSX reservations. A driver
wake-up delay of 200 froze the game at character creation. The bad pointer is `0xc0` every time, which looks like the address of a field
at offset `0xc0` in an object that does not exist, reached through both a shader constant and a texture slot. `ZoneDetails` names no sky
(as Sanctuary does for FabledRealms, but that is the 2014 PC client); `FR_SKY` sets one to test whether the 2011 PS3 build needs it.
The render chain is thread entry `0x7ea570` -> `0xa60fe4` -> `0x35840` -> `0x7519c0` -> scene walk `0xcc73b0` (recursive) -> `0xcc6e50`
-> `0x784518`, whose command list is at renderer `+3324` (count at `+3320`) and parameter table at `+0x2b8`.

## Dressing the character: the creator's clothes

The creator sends item definition ids and tints (`ItemChest=28216`, `ItemChestTint=234`, `ItemLegs=392`, `ItemFeet=17397`, ...).
Sanctuary turns them into items in the player data and equips them in the profile; the PC client then looks each item's definition
up and draws it. On the PS3 that needs two things this server does not have yet: item definitions (PlayerUpdate sub-opcode 33 on
1.77: an i32-length payload whose first part, read by `0xac53b8`, is Sanctuary's `BaseItemDefinition` up to `VipRankRequired`; then
`0xa90d10`, `0xac3c24`, `0xac3788`, `0x147108` and the cache insert `0x148420`) and the player data's item list and equipment slots.

The shorter way is the packet Sanctuary sends when a profile changes, EquippedItemsChange (PlayerUpdate sub-opcode 7 on the PC and
on 1.77). Its handler (`0x112afc`) reads u16 35, u16 7, u64 guid, an attachment list (`0xb03e90`), one more int, and acts only when
that is the whole packet: `0x11cc34` finds the character by guid (`0x1bd598`) and puts the attachments on it (`0x1b0264`). Each
attachment is three strings (model, texture alias, tint alias, at node offsets 0, 84 and 168) and three ints (tint, composite effect,
slot, at 252, 256 and 260), Sanctuary's `CharacterAttachmentData`. Run through `tools/ppc_trace.py` with the list object the handler
builds (vtable `0xebeeb8`; the reader's byte at +16 is an error flag, 0 = fine), the client's reader consumed all 289 bytes of the
three-piece list for the character above and built three nodes with the right strings and ints.

First test in RPCS3 (2026-11-06): the clothes went on, but the character first played the job change. That is in the handler:
before attaching, `0x11cc58`..`0x11cc8c` plays effect 4005 on the character, because on the PC this packet is what a job change
sends (Sanctuary's `CommandPacketSetProfileHandler`).

Second try: EquipItemChange (PlayerUpdate sub-opcode 6), one per piece. Its handler (`0x112c24` -> `0x11daec`) plays no effect,
but in RPCS3 nothing went on at all. In Sanctuary that packet only goes to other players (`SendTunneledToVisible` without
`sendToSelf`); your own client gets ClientUpdate (38) sub-opcode 5, EquipItem (`InventoryPacketEquipByGuidHandler`).

So the server now sends EquipItem, one per piece. The PS3 handler (`0xfa600`) reads u16 38, u16 5, i32 item id, one attachment (the
same three strings and three ints), profile id and one unused int; the PC packet's trailing `Equip` bool is not there. It acts only
on the whole packet: `0xfec54` calls `0x5aab0` (player, slot, attachment, profile id) and then looks the profile up and redraws when
it is the active one. Item ids are 1, 2, 3... as Sanctuary numbers a new character's items; the profile id is the starter job's.

The pieces come from `bootstrap/data/clothing_items.tsv.gz` (the tint is the creator's, or the definition's icon tint when that is
0, as in Sanctuary's `Player.GetAttachment`), sent for guid 1 a second after `ClientFinishedLoading` in both flows. `FR_EQUIP=7`
goes back to the one EquippedItemsChange; `FR_EQUIP=0` sends nothing.
