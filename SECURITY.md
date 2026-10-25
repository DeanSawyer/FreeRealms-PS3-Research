# Security

## Scope

This repository contains a **research test server** (`bootstrap/`) that redirects DNS and serves HTTP and UDP to a game
client on a test machine. It has no authentication and is **not** hardened for production or for the open internet.

* Run it only on a machine and network you control, with the firewall limited to the test client.
* Do not forward ports 53, 80 or 20260 to the internet.
* `0.0.0.0` is a bind address, not an address to give to a client.
* The trace files it writes (`*.jsonl`) can contain local addresses and paths; they are ignored by Git — keep them out of
  issues unless redacted.

## Reporting a problem in this project's code

If you find a way the tools could harm the machine they run on (for example a path or archive handled unsafely), please
**do not post exploit details in a public issue**. Use GitHub's *Report a vulnerability* option on the repository's
Security tab if it is enabled; otherwise open an issue that says only that you have a security report and ask for a
private channel.

Reports about the original game, its former servers, or third-party emulators are outside this project's scope.
