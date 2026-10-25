# Contributing

Thanks for helping. This project documents what the PS3 *Free Realms* client does, so the most useful contribution is a
**finding someone else can reproduce**.

## Before you share anything

> [!WARNING]
> Never commit or attach game files: no `game.self`, `game.elf`, `.pack`, `.zs`, `.dat`, manifests, digests (`.soe`),
> keys or licence files (`.rap`, `.rif`, `.edat`), save data, or full RPCS3 logs from your own machine. The `.gitignore`
> blocks the usual names, but you are responsible for what you push.

Redact logs before posting them: remove your user name, local paths, IP addresses and account or ticket values.

## Reporting a finding

Open an [issue](https://github.com/DeanSawyer/FreeRealms-PS3-Research/issues/new/choose) using the template. A good report has:

1. **Environment** — RPCS3 version, title ID and build (`NPUA30048`, 1.77), the command you ran, relevant settings.
2. **Inputs** — request paths, packet opcodes or addresses involved (hashes instead of content).
3. **Procedure** — the exact steps to repeat it.
4. **Result** — timestamps, log lines, screen state; what the client did next.
5. **Interpretation** — facts first, then hypotheses, clearly labelled as such.

## Changing code or docs

* Keep a change to one topic; explain *why* in the pull request.
* **Evidence over guesses.** A statement about the client needs a binary address (`game.elf` vaddr) or a test run behind it.
  Mark anything else as a hypothesis. If you add an address to the notes, add it to
  [`docs/re/addresses.csv`](docs/re/addresses.csv) with a text check.
* Put findings in the document of their topic (see the [docs index](docs/README.md)); put new tools in `tools/` with a
  docstring that says what they read and that they are read-only, and add a row to [`tools/README.md`](tools/README.md).
* Python 3.10+, standard library only unless there is a strong reason; no network access in tools that read an install.
* Before you push, run:

  ```bash
  python tools/check_docs.py                       # links and referenced paths
  python -m compileall -q bootstrap tools          # syntax
  ```

* Commit messages: a short imperative summary line, then a paragraph on what changed and why.

## What we will not accept

Game content or keys of any kind, code copied from a source with an incompatible licence, and claims about the client that
cannot be traced to an address or a recorded run.

## Questions

Open an issue labelled **question**. Please say what you already tried.
