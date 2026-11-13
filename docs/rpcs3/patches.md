# RPCS3 game fixes

Two small patches to the game's code make the character creator's custom names work in RPCS3. They are in
[`src/FreeRealms.Bootstrap/Rpcs3/FreeRealms_PS3_fixes.yml`](../../src/FreeRealms.Bootstrap/Rpcs3/FreeRealms_PS3_fixes.yml), for
the decrypted 1.77 `game.elf` (RPCS3 hash `PPU-5425f6d47ba410afbcbc7ce77f87f2ffcde68798`).

## Adding them

**With the Server Manager:** nothing to do. Before each test run (setting *Add the game fixes to RPCS3 before a test run*, on by
default) the manager adds any fix that is missing to RPCS3's `patches\imported_patch.yml` and turns it on in
`config\patch_config.yml`, keeping a `.bak` copy of each file it changes. A fix already listed is left alone, so one you turned off
stays off. RPCS3 must be closed, because it writes those files itself.

**By hand:** open RPCS3 → *Manage → Game Patches*, drag the `.yml` file into the window, tick both fixes and press *Save*.

RPCS3's log shows them at boot: `PAT: Applied patch (... description='Free Realms PS3 keyboard close fix' ...)`. The first boot
after a change takes longer while RPCS3 recompiles the game.

## Keyboard close fix

**Symptom:** in the creator, choose *Custom Name*, type a name and confirm: the screen waits forever and no name check reaches the
server.

**Cause:** two pieces of the game answer the system's *keyboard finished* event (`CELL_SYSUTIL_OSKDIALOG_FINISHED`, 0x503):

| Handler | What it does |
| :--- | :--- |
| `0x1ee0a0` (general; chat) | Calls `cellOskDialogUnloadAsync` first, *then* checks its own flag (`+4192` of the UI object from `0x886a60`) to see whether it opened the keyboard |
| `0x78ae60` (character creator) | Calls `cellOskDialogUnloadAsync` to read the text; on success splits it at the first space into first and last name (`0x1080e70+200` / `+236`) and sets the *done* flags (`+32`, `+34`) its state machine waits for |

The general handler runs first and closes the keyboard although it did not open it. When the creator then asks for the text, RPCS3
answers `CELL_MSGDIALOG_ERROR_DIALOG_NOT_OPENED` (0x8002b302), the creator logs the error and returns without setting its flags.

**Fix:** the general handler checks its flag before closing the keyboard (`0x1ee134`–`0x1ee170` rearranged, the cancel event at
`0x1ee1e0` goes the same way, and the branch at `0x1ee230` follows the move). When chat opened the keyboard it behaves as before.

## Renderer null state guard

**Symptom:** the emulator stops with `VM: Access violation reading location 0x108` in the *Deep Engine Renderer* thread at
`0x7845a4`. It happened a few seconds after confirming a custom name; the same spot crashed in the world before the sky fix.

**Cause:** `0x784518` applies a render state block. It compares the wanted state (`+336`) with the one last applied (`+680`) and,
when they differ, binds the wanted one through `0x91d2ac` with fields `+256` and `+264` of it. Between the comparison and that call
(after `0x91f8d0`, which can wait for the command buffer) the slot can become empty, and the code reads `+264` of nothing.

**Fix:** both paths that load the wanted state (`0x7845b8` and `0x785170`) jump to a check in the unused padding at
`0xea0a5c`–`0xea0a7c` (before the constructor list at `0xea0a80`): an empty state skips the bind and goes on at `0x7845e0`, as if
it had been applied. The padding `0xea0a20`–`0xea0a58` is left for the *select exceptfds* patch.

## Game log to TTY

**What it does:** the game logs its own messages (for example *Failed to serialize InGamePurchaseStoreItemsResponsePacket packet.*)
through one routine, `0x163a8` (level, logger, format, arguments), which drops them unless a debug level is set. The fix hooks its
first instruction and writes each message with `sys_tty_write` (syscall 403, channel 0), then runs the replaced instruction and goes
on. RPCS3 keeps the lines in `TTY.log`, which the Server Manager's test report copies as `game_log_tail.txt`.

