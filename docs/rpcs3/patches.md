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
first instruction: the code in the zero padding at `0xe86e40`-`0xe86f14` (nothing refers to it) writes the message's format text, up
to 400 characters and unformatted (a message that is only `%s`, as the scripts' errors are, gets its text: the first argument, read
when it points into main memory or the stack; version 1.1), with `sys_tty_write` (syscall 403, channel 0), then runs the replaced instruction and goes on. RPCS3
keeps the lines in `TTY.log`, which the Server Manager's test report copies as `game_log_tail.txt`.

## Marketplace without PSN

**Symptom:** the Marketplace and its coin store tab open but show nothing. The game's log has 58 times *ERROR in GetProductInfoList:
sceNpCommerce2InitGetProductInfoListResult failed* (CommerceProcessorPsn) and never *MarketPlace System Support Data Complete*.

**Cause:** the PS3 Marketplace gets its Station Cash product data from PSN (`sceNpCommerce2`), which RPCS3 cannot reach. The commerce
processor (`0x42ae60`) reports to its listener when it is done, `ready(true)` (`0x42b078`, `0x42b10c`), or when fetching failed,
`ready(false)` (`0x42aed8`). The listener is the Marketplace's *OnMarketplaceReady* (`0x8c1dc`, through the thunk `0xaa7348`), which on
true checks the portal categories against the server's store (`0x96088`) and tells the screen it may show the store.

**Fix:** the failure path reports `ready(true)` too (`li r4, 0` → `li r4, 1` at `0x42aed8`). Not checked in RPCS3 yet.
