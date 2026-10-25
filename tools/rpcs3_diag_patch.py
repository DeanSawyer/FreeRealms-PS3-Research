#!/usr/bin/env python3
"""Add or remove the "IsLoaded forced true" diagnostic patch in a portable RPCS3 folder.

The loading overlay of the PS3 client stays up after FinishedLoading (docs/re/zone-loading.md). Its per-frame check asks
Holder::IsLoaded (0x897db4) whether the terrain around the camera is loaded. This patch makes that function return true at
once (`li r3,1; blr`), which separates the two remaining explanations:

  * the overlay drops  -> IsLoaded was what stayed false (terrain around the camera never became ready);
  * the overlay stays  -> one of the four gate objects is not idle, so the check never runs.

    python tools/rpcs3_diag_patch.py --rpcs3 "C:\\Path\\To\\RPCS3_TEST"            # add and enable
    python tools/rpcs3_diag_patch.py --rpcs3 "C:\\Path\\To\\RPCS3_TEST" --remove   # take it out again

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
NAME = "Free Realms IsLoaded forced true"

PATCH_ENTRY = f'''  "{NAME}":
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

CONFIG_ENTRY = f'''  {NAME}:
    All:
      All:
        All:
          Enabled: true
'''

BACKUP_SUFFIX = ".before-isloaded"


def _newline(text):
    return "\r\n" if "\r\n" in text else "\n"


def _fit(block, newline):
    return block.replace("\n", newline)


def add_entry(text, entry, name=NAME):
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


def edit(path, entry, remove):
    if not path.is_file():
        print(f"missing: {path}")
        return False
    raw = path.read_bytes()
    text = raw.decode("utf-8")
    new, changed = remove_entry(text, entry) if remove else add_entry(text, entry)
    if not changed:
        print(f"unchanged: {path} ({'entry not found' if remove else 'entry already there'})")
        return True
    backup = path.with_name(path.name + BACKUP_SUFFIX)
    if not backup.exists():
        backup.write_bytes(raw)
    path.write_bytes(new.encode("utf-8"))
    print(f"{'removed from' if remove else 'added to'}: {path}")
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--rpcs3", required=True, type=Path, help="the portable RPCS3 folder (it holds patches\\ and config\\)")
    parser.add_argument("--remove", action="store_true", help="remove the diagnostic patch again")
    args = parser.parse_args()
    patches = args.rpcs3 / "patches" / "patch.yml"
    config = args.rpcs3 / "config" / "patch_config.yml"
    ok = edit(patches, PATCH_ENTRY, args.remove) and edit(config, CONFIG_ENTRY, args.remove)
    if not ok:
        return 1
    if not args.remove:
        print("Start the game from this RPCS3. The first start may take longer: RPCS3 recompiles the patched executable.")
        print("RPCS3.log should show: PAT: Applied patch (... description='%s' ...)" % NAME)
    return 0


if __name__ == "__main__":
    sys.exit(main())
