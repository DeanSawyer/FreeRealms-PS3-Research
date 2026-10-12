<div align="center">

# Free Realms
### PlayStation 3 Research & Preservation

**Independent technical research into the PlayStation 3 release of Free Realms**

[![Project Status](https://img.shields.io/badge/status-active%20research-2563eb?style=flat-square)](https://github.com/DeanSawyer/FreeRealms-PS3-Research)
![Platform](https://img.shields.io/badge/platform-PlayStation%203-374151?style=flat-square)
![Title ID](https://img.shields.io/badge/title%20ID-NPUA30048-374151?style=flat-square)
![Preservation](https://img.shields.io/badge/focus-digital%20preservation-16803c?style=flat-square)

[Overview](#overview) · [Research Status](#research-status) · [Technical Findings](#technical-findings) · [Roadmap](#research-roadmap) · [Contributing](#contributing)

</div>

---

## Overview

**FreeRealms-PS3-Research** is an independent preservation and reverse-engineering initiative focused on understanding the original PlayStation 3 edition of *Free Realms*, the online multiplayer game developed by Sony Online Entertainment.

The project studies client initialization, legacy service interactions, the original patching and asset installation system, and the technical dependencies required to reproduce observable startup behavior in a controlled research environment.

Our approach is **evidence-driven and reproducible**: preserve original observations, isolate variables, document unsuccessful tests, and distinguish confirmed behavior from hypotheses.

> [!IMPORTANT]
> **This is a research repository, not a playable game release.** Full client operation, asset installation, and connection to a replacement game server have **not** been demonstrated.

## Research Scope

| Workstream | Objective |
| :--- | :--- |
| **Client initialization** | Document boot sequencing, module loading, installer lifecycle, and transition points. |
| **Network services** | Investigate the startup configuration, DNS dependencies, HTTP endpoints, and protocol expectations. |
| **Patch delivery** | Characterize manifests, asset requests, validation fields, compression, and extraction behavior. |
| **Binary analysis** | Trace relevant behavior in the PS3 installer and other components using reproducible methods. |
| **Preservation tooling** | Build independently authored test servers, validation utilities, and diagnostic workflows. |
| **Technical documentation** | Maintain a clear record of confirmed findings, open questions, and experimental limitations. |

## Research Status

**Baseline recorded: October 12, 2026**

The current local test environment has demonstrated successful progression through the initial configuration and patch-manifest requests, followed by entry into the installer's asset-loading state.

| Component | Status | Evidence / qualification |
| :--- | :--- | :--- |
| Local DNS routing | **Observed working** | Selected legacy hostnames resolved to an experimental local endpoint. |
| Startup configuration | **Observed working** | Client requested and received an AppConfig response. |
| Patch manifest retrieval | **Observed working** | HTTP requests for `FreeRealms.soe` completed. |
| Installer state progression | **Observed** | `init → status → statusDone → loading`. |
| Asset download and decode | **Blocked** | Client reported error `1004` and `Unknown codec reading`. |
| Complete game startup | **Not verified** | No successful end-to-end boot into a playable environment. |

### Preserved installer baseline

One captured RPCS3 session recorded these transitions:

```text
01:04.122  init       → status
01:04.282  status     → statusDone → loading
01:35.364  loading    → error

Installer error 1004: Unknown codec reading
```

**Observed loading interval: approximately 31.08 seconds.**

This measurement describes a particular test, **not** a guaranteed 30-second startup period or a successful asset installation. A separate local preservation snapshot contained **993 files**, whose SHA-256 hashes matched the then-current original files; that snapshot is not distributed here.

## Technical Findings

### 1. Legacy startup and patch workflow

The PS3 client was observed accessing a startup configuration endpoint and a patch manifest endpoint:

```text
GET  /app/appconfig.php?game=frps3&server=live
HEAD /patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe
GET  /patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe
```

The local service answered these requests, after which the installer advanced to its asset-loading phase. This validates a portion of the initial handshake only; it does **not** validate the remaining asset pipeline or game-server protocol.

### 2. Outstanding asset decoder failure

The installer requested an asset under the following path:

```text
/patch/freerealms/ps3scelive/ps3-web/FreeRealms.zs/
  art/environments/terraincalendardefinitions.txt.zs
```

The experimental response led to:

```text
ErrorID: 1004
ErrExtra: Unknown codec reading ...
```

Recorded local test values:

| Field | Value |
| :--- | :--- |
| Asset | `terraincalendardefinitions.txt` |
| Uncompressed test input | `403 bytes` |
| Calculated CRC32 | `0x912D54DE` |
| CRC32 decimal | `2435667166` |
| Experimental LZMA payload | `251 bytes` |
| Manifest-indicated compressed length | `373 bytes` |
| Outcome | **Rejected by the client** |

**Interpretation:** The test establishes a decoder compatibility failure, not the original format. The actual framing, codec identifier, and decompression rules for these patch assets remain under investigation. References to ZIP, zlib/Deflate, or LZMA must not be treated as proof of the wire format without an authenticated compressed sample or a verified decoder trace.

### 3. Binary investigation

Research also examines the original PS3 installer module to identify the code paths responsible for codec selection, error generation, and asset processing. Static observations should be accompanied by binary identity, addresses, cross-references, and runtime verification where possible.

No unmodified proprietary executable or original game asset is included in this repository.

## Research Roadmap

- [x] Establish local interception of selected startup service requests.
- [x] Capture a successful AppConfig and manifest exchange.
- [x] Record installer state transitions and the first blocking asset error.
- [x] Create and hash-verify a local baseline preservation snapshot.
- [ ] Document a minimal, repeatable local test setup using original research code.
- [ ] Identify and verify the PS3 patch asset container and codec.
- [ ] Reproduce successful decode and installation of a permitted test asset.
- [ ] Map subsequent client initialization and server handshake stages.
- [ ] Publish structured technical notes and regression tests.
- [ ] Evaluate progress toward a functional, independently operated preservation environment.

Completion marks reflect **specific observations**, not full implementation of a game feature.

## Repository Layout

The following structure is a **target organization** as publishable project material is developed; directories may not exist yet.

```text
FreeRealms-PS3-Research/
├── bootstrap/       # Independently developed local test services
├── docs/            # Research papers, findings, and protocol notes
├── diagnostics/     # Redacted logs and reproducible test fixtures
├── tools/           # Original inspection and verification utilities
└── README.md
```

Private preservation copies, emulator installations, original manifests, and proprietary game binaries remain outside the repository.

## Reproducibility Standards

Every new technical finding should, wherever practical, document:

1. **Environment:** emulator version, title ID, test setup, and relevant configuration.
2. **Inputs:** request paths, response characteristics, and hashes where permissible.
3. **Procedure:** exact sequence of actions needed to repeat the observation.
4. **Results:** timestamps, state changes, error messages, and measurable outcomes.
5. **Interpretation:** clearly separated facts, hypotheses, and remaining uncertainties.

Avoid publishing credentials, account identifiers, private addresses, or copyrighted file contents. Redact logs before sharing them.

## Contributing

Technical contributions and independently verifiable findings are welcome through [Issues](https://github.com/DeanSawyer/FreeRealms-PS3-Research/issues) and [Pull Requests](https://github.com/DeanSawyer/FreeRealms-PS3-Research/pulls), subject to repository access settings.

Particularly valuable contributions include decoder research, original patch-container analysis, documented PS3 control-flow investigation, independently authored tooling, and historical technical references with reliable provenance.

**Contribution principles:** provide supporting evidence, identify the software version or artifact analyzed, mark uncertain conclusions explicitly, and submit only material you are permitted to redistribute.

## Legal & Preservation Notice

*Free Realms* and related trademarks, game content, and intellectual property belong to their respective rights holders. This is an **unofficial, independent, noncommercial technical research effort** and is not affiliated with or endorsed by Sony Online Entertainment, Daybreak Game Company, Sony Interactive Entertainment, or any successor or related entity.

**No original game client, copyrighted asset archive, proprietary executable, encryption key, or copyrighted patch payload is supplied here.** Users and contributors must obtain any relevant software lawfully and comply with applicable law and permissions.

---

<div align="center">

**Preserve the evidence. Reproduce the behavior. Document what we learn.**

[View Research Repository](https://github.com/DeanSawyer/FreeRealms-PS3-Research)

</div>
