# RPCS3 game fixes

Small patches to the game's code: two make the character creator's custom names work in RPCS3, and one turns character select
into a wheel of your saved characters. They are in
[`src/FreeRealms.Bootstrap/Rpcs3/FreeRealms_PS3_fixes.yml`](../../src/FreeRealms.Bootstrap/Rpcs3/FreeRealms_PS3_fixes.yml), for
the decrypted 1.77 `game.elf` (RPCS3 hash `PPU-5425f6d47ba410afbcbc7ce77f87f2ffcde68798`).

## Adding them

**With the Server Manager:** nothing to do. Before each test run (setting *Add the game fixes to RPCS3 before a test run*, on by
default) the manager adds any fix that is missing to RPCS3's `patches\imported_patch.yml` and turns it on in
`config\patch_config.yml`, keeping a `.bak` copy of each file it changes. A fix already listed keeps its on/off setting, so one you
turned off stays off; if its code is older than the one shipped, the code is replaced (the log says *updated*). RPCS3 must be
closed, because it writes those files itself.

**By hand:** open RPCS3 → *Manage → Game Patches*, drag the `.yml` file into the window, tick the patches and press *Save*.

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

**The patch:** state 11's X branch goes to a check (`0x791c60`): a head record with guid `0x7FFF0000` hides the popup, goes to
state 13 and moves the camera to the creator (as the game's own select → creator step does, state 9 at `0x793704`); any other guid
starts the game as before. The Triangle branch and the
fall-through check D-pad right and left (`0x791b94`): right or Triangle moves the head record to the tail, left the tail to the
head; then the preview is removed and the state goes back to 5, which builds the new head's preview and presents it. The code
uses the space of the old delete-warning path (`0x791b94`-`0x791cc8`, which stops short of state 10's handler at `0x791ce4`); the
rest of that path, and the blocks it jumps out to (`0x7935c0`, `0x793600`, `0x793758`, `0x7939e0`), are no longer reached.

The server adds the **New character** stop after the saved characters while the account has room (fewer than 10): guid
`0x7FFF0000` (2147418112), name "New character", and a whole look (the creator's first choices and starter outfit), because
select builds a preview of whatever it shows. Without the patch only the first character is ever shown, so it is never reached.

**Why not guid 0 and no look (version 1.0):** turning to the stop crashed with `VM: Access violation writing location 0x48c` at
`0x78bf80`. The preview builder (`0x78bc88`) makes an actor with `0x1c1120`, keyed by the record's guid (`ld r0, 0(record)`), and
writes field `+1164` of the result without checking it. `0x1c1120` returns null when an actor with that id already exists
(`0x1c1384`) or the look cannot be set up (`0x1b05c4`); a stop with guid 0 and no head, hair or skin hit one of the two.
