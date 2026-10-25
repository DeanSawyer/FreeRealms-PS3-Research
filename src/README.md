# Windows manager (C# preview)

A **.NET 10 / WPF** desktop wrapper around the PS3 bootstrap server: editable settings
(client folder, bind and advertised address, port, DNS on/off, describe local files in the digest), **Start/Stop** buttons and a live
log window. It runs the C# server ([`ps3/`](ps3/README.md)) inside the manager; a checkbox switches to the Python
[reference server](../bootstrap/README.md), which needs Python 3 on `PATH`.

> [!WARNING]
> **Preview.** The installer builds successfully in CI, but the manager has not been validated end to end against RPCS3.
> It does not pass `--digest` or `--extra-client`; for a full test use [`tools/run_test.py`](../tools/README.md) (Python server).

## Requirements

* Windows. Python 3 on `PATH` (`python` or `py`) is needed only for the Python reference server option; the installer does not bundle it.
* Administrator rights to bind ports 53 and 80; close any other service using them first.
* Your own `NPUA30048` client folder. No game files are packaged.

## Download a CI installer

1. Open the [Windows installer workflow](https://github.com/DeanSawyer/FreeRealms-PS3-Research/actions/workflows/windows-build.yml).
2. Open a **successful** run and download the artifact `FreeRealmsPS3Bootstrap-Windows-Setup`.
3. Extract the ZIP and run `FreeRealmsPS3Bootstrap-Setup.exe`.

GitHub requires repository access and a login to download artifacts. If there is no successful run, no installer is available.

## Run from source

With the .NET 10 SDK:

```powershell
dotnet run --project src/FreeRealms.Bootstrap/FreeRealms.Bootstrap.csproj
```

Choose the local `NPUA30048` folder. The manager reads the digest from `USRDIR\Digests\NPUA30048.soe`.

**Addresses.** Defaults are bind `127.0.0.1`, advertise `127.0.0.1`, HTTP `80`, DNS `53` (optional). When RPCS3 or a PS3 reaches the
server over the network, bind `0.0.0.0` and advertise your real LAN IPv4; `0.0.0.0` is a bind address only.

## How it works

`NativeServerHost.cs` starts `Sanctuary.Ps3` in the manager's own process and shows its log in the panel; the trace files are written to
`%LOCALAPPDATA%\FreeRealmsPS3Bootstrap\traces`. With the Python option, `PythonProcessHost.cs` launches the bundled script as a hidden
child process instead; **Stop** ends it. All Python modules of `bootstrap/` are copied into the publish output by
`FreeRealms.Bootstrap.csproj`.

## Limitations

* **Check GitHub** looks at the latest GitHub Release, and if it is newer offers to download `FreeRealmsPS3Bootstrap-Setup.exe`
  (HTTPS from github.com only), checks its SHA-256 when GitHub supplies one, and runs it. It needs a Release built from a `v*` tag
  and a repository readable without login; it reports "unavailable" if no release has been published or the repository is private.
* The C# server has been checked against the Python one by tests, not yet as often against the real PS3 client; if a run works with the
  Python option and not with the default, please report it (see [`ps3/README.md`](ps3/README.md#differences-from-the-python-script)).

## Build pipeline

[`windows-build.yml`](../.github/workflows/windows-build.yml) publishes the app for `win-x64`, runs the Inno Setup script
[`installer/FreeRealmsBootstrap.iss`](../installer/FreeRealmsBootstrap.iss) and uploads `FreeRealmsPS3Bootstrap-Setup.exe` as a
workflow artifact (and as a release asset when a `v*` tag is pushed).
