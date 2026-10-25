#!/usr/bin/env python3
"""Add or remove the "overlay check forced loaded" diagnostic patch in a portable RPCS3 folder.

The loading overlay of the PS3 client stays up after FinishedLoading (docs/re/zone-loading.md). Its per-frame check
(fn_000ee7bc) asks Holder::IsLoaded (0x897db4) whether the terrain around the camera is loaded. This patch replaces the two
calls to IsLoaded *inside that check* (0xef660 and 0xf0504) with `li r3,1`, so only the overlay logic sees "loaded". (A first
version patched IsLoaded itself; that changes three other callers as well and stopped the character creator from appearing.)
It separates the two remaining explanations:

  * the overlay drops  -> IsLoaded was what stayed false (terrain around the camera never became ready);
  * the overlay stays  -> one of the four gate objects is not idle, so the check never runs.

    python tools/rpcs3_diag_patch.py --rpcs3 "C:\\Path\\To\\RPCS3_TEST"            # add and enable
    python tools/rpcs3_diag_patch.py --rpcs3 "C:\\Path\\To\\RPCS3_TEST" --remove   # take it out again

A third kind, `--patch fault --address 0x...`, replaces one instruction with `lwz r0,0(0)` (a read from address 0), so the thread that
executes it dies at once and RPCS3 writes its registers and call stack to the log. It answers one question per run: is this
address ever reached? (docs/re/zone-loading.md, "Where the main thread is")

A second patch, `--patch loading-thread`, makes the native LoadingScreenThread (the LOADSCREEN.DDS splash that WaitForWorldReady
shows and hides) end at once: the call to its stop check inside the thread loop (0xb641a8, `bl 0x7ea068`) becomes `li r3,1`.
It shows what is behind the splash: if the world appears, the splash is the only thing left (docs/re/zone-loading.md).

It edits `patches/patch.yml` (the patch) and `config/patch_config.yml` (the enable flag) as text, inserting the entry under the
game's existing `PPU-<hash>:` key, and keeps a one-time backup of each file next to it (`*.before-isloaded`). Running it twice
changes nothing. The hash is the one RPCS3 prints as "PPU executable hash" for the decrypted build 1.77 `game.elf`.
Standard library only.
"""
import argparse
import re
import sys
from pathlib import Path

PPU_HASH = "PPU-5425f6d47ba410afbcbc7ce77f87f2ffcde68798"
NAME = "Free Realms overlay check forced loaded"
OLD_NAME = "Free Realms IsLoaded forced true"   # the first, too broad version; removed when found

PATCH_ENTRY = f'''  "{NAME}":
    Games:
      All:
        All: [ All ]
    Author: "FreeRealms-PS3-Research"
    Patch Version: 1.0
    Notes: "Diagnostic: the two IsLoaded calls of the loading-overlay check (0xef660, 0xf0504) answer true, to see whether the overlay drops."
    Patch:
      - [ be32, 0x000ef660, 0x38600001 ]
      - [ be32, 0x000f0504, 0x38600001 ]
'''

CONFIG_ENTRY = f'''  {NAME}:
    All:
      All:
        All:
          Enabled: true
'''

OLD_PATCH_ENTRY = '''  "Free Realms IsLoaded forced true":
    Games:
      All:
        All: [ All ]
    Author: "FreeRealms-PS3-Research"
    Patch Version: 1.0
    Notes: "Diagnostic: Holder::IsLoaded (0x897db4) returns true at once, to see whether the loading overlay drops."
    Patch:
      - [ be32, 0x00897db4, 0x38600001 ]
      - [ be32, 0x00897db8, 0x4e800020 ]
'''

OLD_CONFIG_ENTRY = '''  Free Realms IsLoaded forced true:
    All:
      All:
        All:
          Enabled: true
'''

THREAD_NAME = "Free Realms loading screen thread stops at once"

THREAD_PATCH_ENTRY = f'''  "{THREAD_NAME}":
    Games:
      All:
        All: [ All ]
    Author: "FreeRealms-PS3-Research"
    Patch Version: 1.0
    Notes: "Diagnostic: the stop check in the LoadingScreenThread loop (0xb641a8) answers true, so the splash thread ends at once."
    Patch:
      - [ be32, 0x00b641a8, 0x38600001 ]
'''

THREAD_CONFIG_ENTRY = f'''  {THREAD_NAME}:
    All:
      All:
        All:
          Enabled: true
'''

FAULT_WORD = 0x80000000   # lwz r0,0(0): a load from address 0, which is not mapped on the PS3


