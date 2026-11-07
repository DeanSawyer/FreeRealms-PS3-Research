# Contributing

Thanks for helping. This project documents what the PS3 *Free Realms* client does and answers it with a C# server, so the most
useful contributions are a **finding someone else can reproduce** and a **change that keeps the server's tests green**.

## Before you share anything

> [!WARNING]
> Never commit or attach game files: no `game.self`, `game.elf`, `.pack`, `.zs`, `.dat`, manifests, digests (`.soe`),
> keys or licence files (`.rap`, `.rif`, `.edat`), save data, or full RPCS3 logs from your own machine. The `.gitignore`
> blocks the usual names, and CI fails when one is tracked, but you are responsible for what you push.

Redact logs and test reports before posting them: remove your user name, local paths, IP addresses, online id and ticket values.

## Reporting a finding

Open an [issue](https://github.com/DeanSawyer/FreeRealms-PS3-Research/issues/new/choose) using the template. A good report has:

1. **Environment** — RPCS3 version, title ID and build (`NPUA30048`, 1.77), the manager or console settings you used.
2. **Inputs** — request paths, packet opcodes or addresses involved (hashes instead of content).
3. **Procedure** — the exact steps to repeat it.
4. **Result** — timestamps, log lines, screen state; what the client did next. A test-run zip from the Server Manager has all of it.
5. **Interpretation** — facts first, then hypotheses, clearly labelled as such.

## Changing code or docs

* Keep a change to one topic; explain *why* in the pull request.
* **Evidence over guesses.** A statement about the client needs a binary address (`game.elf` vaddr) or a test run behind it.
  Mark anything else as a hypothesis. If you add an address to the notes, add it to
  [`docs/re/addresses.csv`](docs/re/addresses.csv) with a text check.
* Put findings in the document of their topic (see the [docs index](docs/README.md)).
* **C# only.** The server follows the Sanctuary solution's conventions: `net9.0` for the library, central package management,
  MSTest, file-scoped namespaces, nullable enabled. The Server Manager is WPF on `net10.0-windows`.
* **The recorded vectors are the contract.** `*Vectors.g.cs` hold what the server sends to real client packets. If your change
  is meant to alter that, update the affected vectors in the same pull request and say so; otherwise a failing vector test means
  the change broke something.
* Before you push, run:

  ```powershell
  dotnet test src\ps3\Sanctuary.Ps3.slnx
  dotnet build src\FreeRealms.Bootstrap      # on Windows, when you touch the manager
  ```

* Commit messages: a short imperative summary line, then a paragraph on what changed and why.

## License of contributions

By submitting a change you agree that it is licensed under the repository's license, **AGPL-3.0-or-later** (see [`LICENSE`](LICENSE)).

## What we will not accept

Game content or keys of any kind, code copied from a source with an incompatible licence, and claims about the client that
cannot be traced to an address or a recorded run.

## Questions

Open an issue labelled **question**. Please say what you already tried.
