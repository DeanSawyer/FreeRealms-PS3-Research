# Tools

Command-line utilities for studying the client and checking an install. Everything reads **your own** files and is
read-only unless noted. Python 3.10+, standard library only, except where marked.

Run any tool with `-h` or read the docstring at the top of the file for its options.

## Test runs

| Tool | Purpose |
| :--- | :--- |
| [`run_test.py`](run_test.py) | One-command test (Administrator PowerShell): restores the original scripts, starts the server and RPCS3, then packs reports and logs into a zip on the Desktop. `--server-exe` tests the C# port (`Sanctuary.Ps3.Server.exe`) instead of the Python script. |
| [`rpcs3_diag_patch.py`](rpcs3_diag_patch.py) | Adds (or `--remove`s) a diagnostic RPCS3 patch: the default makes the loading-overlay check see `IsLoaded` as true; `--patch loading-thread` ends the native loading-screen thread at once; `--patch fault --address 0x...` makes the thread that reaches an address die there, so RPCS3 logs its call stack ([`docs/re/zone-loading.md`](../docs/re/zone-loading.md)). `run_test.py` has matching switches `--force-isloaded`, `--stop-loading-thread` and `--fault-at ADDR`. |

## C# reference vectors

| Tool | Purpose |
| :--- | :--- |
| [`gen_ps3_vectors.py`](gen_ps3_vectors.py), [`gen_ps3_web_vectors.py`](gen_ps3_web_vectors.py), [`gen_ps3_gateway_vectors.py`](gen_ps3_gateway_vectors.py) | Run the Python reference and write what it answers as C# test data (`src/ps3/Sanctuary.Ps3.Tests/*.g.cs`). `--check` fails when a file is out of date; CI runs it. See [`src/ps3/README.md`](../src/ps3/README.md). |

## Install and digest

| Tool | Purpose |
| :--- | :--- |
| [`verify_client.py`](verify_client.py) | Compare a game folder against the digest: which files match, differ or are missing. |
| [`parse_digest.py`](parse_digest.py) | List every file in the digest with its size, compressed length and CRC32; test the `.zs` size rule. |
| [`analyze_digest.py`](analyze_digest.py) | Exploratory structure analysis of the digest (entropy, record layout around a known asset). |
| [`codec_sizes.py`](codec_sizes.py) | Compare candidate encodings of an asset against the manifest length, without an emulator run. |
| [`restore_locale.py`](restore_locale.py) | Rebuild locale files that lost their CRLF line endings (verifies the MD5 before writing). |

## Assets and world data

| Tool | Purpose |
| :--- | :--- |
| [`list_packs.py`](list_packs.py) | List asset names stored in the `assets_ps3_NNN.pack` files (names only). |
| [`asset_manifest.py`](asset_manifest.py) | Query `assets_ps3_manifest.bin`: what the client may load, and what is only available by download. |
| [`zone_report.py`](zone_report.py) | Which zone names the client can load from this install; flags CRC mismatches. |
| [`gzne_info.py`](gzne_info.py) | Print a zone's terrain header (cell size, box, tile edge). |
| [`tile_report.py`](tile_report.py) | Which terrain tiles the client needs around the player, which are local, and where the player is placed. |

## Binary analysis

| Tool | Purpose |
| :--- | :--- |
| [`self_info.py`](self_info.py) | Print a SELF/EBOOT header (key revision, NPDRM license type). |
| [`elf_strings.py`](elf_strings.py) | Strings in `game.elf` matching a pattern, with their neighbours. |
| [`ppc_trace.py`](ppc_trace.py) | Run a function of `game.elf` in a small PowerPC emulator and record every read from an input buffer (finds packet layouts). |
| [`ppc_explore.py`](ppc_explore.py) | Find list fields in a packet by probing each 4-byte field with a count of 1. |
| [`undat.py`](undat.py) | Decrypt the client's SDAT files, e.g. the Lua UI scripts. Needs `cryptography`. GPL-2.0-or-later, derived from RPCS3. |
| [`luadis.py`](luadis.py) | List the functions of a decrypted Lua 5.1 chunk that use a given constant. |
| [`sdat_patch_loading.py`](sdat_patch_loading.py) | Test patch: make the Lua loading checks answer "done" and re-pack the SDAT. Needs `cryptography`. GPL-2.0-or-later. |
| [`re/`](re/README.md) | Ghidra workspace: headless import, query server and client, and the check that keeps `docs/re/addresses.csv` honest. |

## Repository upkeep

| Tool | Purpose |
| :--- | :--- |
| [`check_docs.py`](check_docs.py) | Fail on broken Markdown links or documentation that names a file that no longer exists. Runs in CI. |
