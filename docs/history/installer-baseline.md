# The first installer experiment (12 October 2026)

> **Historical record.** This is the project's first measured result, kept unchanged in substance. Its open question —
> why the client rejected the downloaded asset with error 1004 — was **answered later**: the `.zs` files are a zlib stream
> followed by a 129-byte trailer, not LZMA. See [`../install/digest-format.md`](../install/digest-format.md) for the
> container, and the [README](../../README.md) for the current status.

## Setup and what worked

The local test environment progressed through the initial configuration and patch-manifest requests and then entered the
installer's asset-loading state.

| Component | State at the time | Evidence |
| :--- | :--- | :--- |
| Local DNS routing | Observed working | Selected legacy hostnames resolved to the local endpoint. |
| Startup configuration | Observed working | Client requested and received an `AppConfig` response. |
| Patch manifest retrieval | Observed working | HTTP requests for `FreeRealms.soe` completed. |
| Installer state progression | Observed | `init → status → statusDone → loading` |
| Asset download and decode | Blocked | Client reported error `1004`, `Unknown codec reading`. |

One captured RPCS3 session recorded these transitions:

```text
01:04.122  init       → status
01:04.282  status     → statusDone → loading
01:35.364  loading    → error

Installer error 1004: Unknown codec reading
```

The loading interval was about **31.08 seconds**. That figure describes one test; it is not a guaranteed startup time. A
separate local preservation snapshot of 993 files matched the original files by SHA-256; that snapshot is not distributed.

## The requests the client made

```text
GET  /app/appconfig.php?game=frps3&server=live
HEAD /patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe
GET  /patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe
```

The local service answered these, after which the installer advanced to its asset-loading phase. This validated the initial
handshake only.

## The asset the client rejected

```text
/patch/freerealms/ps3scelive/ps3-web/FreeRealms.zs/art/environments/terraincalendardefinitions.txt.zs
```

| Field | Value |
| :--- | :--- |
| Asset | `terraincalendardefinitions.txt` |
| Uncompressed test input | 403 bytes |
| CRC32 | `0x912D54DE` (decimal `2435667166`) |
| Experimental LZMA payload | 251 bytes |
| Manifest-indicated compressed length | 373 bytes |
| Outcome | Rejected by the client (`ErrorID: 1004`, `ErrExtra: Unknown codec reading ...`) |

At the time the test established only a decoder incompatibility, not the real format; ZIP, zlib/Deflate and LZMA were all
unproven. The 373-byte manifest length later turned out to be 244 bytes of zlib plus the 129-byte trailer.

## Why keep this

It shows how the format was found — by measuring lengths and CRCs rather than guessing a codec — and documents the
starting point for anyone reproducing the work from scratch.
