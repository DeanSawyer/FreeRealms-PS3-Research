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

The first row is the leading explanation for the current stuck screen (the server sent `FabledRealms`, a name taken
from the PC game). It is **not yet confirmed** on the user's install: run `tools/zone_report.py` against the real
`USRDIR` (below).

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
overrides it. The height of the ground there is unknown; the default `y` is 400 (`FR_SPAWN_Y`).

## How the server decides

`gateway_probe.choose_zone_from_install()` runs `assetlib.pick_zone()` over the user's packs and manifest and sends the
first zone that has `.gzne`, `.cob` and `.odd` present and manifest-listed (preferring `FabledRealms`). Override with
`FR_ZONE` / `FR_ZONE_TYPE`.

## Test plan

1. `python tools\zone_report.py "<USRDIR>"` → attach `zone_report.txt` (which zones exist; is the current one among them).
2. `python tools\gzne_info.py "<USRDIR>" <zone>` for the chosen zone → attach `gzne_info.txt` (cell box, cell size).
3. Run the bootstrap; the log line `ZoneDetails: zone=… type=2` shows what was sent. Report whether the overlay drops.
4. If the zone exists and the overlay still holds: the next suspects are tile streaming and bubble readiness
   (`0x7da88c`, `0x7dafb0`, `0x7dad04`, `0x7e66c8`) — they need runtime data, not more static reading.

## Not yet verified

* That `FabledRealms` is absent from the PS3 content (static reasoning only).
* The correct spawn position for a PS3 zone (the server still uses the PC value).
* What the client logs at runtime — the client's log sinks are a ring buffer / file / network, not RPCS3's TTY.