- **Version 1.2:** the message is formatted with the game's own `vsnprintf` (`0xa30ff8`, the one its `snprintf` at `0xa4cfd0` calls),
  so the values are in it (*Added/Updated new store successfully - Store: 1, Count: 3461*), up to 999 characters.
- It also writes the name of each script event the Marketplace and the coin store raise, as *Script event Handler.Event*: the hooks
  at `0x87c14` (a `HandlerInGamePurchase` event, such as *OnMarketplaceReady*) and `0x133038` (a handler's event, such as
  *HandlerCoinStore.OnInitializeSearchEngine*).
- **Version 1.3** also wrote the game's debug output (`0xc7134`); **1.4** leaves it out again to make room for the character
  wheel.
- The code is in the zero padding at `0xe86e40`-`0xe87014` (nothing refers to it).
- Versions 1.0 and 1.1 wrote the format text unformatted.

## Marketplace without PSN

**Symptom:** the Marketplace and its coin store tab open but show nothing. The game's log has 58 times *ERROR in GetProductInfoList:
sceNpCommerce2InitGetProductInfoListResult failed* (CommerceProcessorPsn) and never *MarketPlace System Support Data Complete*.

**Cause:** the PS3 Marketplace gets its Station Cash product data from PSN (`sceNpCommerce2`), which RPCS3 cannot reach. The commerce
processor (`0x42ae60`) reports to its listener when it is done, `ready(true)` (`0x42b078`, `0x42b10c`), or when fetching failed,
`ready(false)` (`0x42aed8`). The listener is the Marketplace's *OnMarketplaceReady* (`0x8c1dc`, through the thunk `0xaa7348`), which on
true checks the portal categories against the server's store (`0x96088`) and tells the screen it may show the store.

**Fix:** the failure path reports `ready(true)` too (`li r4, 0` → `li r4, 1` at `0x42aed8`). Not checked in RPCS3 yet.

## Marketplace store list (test)

**Symptom:** the Station Cash shop opens with only *All Items* and *Hot New Items*, and no items, although the game took the store
(*Added/Updated new store successfully - Store: 1, Count: 3461*), the Marketplace was ready and its search setup finished.

**What the game does:** the shop's list is the *BaseClient.InGamePurchase.StoreItems* datasource (the screen at
manager + `0x10000` + 22964, its datasource at screen + 4). It builds its rows (`0x23d778`) from a list of store item ids it keeps
(datasource + 100), filtered by the chosen category (`0x239f3c`). Only the item preview, search results, mannequin and minigame ad
lists are filled (`0x2393b8`); nothing found so far fills it for the main store.

**Test (1.0):** when a store's items are gone through (`0x95d70`, when the store arrives and when the Marketplace is ready), each item
not already in the list was added with `0x2393b8`. It did not make the shop show items (13 November). **1.1 retires it:** it only
puts back the game's own `lwz r11, 12(r28)` at `0x95ef8`, so an RPCS3 that had the test keeps nothing of it; its code area is the
character wheel's now.

## Character wheel

**What it does:** with saved characters, character select shows one character with *Start Game* (X) and *Delete* (Triangle). With
the patch, **D-pad right** and **Triangle** turn to the next saved character and **D-pad left** to the previous one; after the last
character comes **New character**, where X opens the creator instead of logging in. Characters are deleted in the Server
Manager's Characters page; the in-game delete is gone.

**How the game does select (read from `game.elf`):**

| What | Where |
| :--- | :--- |
| The character list from `account/info` | `this+4188`: head `+4`, tail `+8`, count `+12`; each record (408 bytes) links next `+408`, previous `+412` (append at `0xd98828`) |
| Login state 5: build the preview from the head record | `0x78ea48`: `0x78bc88(this, 0, 0, 0, 1, 0)`, kept in `this+4548` and `client+712`; then state 8 → 9 (place it, show the popup) → 10 (`S##PS3_Start_w/Character`) → 11 |
| State 11: wait for a button | `0x791b68`: pad counters at `client+0x10000+10488` (X: start, to state 39 at `0x792768`) and `+10496` (Triangle: delete warning, state 12) |
| D-pad left / right | `+10552` / `+10556`, as the creator's turntable (state 15, `0x7913d4`) reads them |
| Start Game logs in as | the head record's `Guid` (state 39, `0x78ebb8`, `ld r27, 0(head)`) |
| Remove a preview character | `0x1bf598(client->704, actor)`, as the creator does at `0x791940` |

