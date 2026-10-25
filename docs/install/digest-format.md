# Digest and `.zs` container: findings

Source: your own `USRDIR/Digests/NPUA30048.soe` (21,466 bytes) and
`terraincalendardefinitions.txt` (398 bytes LF; 403 bytes after CRLF conversion).
Reproduce with `tools/parse_digest.py` and `tools/codec_sizes.py`.

## Confirmed by measurement

| Finding | Evidence |
| :-- | :-- |
| `NPUA30048.soe` is a **zlib stream** (`78 DA`) followed by a **129-byte trailer** | Standard zlib decompression yields 44,983 bytes; exactly 129 bytes remain, first byte `0x01` |
| The decompressed digest is a tag/length/value tree listing 741 files | Parsed by `tools/parse_digest.py` |
| File record fields: tag 1 name (NUL-terminated), tag 2 compressed length, tag 3 size, tag 4 CRC32, tag 8 timestamp | Terrain record decodes to 373 / 403 / `0x912D54DE`, matching the earlier notes |
| For every file, `compressed length - 129` is never larger than the file size | 741 of 741 entries |
| 11 small files have `compressed length - 129 == size` | Consistent with "stored raw when zlib does not help" (e.g. `logs.txt`: 9 bytes, listed as 138) |
| Terrain asset: zlib of the 403-byte CRLF file is **244 bytes**; 244 + 129 = **373** | Matches the manifest length exactly (`tools/codec_sizes.py`) |
| The CRLF conversion in the bootstrap server is correct | CRC32 of the converted file is `0x912D54DE` |

## Consequences for the earlier experiment

The LZMA response (251 bytes, no trailer) could not match the manifest length of 373.
The installer's "Unknown codec reading" is consistent with it reading the last 129 bytes of the
response as a trailer that was not there, but that link is a **hypothesis**, not a decoded code path.

## Observed in RPCS3 (2026-10-24 run, `--codec zs-zlib-zero-trailer`)

- The installer fetched `AppConfig`, the digest, then `terraincalendardefinitions.txt.zs?c=2435667166&l=373`.
  `c` is the CRC32 of the decoded file (decimal) and `l` is the manifest compressed length.
- The server answered with 373 bytes (zlib payload + zeroed 129-byte trailer). About 25 ms later the client
  requested the next file (`beam_mask.gtf.zs`), with no retry of the terrain file. So the response **appears to
  have been accepted**, which also suggests the trailer content is not rejected at this stage. This is inferred from
  request order, not from a decoded code path.
- The next request returned 404 because the server only knew how to build the terrain file. The server now builds
  any `.zs` response from the matching local file under `USRDIR`, verifying the CRC from `c`.
- The client also retries a missing asset with `&cb=L`, `&cb=LF`, `&cb=LFH`, ... (growing letter suffixes).

## Second RPCS3 run (2026-10-24 20:42, `--codec zs-zlib-zero-trailer`)

- 140 responses were sent (terrain + graphics resources), **all matching the manifest length exactly**; the client kept
  requesting the next file with no retries, so the format looks accepted.
- It stopped at `locale/en_us_data.dat`: the digest expects 5,744,504 bytes / CRC32 `0xA1D1B63F`, but the local copy in
  `FR_INSTALL_TEST\NPUA30048` is 5,652,016 bytes / CRC32 `0xCA56784B`, a different build. The client retried six times
  (`cb=L`, `LF`, `LFH`, `LFHB`, `LFHBD`, `LFHBDN`) against our 409 and then failed (installer error 1004).
- `--extra-client DIR` lets the server search other game folders for a file whose CRC32 matches the client's request.

## Third RPCS3 run (21:05): does the client verify content after download? Yes

- With `--serve-mismatch` the server sent the local `en_us_data.dat` (CRC 0xCA56784B) as a valid zlib stream padded to exactly
  the requested 2,368,100 bytes (HTTP 200). The client did not accept it: it immediately requested the same file again with
  `cb=L`, then `cb=LF`. So the client checks the decoded content (CRC32 from `c=`, possibly also the size) after downloading.
  Serving a different build under the original digest's values cannot work.
- Next hypothesis: the digest can instead describe the local build (`--match-local`, below).

## Fourth RPCS3 run (21:15, `--match-local`): the edited digest is accepted

- The client fetched the 21,468-byte rewritten digest and asked for `en_us_data.dat` with the **rewritten** values
  (`c=3394664523` = 0xCA56784B, `l=2359127`). It accepted the response and moved on, so **the digest's 129-byte trailer is
  not verified** (at least not against the body), and the digest can describe the local build.
