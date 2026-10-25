# Credits

This project stands on other people's work. Thank you.

* **OSFR and the Sanctuary / Clover server.** The C# *Free Realms* server emulator (Sanctuary, the solution behind Clover) is where the
  packet layouts come from: the gateway login packet, `AddPc`, the lobby and queue replies and many opcode numbers in
  [`docs/protocol/gateway.md`](docs/protocol/gateway.md) are named after and compared with it, and the C# port in
  [`src/ps3`](src/ps3/README.md) follows its conventions (target framework, central package management, MSTest, file-scoped
  namespaces) so it can be moved into that solution as a second client option. The PS3 client (build 1.77) is much older than the PC
  client Clover serves, and every difference found is written down next to the layout it concerns. Clover is licensed AGPL-3.0, which is
  why this repository is AGPL-3.0-or-later too.
* **RPCS3.** The emulator in which every test was run. [`tools/undat.py`](tools/undat.py) and
  [`tools/sdat_patch_loading.py`](tools/sdat_patch_loading.py) are derived from it (GPL-2.0-or-later) and use only the generic container
  constants published in its source.
* **Sony Online Entertainment / Daybreak Game Company** made *Free Realms*. This is an unofficial, non-commercial preservation effort,
  not affiliated with or endorsed by them.
* **ZLibDotNet**, used by the C# port because .NET's built-in zlib stream does not reproduce Python's zlib output byte for byte
  ([package](https://www.nuget.org/packages/ZLibDotNet)).
