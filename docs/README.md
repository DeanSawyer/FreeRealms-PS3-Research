# Documentation

Findings are grouped by topic. Each document says whether a statement was **read from the binary**, **measured in a test
run**, or is a **hypothesis**; anything not marked is a measurement or a reading, never a guess.

## Reading order

New to the project? Read these in order: the [README](../README.md) for the current status, then
[`install/digest-format.md`](install/digest-format.md) (how the game gets onto disk), [`protocol/gateway.md`](protocol/gateway.md)
(how it talks to a server) and [`re/zone-loading.md`](re/zone-loading.md) (what keeps the loading screen up today).

## Index

| Folder | Document | What it covers |
| :--- | :--- | :--- |
| `install/` | [`digest-format.md`](install/digest-format.md) | The patch digest (`.soe`), the `.zs` container, the installer's checks, the test runs that established them, and how the digest is edited |
| `protocol/` | [`ps3ws-replies.md`](protocol/ps3ws-replies.md) | HTTP web-service replies recovered from the client (success rule, attributes, result codes) |
| `protocol/` | [`gateway.md`](protocol/gateway.md) | The UDP game-server connection: transport, packets, player data, `AddPc`, and a dated log of what each test showed |
| `re/` | [`README.md`](re/README.md) | Index and conventions for the reverse-engineering notes (addresses, workspace) |
| `re/` | [`assets.md`](re/assets.md) | Pack files, the asset manifest, the name hash, why an unknown name fails silently |
| `re/` | [`zone-loading.md`](re/zone-loading.md) | `ZoneDetails` → terrain → tiles → the per-frame loading check; test results so far |
| `re/` | [`addresses.csv`](re/addresses.csv) | Every function and data address the notes rely on, each with a text check that `tools/re/verify_addresses.py` runs |
| `history/` | [`installer-baseline.md`](history/installer-baseline.md) | The first measured installer result (error 1004), kept for the record |

## Conventions

* Addresses are virtual addresses in `game.elf` (file offset + `0x10000`); functions appear as `fn_00xxxxxx` or by address.
* Build under study: PS3 title `NPUA30048`, client build 1.77.
* Docs are checked by `python tools/check_docs.py` (broken links and missing paths fail the CI check).
* A new finding belongs in the document of its topic, with the run or address that supports it. See
  [`CONTRIBUTING.md`](../CONTRIBUTING.md).
