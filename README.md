<div align="center">

<img src="Dev/assets/reSlate.png" alt="reSlate Logo" width="96" />

# reSlate

Clean-room SlateOne (MIPS32 R3000A) static recompilation engine, `.decomp` lifter, player-PC local AOT compiler, and modular runtime for `reEngine` (formerly `reStation`; repository path `Personal/reStation`).

![Version](https://img.shields.io/badge/version-0.3.0--hf.2--beta-blue?style=flat) ![Next](https://img.shields.io/badge/next-v0.3.1-teal?style=flat) ![License](https://img.shields.io/badge/license-MPL--2.0-green?style=flat) ![Runtime](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey?style=flat)

**Active Milestone (`v0.3.0-hf.2` Public Beta -> `v0.3.1`):** `▓▓▓▓▓▓░░░░` **56% toward v1.0.0** — `v0.3.0-hf.2` fixes `TryAdvanceToNextVBlank()` `< 64`-cycle `Math.Clamp` crashes, separates `30 FPS` decimation and inter-VBlank `60–240+ / Max` OpenGL presentation (`PresentationFpsTracker`, `"VSync (Caps to Monitor Hz)"`, locked `60 Hz` VBlank/SPU audio), enables full controller + `LT`/`RT` trigger rebinding & `0` clearability across all 3 tiers (`Global 1:1`, `Game 1:1`, `Modern PC` with `NativeGameActionContext` masks), fixes `libmcrd` async memory-card I/O + `ToT[10]` snapshot restoration, adds `DebugAutomationDiagnostics`, and passes `277/277` dual Release + Debug unit tests (`0` warnings) atop `v0.3.0`/`-hf.1`; `v0.3.1` next adds shared `reEngine.Host` `Vulkan`, `ParallelCpu`, Enhancement SDK, Fast-Forward, and Cutscene Skip.

</div>

---

## About

> **Beta (Pre-1.0):** `reSlate` (`v0.3.0-hf.2`) is an active pre-1.0 public beta release targeting `v1.0.0` desktop stability and `v2.0.0` cross-platform support.

`reSlate` decouples clean-room `SlateOne` (`R3000A` + `GTE` COP2) reverse engineering from end-user gameplay—operating strictly as a static AOT recompiler, native Tier 3 Shared-IR/RyuJIT runtime translator, and clean-room HLE hardware runtime rather than a generic compatibility interpreter. Launched side by side by [reEngine](https://github.com/StreamItAllGit/reEngine) with zero background daemons (`AllowAutoStart = false`), it converts clean `.recomp` packages and verified user-supplied media (`data\rom\`) into locally compiled native `.NET 8.0` assemblies (`<dataDir>\cache\<Game>.Generated.dll`) on first launch.

With `v0.3.0-hf.2` (Beta, Pre-1.0), binaries, installer outputs, engine IDs (`reSlate`, `appId: "reslate"`), discovery markers (`.ready`), side-by-side install folders (`%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.0-hf.2\`), and downstream SDK drops (`SharedLibs\reSlate\v0.3.0-hf.2\`) adopt **`reSlate`**. Bidirectional `reSlate` / `reStation` alias resolution in `reEngine v0.3.0` and preserved `reStation.{Contracts,Core}` assembly identities maintain full ABI compatibility with existing `0.1.x` and `0.2.x` modules.

## Key Features

- **Clean-Room `.decomp` & `.slaspec` Pipeline (`reSlate.Decomp.dll`):** Custom `MPL-2.0` SLEIGH `R3000A + GTE` specification, executable/overlay discovery, C/TIM/VAB/SEQ exporters, and clean-source verification enforcing `-Wall -Wextra -Wno-unused-parameter` (`0` warnings), zero `-Wno-everything` suppressions, six decompiler-tell checks, cohesive C modules, and zero bundled retail media or `*.Generated.dll` binaries.
- **Local Ahead-of-Time Cache (`<dataDir>\cache\`) & Live Tier 3 Native JIT/IR:** Verifies BYO optical/archive media (`.chd`, `.iso`, `.bin`+`.cue`, `.7z`, `.zip`) via SHA-256, lifts recipe-ordered executables and `<container>#<slice>` overlays locally via `reSlate.Compiler.exe` into a collectible `AssemblyLoadContext`, and executes unlifted or bring-up code natively via live `CoreCLR` RyuJIT and pre-decoded Zero-RWX `ThreadedIrBackend` guarded by `4 KB` page and `64 B` cacheline epochs (`staticAotOnly` fails closed; `PsxInterpreter` is quarantined to `--execution-tier interpreterOracle`; `--jit-bringup` emits canonical `isPureStaticReady`).
- **Hardware & Software Rendering (`OpenGL 3.3` & `Software CPU`):** Live frame-boundary switching between `OpenGL 3.3 Core` (`ArrayPool` FIFO queue) and deterministic `Software` rendering, unstretched Hor+ `16:9` side wings (auto-centering `4:3` for 24-bit MDEC FMVs), PGXP sub-pixel precision, truthful `30 FPS` decimation & inter-VBlank `60–240+ / Max` OpenGL presentation (`PresentationFpsTracker`), `"VSync (Caps to Monitor Hz)"`, and the `F3` telemetry strip (`Vulkan`, `ParallelCpu`, Fast-Forward, Cutscene Skip, and shared `hdpacks\` / `ISubTickFrameHook` defer to `v0.3.1` pending shared `reEngine.Host` consolidation).
- **All-Tier Rebindable Input, Unified Memory Cards & `60 Hz` SPU Lock:** Full keyboard/mouse (`1001`–`1007`) and controller/`LT`/`RT` (`2001`–`2032`) rebinding with `0` clearability across `Global 1:1`, `Game 1:1`, and `Modern PC` inside the `F1` slide-over drawer (`SetModernControllerSlot` single-slot replacement and `NativeGameActionContext` masks), module-declared `ControlOptions`, single-edge menu tap isolation, unified Slot 1+2 `.mcr` persistence (`data\memcards\` via `SIO0 0x81`, BIOS `B0:0x4E..0x50` `_new_card`, async `libmcrd` `DeliverCardIoEnd`, and `ToT[10]` snapshot parity), `60 Hz` hardware VBlank + `44.1 kHz` SPU lock (`< 64`-cycle VBlank clamp fix), and VBlank-aligned `"RST1"`/`"RSTX"` `"MCD2"` quick-saves (`F5`/`F9`).
- **Canonical Config, Host Integration & Binary Purity:** Canonicalizes `<dataDir>\config\settings.json` (auto-migrating `<dataDir>\settings.json` non-destructively), aligns with `reEngine.Host.Contracts`, and ships pure `Release` binaries with zero automation references (`--automation-port` fails closed with `ExitCode = 2`; `Debug`/`RESTATION_TEST_AUTOMATION` exposes `DebugAutomationDiagnostics`).

## Getting Started

### Prerequisites

- **Windows 10 / 11 (`win-x64`)**
- **.NET 8.0 SDK / Desktop Runtime** *(the signed Setup EXE bundles required runtime libraries; portable ZIP and source builds require .NET 8.0)*

### Releases & Downloads

Every `0.x` beta release is published on [GitHub Releases](https://github.com/StreamItAllGit/reSlate/releases) (`--prerelease`) in 7-file distributions alongside Discord Dev Updates:

- **`reSlate-Setup-v0.3.0-hf.2-win-x64.exe`** — Signed per-user setup wizard (`Lightweight` player + local AOT or `Full` offline `.decomp` toolchain integration, automatically detecting existing `reSlate`/`reEngine`/`reStation` install folders and saved profiles on upgrade while rejecting populated `reEngine` roots so Setup targets `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.0-hf.2\` with a `.ready` discovery marker).
- **`reSlate-v0.3.0-hf.2-win-x64-Portable.zip`** — Standalone portable bundle containing staged binaries, `.ready` marker, license notices, and sanitized docs with zero setup executable inside.

*`v0.3.0-hf.2` (`277/277` dual Release + Debug unit tests, `0` warnings) is signed, sandbox-verified, and published to [GitHub Releases](https://github.com/StreamItAllGit/reSlate/releases) (subject to final maintainer release upload).*

### Quick Try

```powershell
# Launch a verified clean-room .recomp module with user-supplied disc media (example: SLUS-00158)
reSlate.exe --module .\modules\crowndepths-forsakenisle-slateone-02\module.recomp.json --disc "D:\Media\CrownDepths-ForsakenIsle.chd"

# Run non-interactive verification checks and host diagnostics in an isolated data directory
reSlate.exe --verify --frames 240 --data-dir .\tmp-data --log-dir .\tmp-logs
reSlate.exe --doctor --data-dir .\tmp-data --log-dir .\tmp-logs
```

<details>
<summary>Developer verification, CLI overrides, exit codes, and sandbox testing flags</summary>

```powershell
# Run the isolated unit test suite in both Release and Debug with warnings treated as errors (277/277 passing)
$env:APP_ISOLATED_TEST = "1"
dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true
dotnet test Dev\src\reStation.slnx -c Debug -p:TreatWarningsAsErrors=true

# Install silently via the Setup executable into an isolated test root
reSlate-Setup-v0.3.0-hf.2-win-x64.exe --profile Lightweight --reengine-dir "%LOCALAPPDATA%\Programs\StreamItAll\reEngine" --silent
```

| CLI Flag | Purpose |
| :--- | :--- |
| `--module <path>` | Load an unpacked `.recomp` folder, `module.recomp.json`, or single-file ZIP `.recomp` cartridge. |
| `--disc <path>` / `--preload-media <path>` | Verify and extract BYO optical/archive media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`). |
| `--backend <opengl\|software>` / `--require-backend <id>` | Select `opengl` or `software` renderer; `--require-backend vulkan\|parallelCpu` exits `6` (deferred to `v0.3.1`). |
| `--scale <1\|2\|4\|8>` | Set internal rendering scale (`1x`, `2x`, `4x`, or `8x`). |
| `--fps-mode <original\|enhanced>` / `--target-fps` / `--vsync` | Run native `Original (60/30)` cadence (`TargetFps=30` swap decimation with `60 Hz` SPU) or `Enhanced FPS` (`60–240+ Hz` / `unlocked`). |
| `--execution-tier <mode>` / `--jit-bringup` / `--force-zero-rwx` | Select `staticAotOnly`, `aotWithJitFallback` (default), `aotWithThreadedIr`, `jitBringup`, or quarantined `interpreterOracle`. |
| `--verify` / `--doctor` / `--tray-only` / `--wait` | Run non-interactive health checks, host diagnostics, offscreen tray checks, or synchronous Job-Object wait mode. |
| `--fixture` / `--dump-screenshot` / `--dump-audio` | Run synthetic fixture diagnostics or write frame PNG / audio WAV captures. |
| `--data-dir` / `--config` / `--log-dir` / `--save-dir` | Redirect writable runtime storage, `config/settings.json`, logs, and save directory (`--save-dir` router hook completes in `v0.4.0`). |
| `--automation-port` / `--automation-token` | `Debug` and `-test.*` sandbox automation interface only (`Release` builds exclude automation and exit `2`). |

**Exit Codes:** `0` Success · `1` Runtime or `--verify` fault · `2` CLI error / disallowed Release automation flag · `3` Path containment fault · `4` Disc/archive verification error · `5` `--jit-bringup` spin watchdog · `6` Unavailable or `v0.3.1`-deferred `--require-backend` · `130` Cancelled (`SIGINT`).

</details>

## Project Structure

- `Dev/` — `.NET 8.0` solution (`Dev\src\reStation.slnx`), tests, build/sanitization/E2E scripts, IDE workspace (`Dev\reStation.code-workspace`), and technical manuals (`Dev\docs\`)
- `Builds/` — Local compiled staging builds (`v<Version>\`)
- `Releases/` — 7-file versioned distribution bundles (`Setup.exe`, `Portable.zip`, docs, checksums)
- `Archive/` — Compressed source history snapshots

## Documentation

- [Features](FEATURES.md) — Current capabilities and source mapping
- [Roadmap](ROADMAP.md) — Active `v0.3.0-hf.2` completion status and `v0.3.1`–`v2.0.0` milestones
- [Changelog](CHANGELOG.md) — Version history ledger
- [Security Policy](SECURITY.md) — Supported tracks, containment boundaries, and vulnerability reporting
- [Contributing](CONTRIBUTING.md) — Local test setup, PR checklist, and clean-room rules
- [User Guide](Dev/docs/USER_GUIDE.md) — End-user installation, controls, and troubleshooting (ships as `Releases\README.md`)
- [Architecture](Dev/docs/ARCHITECTURE.md) — Assembly graph, data flows, file index, and design decisions
- [Interfaces](Dev/docs/INTERFACES.md) — Public `.NET` contracts, CLI surface, and manifest schemas
- [Operations](Dev/docs/OPERATIONS.md) — Build, sandbox verification, logs, and release procedures

## Code Signing Policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

- **Authors:** Repository contributors and maintainers ([`@StreamItAllGit`](https://github.com/StreamItAllGit))
- **Reviewers:** Repository maintainers ([`@StreamItAllGit`](https://github.com/StreamItAllGit))
- **Approvers:** Repository owners ([`@StreamItAllGit`](https://github.com/StreamItAllGit))
- **Privacy Policy:** This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

## License & Legal

Distributed under the **Mozilla Public License 2.0 (`MPL-2.0`)** ([LICENSE](LICENSE)). Embedded container extraction helpers (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` (`LICENSE-7ZIP.txt` in `app\`). See [SECURITY.md](SECURITY.md) for containment guarantees and private vulnerability reporting.

**Clean-Room Policy & Non-Affiliation Disclaimer:** `reSlate` distributes zero copyrighted game assets, PlayStation (`PS1`) BIOS ROMs, or Sony proprietary code. End users provide their own legally dumped disc media for use with clean-room `.recomp` modules. *PlayStation*, *PS1*, *DualShock*, and *PocketStation* are registered trademarks of Sony Interactive Entertainment Inc.; `reSlate` is an independent clean-room project with zero affiliation or endorsement.
