# Bootstrap server

An **experimental**, independently written server that stands in for the game's former servers, so the PS3 client can be
studied on a test machine. It is not a production server and not a complete game-server emulator. Python 3.10+, standard
library only.

| Layer | Port | What it does | Code |
| :--- | :--- | :--- | :--- |
| DNS | 53/udp | Redirects the game's hostnames to this machine; forwards everything else upstream (`--no-dns` to turn off) | `fr_ps3_bootstrap.py` |
| HTTP | 80/tcp | `AppConfig`, the patch digest, the `/ps3ws/*.action` web-service replies, and `.zs` asset downloads from your own install | `fr_ps3_bootstrap.py` |
| UDP gateway | 20260/udp | SOE UdpLibrary handshake, `Login`/`LoginReply`, `ZoneDetails`, player data, `AddPc` | `gateway_probe.py` |

## Requirements

* Your own copy of the client folder `NPUA30048`, including `USRDIR/Digests/NPUA30048.soe`. **Never commit it.**
* Administrator rights (ports 53 and 80) and an isolated test network.
* Optional: a second client folder (`--extra-client`) to supply files your main copy lacks or has damaged.

## Start

The easiest way is the one-command test, which also starts RPCS3 and packs the logs:

```powershell
python .\tools\run_test.py        # Administrator PowerShell, from the repository folder
```

It restores the original `scriptsbase.bin.sdat`, runs `zone_report.py` and `tile_report.py`, starts this server and RPCS3, waits
until RPCS3 is closed, then writes `Desktop\fr_test_<time>.zip` with the reports, the server log, the interesting lines and the
ends of the RPCS3 log and the gateway trace (`--no-restore` leaves the scripts alone).

To start only the server:

```powershell
python .\bootstrap\fr_ps3_bootstrap.py --client "C:\Path\To\NPUA30048" --match-local
```

Defaults: `--bind 127.0.0.1`, `--advertise 127.0.0.1`, `--upstream 1.1.1.1`, `--port 80`, `--gateway-port 20260`.

For RPCS3 on a separate network path, or a physical PS3, use a **reachable LAN address** and allow only the traffic you need:

```powershell
python .\bootstrap\fr_ps3_bootstrap.py --client "C:\Path\To\NPUA30048" --bind 0.0.0.0 --advertise YOUR_LAN_IPV4
```

`0.0.0.0` means *listen on every IPv4 interface*; it is never an address to give to the client. If another DNS service already
answers the game's hostnames, pass `--no-dns`.

## Options

