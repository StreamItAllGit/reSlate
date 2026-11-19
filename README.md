<div align="center">

<img src="Dev/assets/reStation.png" alt="reSlate Logo" width="96" />

# reSlate

Clean-room SlateOne (MIPS32 R3000A) static recompilation engine, `.decomp` lifter, player-PC local AOT compiler, and modular runtime for `reEngine` (formerly `reStation`; repository path `Personal/reStation`).

![Version](https://img.shields.io/badge/version-0.2.8--beta-blue?style=flat) ![Target](https://img.shields.io/badge/next-v0.3.0--public--beta-teal?style=flat) ![License](https://img.shields.io/badge/license-MPL--2.0-green?style=flat) ![Runtime](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey?style=flat)

**Active Milestone (0.3.0):** `▓▓▓▓░░░░░░` **45% toward v1.0.0** — `v0.2.8` shipped baseline (local AOT cache, `OpenGL 3.3` + `Software` backends, `16:9` Hor+ wings, multi-bind controls, `184/184` passing unit tests); `v0.3.0` completes the `reSlate` public-release transition alongside `reEngine v0.3.0` integration, `Vulkan` + deterministic `ParallelCpu`, memory card persistence, enhancement SDK hooks, clean-source decomp verification, and sandbox E2E certification

</div>

---

## About

`reSlate` decouples clean-room `SlateOne` (`R3000A` + `GTE` COP2) reverse engineering from end-user gameplay—operating strictly as a static AOT recompiler, optional Shared-IR runtime translator, and clean-room HLE hardware runtime rather than a generic compatibility emulator. Routed side by side by [reEngine](https://github.com/StreamItAllGit/reEngine) with zero background daemons (`AllowAutoStart = false`), it turns clean `.recomp` packages and verified user-supplied media (`data\rom\`) into locally compiled native `.NET 8.0` assemblies (`<dataDir>\cache\<Game>.Generated.dll`) on first launch.

Starting at `v0.3.0`, release binaries, engine IDs (`reSlate`), side-by-side install folders (`%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v<Ver>\`), and documentation adopt **`reSlate`**, while bidirectional `reSlate` / `reStation` alias resolution in `reEngine v0.3.0` and preserved `reStation.{Contracts,Core}` assembly identities maintain full compatibility with existing `0.1.x` and `0.2.x` modules.

## Key Features

- **Clean-Room `.decomp` & `.slaspec` Pipeline (`reSlate.Decomp`):** Custom `MPL-2.0` SLEIGH `R3000A + GTE` specification, executable/overlay discovery, C/TIM/VAB/SEQ exporters, and zero-retail clean-room validators (`0` bundled game assets, vendor BIOS images, or pre-generated `*.Generated.dll` binaries).
- **Local Ahead-of-Time Cache (`<dataDir>\cache\`):** Verifies BYO optical/archive media (`.chd`, `.iso`, `.bin`+`.cue`, `.7z`, `.zip`) via SHA-256, lifts recipe-ordered executables and `<container>#<slice>` overlays locally, and runs them inside a leak-free collectible `AssemblyLoadContext`.
- **Opt-In Tiered IR & Zero-RWX Runtime Fallback:** Supports optional `CoreCLR` RyuJIT (`--jit-bringup`, outputting clean structural `bringup.profile.json` graphs with an infinite-spin watchdog) and pre-decoded Zero-RWX `ThreadedIrBackend`, guarded by `4 KB` page and `64 B` cacheline epochs.
- **Hardware & Software Rendering:** Live frame-boundary switching between `OpenGL 3.3 Core` and the deterministic `Software` parity backend, unstretched Hor+ `16:9` widescreen geometry (auto-centering `4:3` for 24-bit MDEC FMVs), PGXP sub-pixel precision, and an in-game `F3` telemetry strip (`v0.3.0` adds `Vulkan` and multithreaded `ParallelCpu`).
- **3-Tier Rebindable Input & Kernel HLE:** Simultaneous 3-slot keyboard/mouse (`1001`–`1007`) and gamepad (`2001`–`2032`) bindings inside an interactive `F1` slide-over drawer, module-declared `ControlOptions`, clean-room A0/B0/C0 HLE kernel/SPU/CD-ROM/MDEC timing, and VBlank-aligned `F5`/`F9` quick-saves.

## Getting Started

### Prerequisites

- **Windows 10 / 11 (`win-x64`)**
- **.NET 8.0 SDK / Desktop Runtime** *(the signed Setup EXE bundles required runtime libraries; portable ZIP and source builds require .NET 8.0)*

### Releases & Downloads

Beginning with `v0.3.0`, every release ships as a 7-file package across GitHub (`--prerelease` while in `0.x` Beta), Google Drive, Discord announcements, and the project hub:

- **`reSlate-Setup-v<Version>-win-x64.exe`** — Signed per-user setup wizard (`Lightweight` player + local AOT or `Full` offline `.decomp` toolchain integration, installing under `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v<Version>\`).
- **`reSlate-v<Version>-win-x64-Portable.zip`** — Standalone portable bundle containing staged binaries, `.ready` engine marker, license notices, and sanitized docs with zero setup executable inside.

*Current shipped baseline is `v0.2.8` (packaged under legacy name `reStation`); the `v0.3.0` public beta repository (`https://github.com/StreamItAllGit/reSlate/releases`) opens upon completion of pre-public history review.*

### Quick Try

```powershell
# Launch a verified clean-room .recomp module with user-supplied disc media (example: SLUS-00158)
reSlate.exe --module .\modules\crowndepths-forsakenisle-slateone-02\module.recomp.json --disc "D:\Media\CrownDepths-ForsakenIsle.chd"

# Run non-interactive verification checks in an isolated data directory
reSlate.exe --verify --frames 240 --data-dir .\tmp-data --log-dir .\tmp-logs
```

<details>
<summary>Developer verification, CLI overrides, exit codes, and sandbox testing flags</summary>

```powershell
# Run the isolated unit test suite with warnings treated as errors
$env:APP_ISOLATED_TEST = "1"
dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true

# Install silently via the Setup executable into an isolated test root
reSlate-Setup-v0.3.0-win-x64.exe --profile Lightweight --reengine-dir "%LOCALAPPDATA%\Programs\StreamItAll\reEngine" --silent
```

| CLI Flag | Purpose |
| :--- | :--- |
| `--module <path>` | Load an unpacked `.recomp` folder, `module.recomp.json`, or single-file ZIP `.recomp` cartridge. |
| `--disc <path>` / `--preload-media <path>` | Verify and extract BYO optical/archive media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`). |
| `--backend <opengl\|software\|vulkan\|parallelCpu>` | Select render backend (`vulkan`, `parallelCpu`, and `--require-backend` land in `v0.3.0`). |
| `--fps-mode <original\|enhanced>` | Run native hardware cadence or interpolated `60–240+ Hz` presentation (`--target-fps`, `--vsync`). |
| `--profile <Lightweight\|Full>` / `--reengine-dir` | Choose installer/runtime profile (`Lightweight` player vs. `Full` offline toolchain) and custom `reEngine` path. |
| `--jit-bringup` / `--force-zero-rwx` / `--execution-tier` | Opt-in Tier 3 bring-up profiler (`bringup.profile.json`) or zero-RWX Threaded-IR diagnostic mode. |
| `--verify` / `--doctor` / `--tray-only` / `--wait` | Run non-interactive health checks (`--doctor` alias in `v0.3.0`), offscreen tray checks, or synchronous wait mode. |
| `--data-dir` / `--config` / `--log-dir` / `--save-dir` | Redirect writable runtime storage, `settings.json`, logs, and per-game save directories (`--save-dir` in `v0.4.0`). |
| `--automation-port` / `--automation-token` | `Debug` and `-test.*` sandbox automation interface only (`Release` builds exclude automation and exit `2`). |

**Exit Codes:** `0` Success · `1` Runtime or `--verify` fault · `2` CLI error / disallowed Release automation flag · `3` Path containment fault · `4` Disc/archive verification error · `5` `--jit-bringup` spin watchdog · `6` Unavailable `--require-backend` (`v0.3.0`) · `130` Cancelled (`SIGINT`).

</details>

## Project Structure

- `Dev/` — `.NET 8.0` solution (`Dev\src\reStation.slnx`), tests, build/sanitization scripts, IDE workspace (`Dev\reStation.code-workspace`), and technical manuals (`Dev\docs\`)
- `Builds/` — Local compiled staging builds (`v<Version>\`)
- `Releases/` — 7-file versioned distribution bundles (`Setup.exe`, `Portable.zip`, docs, checksums)
- `Archive/` — Compressed source history snapshots

## Documentation

- [Features](FEATURES.md) — Current capabilities and source mapping
- [Roadmap](ROADMAP.md) — Active `v0.3.0` deliverables and long-term milestones
- [Changelog](CHANGELOG.md) — Version history ledger
- [Security Policy](SECURITY.md) — Supported tracks, containment boundaries, and vulnerability reporting
- [Contributing](CONTRIBUTING.md) — Local test setup, PR checklist, and clean-room rules
- [User Guide](Dev/docs/USER_GUIDE.md) — End-user installation, controls, and troubleshooting (ships as `Releases\README.md`)
- [Architecture](Dev/docs/ARCHITECTURE.md) — Assembly graph, data flows, file index, and design decisions
- [Interfaces](Dev/docs/INTERFACES.md) — Public `.NET` contracts, CLI surface, and manifest schemas
- [Operations](Dev/docs/OPERATIONS.md) — Build, sandbox verification, logs, and release procedures

## License & Legal

Distributed under the **Mozilla Public License 2.0 (`MPL-2.0`)** ([LICENSE](LICENSE)). Embedded container extraction helpers (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` (`LICENSE-7ZIP.txt` in `app\`). See [SECURITY.md](SECURITY.md) for containment guarantees and private vulnerability reporting.

**Clean-Room Policy & Non-Affiliation Disclaimer:** `reSlate` distributes zero copyrighted game assets, PlayStation (`PS1`) BIOS ROMs, or Sony proprietary code. End users provide their own legally dumped disc media for use with clean-room `.recomp` modules. *PlayStation*, *PS1*, *DualShock*, and *PocketStation* are registered trademarks of Sony Interactive Entertainment Inc.; `reSlate` is an independent clean-room project with zero affiliation or endorsement.