def fault_patch(address):
    """(name, patch.yml entry, patch_config.yml entry) of the "fault at <address>" diagnostic."""
    if address is None or address < 0x10000 or address % 4:
        raise ValueError("--address must be a 4-byte aligned code address (0x10000 or higher)")
    name = "Free Realms diagnostic fault at 0x%08x" % address
    entry = f'''  "{name}":
    Games:
      All:
        All: [ All ]
    Author: "FreeRealms-PS3-Research"
    Patch Version: 1.0
    Notes: "Diagnostic: the instruction at 0x{address:08x} becomes lwz r0,0(0), so the thread dies there and RPCS3 logs its call stack."
    Patch:
      - [ be32, 0x{address:08x}, 0x{FAULT_WORD:08x} ]
'''
    config = f'''  {name}:
    All:
      All:
        All:
          Enabled: true
'''
    return name, entry, config


# patch choice -> (name, patch.yml entry, patch_config.yml entry, older entry versions to drop)
PATCHES = {
    "overlay": (NAME, PATCH_ENTRY, CONFIG_ENTRY, OLD_PATCH_ENTRY, OLD_CONFIG_ENTRY),
    "loading-thread": (THREAD_NAME, THREAD_PATCH_ENTRY, THREAD_CONFIG_ENTRY, None, None),
    "fault": None,   # built from --address by fault_patch()
}

BACKUP_SUFFIX = ".before-isloaded"


def _newline(text):
    return "\r\n" if "\r\n" in text else "\n"


def _fit(block, newline):
    return block.replace("\n", newline)


def add_entry(text, entry, name):
    """Insert `entry` under the PPU hash key (or add the key); a no-op when the entry is already there."""
    newline = _newline(text)
    if re.search(r'^\s{2}"?' + re.escape(name) + r'"?:\s*$', text, re.M):
        return text, False
    block = _fit(entry, newline)
    key = re.search(r"^" + re.escape(PPU_HASH) + r":[ \t]*\r?\n", text, re.M)
    if key:
        return text[:key.end()] + block + text[key.end():], True
    tail = "" if text.endswith(("\n", "\r\n")) or not text else newline
    return text + tail + newline + PPU_HASH + ":" + newline + block, True


def remove_entry(text, entry):
    newline = _newline(text)
    block = _fit(entry, newline)
    if block not in text:
        return text, False
    return text.replace(block, "", 1), True


def edit(path, entry, remove, name, old_entry=None):
    if not path.is_file():
        print(f"missing: {path}")
        return False
    raw = path.read_bytes()
    text = raw.decode("utf-8")
    new, changed = remove_entry(text, entry) if remove else add_entry(text, entry, name)
    if old_entry:
        new, dropped = remove_entry(new, old_entry)       # the first, too broad version is never left behind
        changed = changed or dropped
    if not changed:
        print(f"unchanged: {path} ({'entry not found' if remove else 'entry already there'})")
        return True
    backup = path.with_name(path.name + BACKUP_SUFFIX)
    if not backup.exists():
        backup.write_bytes(raw)
    path.write_bytes(new.encode("utf-8"))
    print(f"{'removed from' if remove else 'added to'}: {path}")
    return True


def apply(rpcs3_dir, remove=False, which="overlay", address=None):
    """Add (or remove) a patch ("overlay", "loading-thread" or "fault" at `address`) in the portable RPCS3 folder `rpcs3_dir`. True when both files were handled."""
    if which == "fault":
        name, patch_entry, config_entry = fault_patch(address)
        old_patch = old_config = None
    else:
        name, patch_entry, config_entry, old_patch, old_config = PATCHES[which]
    rpcs3_dir = Path(rpcs3_dir)
    patches = rpcs3_dir / "patches" / "patch.yml"
    config = rpcs3_dir / "config" / "patch_config.yml"
    return (edit(patches, patch_entry, remove, name, old_patch)
            and edit(config, config_entry, remove, name, old_config))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--rpcs3", required=True, type=Path, help="the portable RPCS3 folder (it holds patches\\ and config\\)")
    parser.add_argument("--remove", action="store_true", help="remove the diagnostic patch again")
    parser.add_argument("--patch", choices=sorted(PATCHES), default="overlay",
                        help="overlay (default): the overlay check sees 'loaded'; loading-thread: the splash thread ends at once; "
                             "fault: the thread that reaches --address dies there (its call stack goes to the log)")
    parser.add_argument("--address", type=lambda v: int(v, 0), help="code address for --patch fault, for example 0xecd68")
    args = parser.parse_args()
    try:
        done = apply(args.rpcs3, args.remove, args.patch, args.address)
    except ValueError as err:
        parser.error(str(err))
    if not done:
        return 1
    if not args.remove:
        name = fault_patch(args.address)[0] if args.patch == "fault" else PATCHES[args.patch][0]
        print("Start the game from this RPCS3. The first start may take longer: RPCS3 recompiles the patched executable.")
        print("RPCS3.log should show: PAT: Applied patch (... description='%s' ...)" % name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
