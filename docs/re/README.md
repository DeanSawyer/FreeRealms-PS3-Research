# Reverse-engineering notes — PS3 Free Realms client (NPUA30048, build 1.77)

Goal: a working PS3 client against a local server ("restore the client first"). These notes record what was
**read from the binary**; anything not read from the binary is labelled as inferred or open.

## Index

| Document | Contents |
|---|---|
| [`zone-loading.md`](zone-loading.md) | ZoneDetails → terrain load → the loading overlay; GZNE header; failure table; test plan |
| [`assets.md`](assets.md) | Pack and manifest formats, the name hash, the request path |
| [`addresses.csv`](addresses.csv) | Every function/data address the notes rely on, with a text check |
| [`../ps3-gateway.md`](../ps3-gateway.md) | Wire protocol of the gateway (UDP 20260) and the session log of findings |
| [`../ps3ws-replies.md`](../ps3ws-replies.md), [`../digest-format.md`](../digest-format.md) | HTTP replies and the `.soe` digest |

## Workspace

* `game.elf` — PowerPC64 big-endian ELF with 32-bit pointers. **vaddr = file offset + 0x10000.** TOC `0xf1f758`,
  function descriptors (OPD) at `0xeeb530`. Not committed (user's own file).
* Ghidra headless project built by [`tools/re/`](../../tools/re/README.md); queries go through a small file-queue
  server so the analysis is done once and answered many times.
* Python tools in `tools/` read the user's install read-only (pack indexes, manifest, `.gzne` header).

## Conventions

1. A claim in a document gets an address. The address gets a row in `addresses.csv` with an `expect` text.
2. `python3 tools/re/verify_addresses.py <queue>` must print `N of N rows verified` before a commit that changes either.
3. No guessing: unverified items go in a "Not yet verified" / "Open questions" list.
4. Client files, packs, manifests, digests and runtime traces are never committed.
5. `Clover` (the PC server source) is a read-only reference for protocol *shape*; PS3 layouts are re-derived from the
   PS3 binary.

## Status

* **Solid (read from the binary, checked by the table):** ZoneDetails handler and its failure path; holder/controller
  structure; terrain request chain (`.gzne`/`.cob`/`.odd`); GZNE header fields used by the loaded check; manifest and
  pack formats; the CRC-64 name hash; the per-frame overlay check.
* **Leading explanation, unconfirmed on the user's install:** the zone name sent (`FabledRealms`) is not present in
  the PS3 content, so the terrain header request is refused and the overlay stays. `tools/zone_report.py` decides this
  in one run.
* **Open:** spawn position for a PS3 zone; tile streaming / bubble readiness if the zone does exist; the PS3 layouts of
  the packets Clover sends in `OnClientIsReady`.
