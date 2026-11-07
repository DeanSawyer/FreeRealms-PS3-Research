# Credits

This project stands on other people's work. Thank you.

* **Character-creator mapping tables.** `src/ps3/Sanctuary.Ps3/Gateway/CharacterMappings.g.cs` holds Sanctuary's
  `Resources/CharacterCreate/*Mappings.txt` unchanged: which head, hair, skin tone, face paint and body option each creator choice means.
* **Item classes and categories.** `src/ps3/Sanctuary.Ps3/Data/ItemClasses.txt`, `ItemCategories.txt` and `ItemCategoryGroups.txt` are
  Sanctuary's `Resources` files, unchanged.
* **Item definitions and starter items.** `src/ps3/Sanctuary.Ps3/Data/item_definitions.tsv.gz` is a subset of Sanctuary's
  `Resources/ClientItemDefinitions.json` (every class 1 clothing item and every job's default items); the jobs and their default
  items in `Gateway/Jobs.cs` come from its `Resources/Profiles.json`. The Sanctuary flow, the inventory handlers and the reference
  data follow Sanctuary.Gateway's handlers one for one.
* **OSFR and the Sanctuary / Clover server.** The C# *Free Realms* server emulator (Sanctuary, the solution behind Clover) is where the
  packet layouts come from: the gateway login packet, `AddPc`, the lobby and queue replies and many opcode numbers in
  [`docs/protocol/gateway.md`](docs/protocol/gateway.md) are named after and compared with it, and the C# port in
  [`src/ps3`](src/ps3/README.md) follows its conventions (target framework, central package management, MSTest, file-scoped
  namespaces) so it can be moved into that solution as a second client option. The PS3 client (build 1.77) is much older than the PC
  client Clover serves, and every difference found is written down next to the layout it concerns. Clover is licensed AGPL-3.0, which is
  why this repository is AGPL-3.0-or-later too.
* **RPCS3.** The emulator in which every test was run. Two research helpers on the `python-archive` branch,
  [`tools/undat.py`](https://github.com/DeanSawyer/FreeRealms-PS3-Research/blob/python-archive/tools/undat.py) and
  [`tools/sdat_patch_loading.py`](https://github.com/DeanSawyer/FreeRealms-PS3-Research/blob/python-archive/tools/sdat_patch_loading.py), are derived from it (GPL-2.0-or-later) and use only the generic container
  constants published in its source.
* **Sony Online Entertainment / Daybreak Game Company** made *Free Realms*. This is an unofficial, non-commercial preservation effort,
  not affiliated with or endorsed by them.
* **ZLibDotNet**, used by the server because .NET's built-in zlib stream does not produce the exact output the client's length check expects
  ([package](https://www.nuget.org/packages/ZLibDotNet)).
