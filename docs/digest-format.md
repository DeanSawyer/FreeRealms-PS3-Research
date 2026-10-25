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

## Hypotheses, not yet tested in the emulator

- The `.zs` file is `zlib stream (or raw bytes) + 129-byte trailer`, the same shape as the digest.
- The trailer's first byte (`0x01` in the digest) may identify the codec or signature type.
- The remaining 128 bytes are not a plain MD5/SHA-1/SHA-2 digest of the digest body and look random.
  They may be a signature (128 bytes = 1024-bit), which would only be verifiable with the
  matching public key. Whether the client checks it for assets is unknown.
- The stored-vs-compressed decision is probably "payload length equals size means raw", but the
  client may use the trailer byte instead.

## Next experiments

1. `--codec zs-zlib-zero-trailer`: correct container, zeroed trailer. If the installer gets past
   error 1004 and fails later with a different error, the trailer content is being checked.
2. `--codec zs-zlib-digest-trailer`: the same container with the digest's own trailer bytes.
3. Record the installer error and `ps3_protocol_trace.jsonl` after each run.
