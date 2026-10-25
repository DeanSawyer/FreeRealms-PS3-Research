# Asset storage: packs, manifest and the request path

Everything here was read out of `game.elf` (NPUA30048, build 1.77). Function and data addresses are virtual
addresses; they are listed with the check text that proves them in [`addresses.csv`](addresses.csv) and re-verified
against the analysed binary by `tools/re/verify_addresses.py`. Readers for every format below are in
[`bootstrap/assetlib.py`](../../bootstrap/assetlib.py).

## Pack files — `assets_ps3_NNN.pack`

The packs hold the game's content. Only the **index** is parsed here; asset bytes are never copied anywhere.

| Part | Fact | Where it was read |
|---|---|---|
| Index reader | Reads 0x2000 bytes from file offset 0, parses one chunk, continues at the chunk's `next` offset until it is 0 | `0xa28f8c` (`PackFile::ReadIndex`) |
| Chunk | `u32 next` · `u32 count` · `count ×` entry | `0xda46c4` |
| Entry | `u32 name_length` · name bytes · `u32 offset` · `u32 length` · `u32 crc` | `0xda4368` |
| Byte order | Big endian on every host (the stream does an endian test with `stw`/`lbz`) | `0xda3020` |
| Names | Compared case-insensitively; digit-only names are stored as a `u64` | `0xa26e24` |
| Read | Looks up the entry in every pack of the group and reads `length` bytes at `offset` | `0xa24d30`, `0xa26e24` |
| Writer | `0xa27350` is the *writer* (flush of the pending index). It was once mistaken for the reader | — |

The error strings `PackFile Error: pack file could not be read` (`0xa24d30`) and `PackFileGroup Error` (`0xa26e24`)
are logged when a read fails.

`tools/list_packs.py` lists the names; `tools/zone_report.py` cross-checks them with the manifest.

## Manifest — `assets_ps3_manifest.bin`

A flat array of 16-byte records, sorted by the first field. The client binary-searches it (`0x7ff838`, `0x803120`).

```
u64  hash      CRC-64 of the UPPER-CASED asset name
u32  crc32     compared with the pack entry's crc by the "is the local copy current" check
u32  size      low 31 bits = size, top bit = "stored compressed (.z)" for downloads
```

* **Hash.** `fn_0001fac0` is a table-driven CRC-64: reflected polynomial `0x95AC9329AC4BC9B5`, initial value and final
  xor all ones, table at `0xdb1f10` (the table is exactly this polynomial's). Self-check: `crc64("123456789")` is
  `0x3558e8e979f60d7e`. Only `a`–`z` are folded to upper case.
* **Byte order.** The PS3 build byte-swaps the records after loading (`0x8035c0`), so the file may be big or little
  endian. `assetlib.Manifest` picks the order by checking that the hashes come out sorted.
* **Use.** `tools/asset_manifest.py check|scan|verify`.

## The request path (why an unknown name fails silently)

```
asset request (name)
  └─ AssetDeliveryIndirect::Request                    0x802828
       ├─ name not in manifest  → log "asset not in manifest", return 1 (refused)        no retry, no error screen
       └─ name in manifest
            └─ IsLocalCurrent                           0x801020
                 manifest entry AND pack entry exist and their crc32 are equal → read from the pack
                 otherwise → treated as not current; a download from the server is the apparent intent (inferred, not traced)
```

Consequences used elsewhere:

1. **A name must be in the manifest to be loadable at all.** Packs alone are not enough; both are needed for a local
   read, and the crc must match.
2. **A refused request is not an error screen.** Callers such as the terrain header request return false and the
   caller gives up quietly — see [`zone-loading.md`](zone-loading.md).
3. Names are normalised before the lookup (`0x7f7928` swaps an extension for the local variant when that file exists).

## Open questions

* Download of an asset that is in the manifest but not (or stale) in the packs goes through the HTTP asset service;
  its URL layout and compression (`.z` flag) are covered by `docs/ps3ws-replies.md` and have not been re-derived here.
* The Jenkins-style hash used for in-memory pack lookup is documented only as far as `assetlib.name_hash` needs
  (it is not needed to read packs from disk).