**The patch:** state 11's X branch goes to a check (`0x791f60`): a head record with guid `0x7FFF0000` hides the popup, goes to
state 13 and moves the camera to the creator (as the game's own select → creator step does, state 9 at `0x793704`); any other guid
starts the game as before. The Triangle branch turns to the next character; the fall-through (`0x791b94`) checks D-pad right and
left: right moves the head record to the tail, left the tail to the head; then the preview is removed and the state goes back to 5,
which builds the new head's preview and presents it. The code is in the dead part of the old delete-warning path
(`0x791b94`-`0x791c10`), the old delete prompt (state 12, `0x791f50`-`0x791ff0`, which nothing sets any more) and the zero padding at
`0xe87014`-`0xe87128`.

The server adds the **New character** stop after the saved characters while the account has room (fewer than 10): guid
`0x7FFF0000` (2147418112), name "New character", and a whole look (the creator's first choices and starter outfit), because
select builds a preview of whatever it shows. Without the patch only the first character is ever shown, so it is never reached.

**Why not guid 0 and no look (version 1.0):** turning to the stop crashed with `VM: Access violation writing location 0x48c` at
`0x78bf80`. The preview builder (`0x78bc88`) makes an actor with `0x1c1120`, keyed by the record's guid (`ld r0, 0(record)`), and
writes field `+1164` of the result without checking it. `0x1c1120` returns null when an actor with that id already exists
(`0x1c1384`) or the look cannot be set up (`0x1b05c4`); a stop with guid 0 and no head, hair or skin hit one of the two.

**Version 1.2 (13 November):** version 1.1 crashed when turning the wheel (`fr_test_20261113_104830`: access violation writing `0x48c`
at `0x78bf80`), for two reasons, both fixed:
- **Shared code was overwritten.** The old delete path is only dead up to `0x791c14`: other states jump to `0x791c18`, `0x791c88`
  and `0x791ca8` (from `0x7939e4`, `0x7937b8`, `0x7935e4` and `0x79360c`), which 1.1 had filled with the wheel. The wheel now keeps
  to `0x791b94`-`0x791bf8` (the left/right check and *next*) and the zero padding at `0xe87014`-`0xe87104` (*previous*, the rebuild
  and X), and leaves the game's code from `0x791c18` on as it is.
- **The preview actor's id was still in use.** The preview of the shown character is an actor whose id is the character's guid.
  Removing the old preview (`0x1bf598`) is put off while it is still loading, and the game makes no actor whose id is in use
  (`0x1c1120` returns null), so turning back to a character crashed. Each turn now adds one to the high word of the new head's guid
  (a new actor id every time); Start Game clears it again, and the server reads only the low 32 bits of a guid.

**Version 1.3 (13 November):** 1.2 still crashed the same way, about four seconds after the select screen came up
(`fr_test_20261113_105938`, `fr_test_20261113_110739`, with two saved characters and the stop). Two changes:
- **One press, one turn.** D-pad left and right (`+10552`/`+10556`) are held states, not presses: the creator's turntable keeps the
  last values (`this+4516`/`+4520`) and acts only on a change (`0x7917f0`). 1.2 read them as presses, so while a direction was held
  every return to state 11 turned and rebuilt again. 1.3 keeps the last values in the same fields and turns only when one changes.
- **The preview's actor is checked and logged.** The builder's `mr r26, r3` after `0x1c1120` (`0x78bf48`) calls a check that writes
  `wheel <guid> made <actor> used <actor>` to the game log (when the game log to TTY fix is on). `0x1b05c4` can only fail when its model
  pool (`0x865010`) returns nothing, and that pool grows instead, so a null actor means an actor with the id is already there: the
  check then looks it up in the manager's table (`manager+16`, 128 buckets by `hi ^ lo`, chained at `+88`, id at `+112`) and uses it,
  instead of writing to null. `made 0` in the log shows when that happened.

Test reports now also have `rpcs3_crash.txt`: RPCS3's dump of the crashed thread (registers, call stack), which the filtered log drops.