| Option | Meaning |
| :--- | :--- |
| `--client DIR` | The `NPUA30048` folder (required). |
| `--codec NAME` | How `.zs` assets are encoded. Default `zs-zlib-zero-trailer`, the container the client accepts. Other names are the early experiments; `lzma-alone` reproduces installer error 1004. |
| `--digest FILE` | The original 21,466-byte digest to serve. The client overwrites the local copy with whatever it last received, so keep the original and pass it here. |
| `--match-local` | Serve a digest rewritten to describe the files actually in `--client` (for builds that differ from the original digest). The client does not verify the digest's trailer, so this works. Each change is printed as `DIGEST-REWRITE`. |
| `--extra-client DIR` | Another client folder to search for a file whose CRC32 matches the request (repeatable). |
| `--serve-mismatch` | Experiment, **rejected by the client**: send a local file of the right name but a different CRC. Kept to reproduce the result. |
| `--existing-character` | Report one sample character instead of an empty account (character select instead of creator). |
| `--account-status N` | Status for an account with no characters (default 2, the client's “No existing character found”; 1 crashes the client). |
| `--xml-style attributes\|elements` | How values are written in web-service replies; the client reads attributes (default). |
| `--server-list-variant baseline\|zone` | Original server-list XML or an experimental zone response. |
| `--gateway-port PORT` | UDP port of the gateway (`0` turns it off). |
| `--advertise IP`, `--bind IP`, `--port N`, `--upstream IP`, `--no-dns` | Network settings, see above. |

### Environment switches (gateway)

| Variable | Effect |
| :--- | :--- |
| `FR_ZONE`, `FR_ZONE_TYPE` | Override the zone name and type sent in `ZoneDetails` (default: chosen from your install, type `2`). |
| `FR_SPAWN="x,y,z"`, `FR_SPAWN_Y` | Override the spawn position, or only its height (default 400). |
| `FR_ADDPC` | Which character `AddPc` adds: `other` (default), `self`, `both`, `none`. |
| `FR_EXTRA=1` | Also send hit points, mana, game settings and an empty item list (layouts from the PC server, untested on the PS3 client). |
| `FR_WELCOME=0`, `FR_TIME=0` | Leave out the welcome-screen packet or the game-time reply. |

## Which zone and spawn the server sends

At login the gateway sends `ZoneDetails` with a zone name. The client turns it into `<zone>.gzne`, `<zone>.cob` and `<zone>.odd`,
and keeps its loading screen until the terrain around the player has loaded ([`docs/re/zone-loading.md`](../docs/re/zone-loading.md)).
On start the server reads the pack indexes and the manifest of `--client` ([`assetlib.py`](assetlib.py)), picks a zone whose files are
all present (preferring `FabledRealms`) and a spawn in the centre of a terrain tile whose neighbourhood is stored locally. The choice is
logged as `ZoneDetails: zone=… type=2` and `spawn: x=… z=…`.

```powershell
python .\tools\zone_report.py "C:\Path\To\NPUA30048\USRDIR"          # which zones the install can load
python .\tools\tile_report.py "C:\Path\To\NPUA30048\USRDIR"          # terrain tiles near the player and the picked spawn
python .\tools\gzne_info.py   "C:\Path\To\NPUA30048\USRDIR" ZoneName  # terrain box and cell size
```

## The patch digest and asset downloads

The client downloads each missing file as a `.zs` container (a zlib stream plus a 129-byte trailer) and checks the decoded CRC32.
The formats, the test runs that established them and the way the digest is edited are in
[`docs/install/digest-format.md`](../docs/install/digest-format.md); `tools/verify_client.py` compares a game folder against the
digest (read-only), and `tools/parse_digest.py` lists every file in it.

## Web-service replies

The `/ps3ws/*.action` replies follow the formats recovered from the decrypted client
([`docs/protocol/ps3ws-replies.md`](../docs/protocol/ps3ws-replies.md)): values are XML attributes, and `Status` must be `1` for the
client to treat a reply as successful.

## The UDP gateway

After `character/login` the client starts the SOE UdpLibrary against the `GatewayAddress` it was given. `gateway_probe.py`
completes the handshake (no CRC, no encryption), acknowledges reliable packets, decodes the client's `Login` and answers it, then
sends the packets that get the client to *FinishedLoading* and logs every packet as hex. Packet layouts and findings are in
[`docs/protocol/gateway.md`](../docs/protocol/gateway.md); the layouts follow the UdpLibrary port in the Clover server
(`Sanctuary.UdpLibrary`, AGPL-3.0).

## Modules

| File | Role |
| :--- | :--- |
| `fr_ps3_bootstrap.py` | Entry point: DNS, HTTP, web-service replies, asset serving |
| `gateway_probe.py` | UDP gateway and the packets sent into the world |
| `assetlib.py` | Readers for the client's packs, manifest and `.gzne`; zone and spawn choice |
| `digest_edit.py` | Read, edit and re-serialise the digest (`--match-local`) |
| `codec_candidates.py` | The `.zs` encoders, including the experiments that failed |

## Files written, safety

* The server writes `ps3_protocol_trace.jsonl` and `ps3_gateway_trace.jsonl` next to itself. They are ignored by Git and may
  contain local addresses and paths; redact before sharing.
* No authentication, no hardening. Do not expose ports 53, 80 or 20260 to the internet. See [`SECURITY.md`](../SECURITY.md).
* Other asset responses depend on client files on disk; none are bundled with this repository.