- `en_us_data.dir`: the local file was the LF-collapsed version of the original; LF-to-CRLF restored it exactly
  (CRC 0x5C6C9D59), so the GitHub copy stores some binary files with CRLF turned into LF. For `en_us_data.dat` the same
  conversion does not give the original (mixed line endings), so the English string data is probably not the original
  bytes, and its `.dir` index is now the original: the game may misread it later (not an installer problem).
- Next stall: `logs/logs.txt` (index 160, 9 bytes, listed with compressed length 138 = size + 129, i.e. stored raw). The CRC
  in the request matched our data and the 138-byte response (raw bytes + `0x01` + 128 zero bytes) was rejected three times
  (`cb=L`, `cb=LF`). Hypothesis: the first trailer byte selects the payload encoding (0x01 = zlib), so stored files need a
  different value. 11 files are stored: `logs/logs.txt`, 8 small `resources/*.txt`, `loadingscreen.gfx`, `fr_logo.png`.
- The server now varies the container on each retry of a stored file (`STORED_VARIANTS`: trailer byte 0x00, 0x02, 0x03,
  0x04, 0x80, 0xFF, then a stored-block zlib stream) and remembers the variant after the client moves on to another file.
  Look for `STORED ... attempt=N variant=...` in the log.

## After the install (21:30-21:46)

- The installer reached 100% on `--match-local` and exited. `RPCS3.log` shows `PS3Installer is launching:
  /dev_hdd0/game/NPUA30048/USRDIR/game.self. Using NpDrm: true.` followed by `_sys_process_exit` (no exitspawn syscall):
  RPCS3 ended the emulation instead of starting `game.self`. A second launch ran the installer again, much faster (files
  already current).
- Booting `game.self` directly (`rpcs3.exe --no-gui ...\USRDIR\game.self`) fails in RPCS3: `SELF: Failed to decrypt SELF
  metadata info!` / `Failed to decrypt content`. `game.self` is one of the three files that differ from the digest
  (15,897,920 bytes locally vs 15,897,872; locally it does not compress, the original did). `tools/self_info.py` prints the
  SELF header (key revision, NPDRM license type) to tell a missing-license problem from a corrupt or different file.

## Stored files, and the game running (21:44-22:07)

- Stored (uncompressed) files: the first variant, raw payload + trailer byte `0x00` + 128 zero bytes, was accepted
  (`fr_logo.png`: `STORED variant raw+trailer00 was accepted`). Rejected earlier: raw + `0x01`. So the trailer's first byte
  is the payload encoding (0x01 = zlib, 0x00 = stored).
- `game.self` fails in RPCS3 itself (`Failed to decrypt SELF metadata info`), but RPCS3's `Utilities > Decrypt PS3 Binaries`
  produced `game.elf` from it. Booting `game.elf` directly runs the game: the log shows its own threads (Deep Engine Renderer,
  Miles audio thread, fios, SPURS) for over six minutes with no fatal error.
- At 22:03:42 the game called `POST /ps3ws/game/serverList.action?request_locale=en_US` (8-byte body, host
  `ps3.freerealms.com`); our placeholder reply was 228 bytes. Immediately after, the game opened
  `cellMsgDialogOpen2(type=0xa1, msgString="")` (OK button, empty text) and showed a blank box. Hypotheses: our placeholder
  XML is not what the client's parser expects, and/or the message text is empty because the English string data does not match
  its index (`en_us_data.dat` is the LF-collapsed copy).
- `tools/elf_strings.py` lists strings near a pattern in `game.elf` to recover the tag names the client parses.

## Locale data restored exactly (22:12)

- `locale/en_us_data.dat` is a UTF-8 text file (BOM, then one `id<TAB>type<TAB>text` record per line, CRLF-terminated);
  `en_us_data.dir` is its text index: a `## key: value` header (`Count`, `MD5Checksum` of the original `.dat`, ...) and one
  `id<TAB>offset<TAB>length<TAB>flag` row per record. Offsets/lengths refer to the original CRLF file and the length excludes
  the record's CRLF.
- The GitHub copy has all CRLF turned into LF (5,652,016 bytes, 92,509 LFs). A blind LF-to-CRLF fails because 21 records hold
  a bare LF inside their text. Walking the index (record i ends `length` bytes after its start, then LF becomes CRLF) rebuilds
  the file: 5,744,504 bytes, CRC32 0xA1D1B63F, MD5 DCE1D769... = the index's `MD5Checksum`. `tools/restore_locale.py` does this
  (verifies the MD5 before writing, keeps a `.lf-copy` backup).
- Consequence: `en_us_data.dat` was never a different build, only a line-ending casualty, and the game was reading the original
  `.dir` against the collapsed `.dat` (offsets off by up to 92,488 bytes) when the blank message box appeared.

