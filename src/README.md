# Windows Bootstrap Manager (C# preview)

An experimental **.NET 10 WPF** desktop application that combines a local HTTP service, optional DNS responder, editable server settings, and a live log window. No Python terminal is needed to run this C# version.

> [!WARNING]
> **Preview / unverified:** The first Windows CI build has not yet been confirmed successful. The C# engine does not implement all routes or the exact asset behavior of the preserved Python baseline. Do not replace the known 31-second test environment with this build.

## Downloading a CI installer

1. Open [Windows installer workflow](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml).
2. Open a **successful** run.
3. Under **Artifacts**, download `FreeRealmsPS3Bootstrap-Windows-Setup`.
4. Extract the artifact ZIP and run `FreeRealmsPS3Bootstrap-Setup.exe`.

GitHub may require repository access and login. If there is no successful run, the installer is **not available yet**.

## Running from source

On Windows with .NET 10 SDK:

```powershell
dotnet run --project src/FreeRealms.Bootstrap/FreeRealms.Bootstrap.csproj
```

Choose the local `NPUA30048` client directory. The application reads the manifest from `USRDIR\Digests\NPUA30048.soe`; no original assets are packaged.

**Default addresses:** Bind `127.0.0.1`, advertise `127.0.0.1`, HTTP `80`, optional DNS `53`. Binding to privileged or reserved ports may need administrator permissions or an appropriate URL reservation. For separate LAN devices, use `0.0.0.0` only as a bind address and your actual reachable IPv4 as the advertised address.

## Current limitations

- HTTP includes AppConfig and digest handling, but **asset codec/decompression responses are intentionally unimplemented** in this first C# preview.
- The built-in DNS responder currently answers only selected Free Realms hostnames; it **does not forward unrelated DNS queries**. Use it only in an isolated test environment or disable DNS.
- The **Check GitHub** button opens the project's Releases page. **Automatic downloading and installing of updates is not implemented yet**; this is a follow-up feature.
- CI is configured to publish a self-contained Windows executable and package it with **Inno Setup**, using a GitHub Actions artifact rather than a public GitHub Release.
- No proprietary PS3 game code or assets are distributed.

## Build pipeline

`windows-build.yml` publishes the WPF application for `win-x64`, invokes the Inno Setup script in `installer/FreeRealmsBootstrap.iss`, and uploads `FreeRealmsPS3Bootstrap-Setup.exe` as a workflow artifact.

For safe iteration, leave the original Python bootstrap and verified client snapshot unchanged until equivalent HTTP/DNS traces and installer state transitions are reproduced by this C# version.

## v0.3.0 — Python baseline inside the manager

The Windows manager now launches the preserved `bootstrap/fr_ps3_bootstrap.py` as a hidden child process, rather than using the incomplete C# DNS/HTTP engine. The Python script is included in the installer/publish output automatically. Its stdout and stderr are displayed inside the manager's live activity panel, and **Stop Services** terminates the owned Python process. This is intended to reproduce the research baseline, but has not yet been validated against RPCS3.

**Requirement:** Python 3 must already be installed and discoverable as `python` or `py`. The installer does not bundle the Python runtime. Administrator rights may be required to bind ports 53 and 80. Close any pre-existing Python bootstrap or other service on these ports before starting the manager.

**Network mode:** use a reachable LAN address for the advertised IP when RPCS3 is not using host loopback. `0.0.0.0` is a bind address only.

**Updates:** GitHub Actions generates a downloadable artifact when a build succeeds. The app's release-based updater cannot anonymously access releases from a private repository; download successful Actions artifacts manually until an authenticated channel is available.
