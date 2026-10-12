# PS3 research bootstrap server

This is an **experimental**, independently authored DNS/HTTP server for studying the startup behavior of a legally obtained Free Realms PlayStation 3 installation. It is not a production server or a complete game-server emulator.

## Requirements

- Python 3.10+ (standard library only).
- Your own separately obtained `NPUA30048` client directory, including `USRDIR/Digests/NPUA30048.soe`. **Do not commit this directory or manifest.**
- Administrator privileges or equivalent permissions may be needed to bind DNS port 53 and HTTP port 80.
- A dedicated test network/environment.

## Local-only startup

```powershell
python .\bootstrap\fr_ps3_bootstrap.py --client "C:\Path\To\NPUA30048"
```

Defaults: `--bind 127.0.0.1`, `--advertise 127.0.0.1`, `--upstream 1.1.1.1`, `--port 80`.

For an RPCS3 test that uses a separate guest-network path or for a physical PS3, use a **real, reachable LAN address** and allow only the necessary traffic on your firewall:

```powershell
python .\bootstrap\fr_ps3_bootstrap.py --client "C:\Path\To\NPUA30048" --bind 0.0.0.0 --advertise YOUR_LAN_IPV4
```

**`0.0.0.0` means listen on all IPv4 interfaces. It is not a destination address to advertise to the PS3.** `127.0.0.1` only works when the client can reach the server through the same host's loopback network. For separate devices, replace `YOUR_LAN_IPV4` at runtime, without committing that value.

If another DNS service already handles name resolution, pass `--no-dns`.

## Limitations and safety

- The current experimental terrain asset response uses LZMA. Its format is **not verified compatible** with the PS3 client's decoder; an observed test resulted in installer error **1004**.
- Other asset responses depend on client files on disk; none are bundled with this repository.
- This test server has no authentication or production hardening. Do not expose ports 53 or 80 to the public internet.
- The script writes `ps3_protocol_trace.jsonl` next to itself. This file is ignored by Git and may contain local addresses or file paths.
- Keep the known-working experiment separate from this published, local-default variant until behavior has been revalidated.