## Digest layout (parsed by `tools/parse_digest.py`)

- Tag/length/value tree. Length byte `< 0x80` is one byte, `0x80-0xFE` starts a 15-bit two-byte length, `0xFF` is
  followed by a four-byte length. Directory = tag 2 (children inside, name in tag 1); file = tag 3 holding name (tag 1),
  compressed length (2), size (3), CRC32 (4), timestamp (8). Tag `0xFE` holds child counts and is skipped.
- 741 downloadable files. Seven more records (`useroptions.ini`, `logs`, ...) carry only a flag (tag 6) and are
  client-generated.

## Install-state findings (2026-10-24)

_Notes from the first runs, in the order they were made. Later runs answered several of the open points; see the sections on
stored files and locale data above._

- **The client stamps downloaded files with the digest's timestamp (tag 8).** `beam_mask.gtf` in the folder the client
  wrote into has mtime 2010-05-12 06:22:46 UTC, exactly the digest value; the untouched GitHub copy has a 2026 mtime.
  On the next run the client skipped the 140 files it had already received and resumed at `en_us_data.dat`.
- `tools/verify_client.py` against the GitHub copy: 195 files identical, 524 identical after LF-to-CRLF conversion
  (line endings rewritten by git), 18 missing, 3 different size, 1 same size but different CRC.
- The 18 missing files are the other locales (`de_de`, `en_gb`, `es_*`, `fr_fr`, `ja_jp`, `ko_kr`, `zh_*`). The client
  skipped them and requested `en_us` only.
- Remaining real differences: `game.self` (+48 bytes locally) and `ui/scriptsbase.bin.sdat` (CRC differs).
  `locale/en_us_data.dat` and `resources/itemdefinitions.txt` are suspected line-ending rewrites of files larger than the
  old 4 MB fix limit (limit raised to 64 MB; unverified against the real files).
- **Update (21:00 run): `en_us_data.dat` is not a line-ending issue.** `verify_client.py` reports it as a size mismatch
  (digest 5,744,504 bytes, local 5,652,016) and the same for `resources/itemdefinitions.txt` (10,561,852 vs 10,503,926);
  the GitHub copy is a different build of those files. No local file has the CRC the client asks for.
- **Stamping the files with the digest timestamp does not stop the download.** After setting `LastWriteTimeUtc` on the four
  mismatched files, the client still requested `en_us_data.dat` (CRC 0xa1d1b63f, length 2368100), got 409, and retried
  seven times with `cb=` suffixes `L, LF, LFH, LFHB, LFHBD, LFHBDN`. The skip decision uses more than the timestamp
  (size and/or CRC of the local file).
- Digest order: indexes 0-143 download first (the other locales `de_de`, `en_gb` are skipped), `en_us_data.dat` is
  index 144, `resources/itemdefinitions.txt` 302, `ui/scriptsbase.bin.sdat` 651, `game.self` 734.

## The early hypotheses and how the runs resolved them

| Hypothesis | Outcome |
| :-- | :-- |
| A `.zs` file is a zlib stream (or raw bytes) plus a 129-byte trailer, like the digest | **Confirmed** — accepted by the client (second RPCS3 run onward) |
| The trailer's first byte selects the payload encoding | **Observed**: `0x01` = zlib, `0x00` = stored; stored files with `0x01` were rejected |
| The other 128 trailer bytes are a signature the client checks | **Not supported**: zeroed bytes are accepted for assets, and an edited digest with its old trailer is accepted |
| Stored-versus-compressed is "payload length equals size" | Consistent with every run; the decision is carried by the trailer byte |
| The client verifies downloaded content | **Yes**: it re-requests a file whose decoded CRC32 differs from the `c=` value (third run) |

Still open: what the 128 trailer bytes mean, and whether any other first-byte values exist.

## Editing the digest (`bootstrap/digest_edit.py`)

- `serialize(parse(body)) == body` holds byte-for-byte for the original digest, and `zlib.compress(body, 9)` reproduces the
  original 21,466-byte file exactly (it was written with zlib level 9), so an unedited digest round-trips identically.
- Header tag 21 equals the sum of all file sizes divided by 1024 (1,750,506 KiB); tag 27 = 748 records; tag 28 = `game.self`.
- `--match-local` rewrites the size (tag 3), CRC32 (tag 4) and compressed length (tag 2) of records whose local file differs
  from the original build, and recomputes tag 21. Missing files, matching files and LF/CRLF-only differences are untouched.
  The 129-byte trailer is copied unchanged, so it no longer matches the body. The client accepted the edited digest (fourth
  run), so the digest's trailer is not verified.
