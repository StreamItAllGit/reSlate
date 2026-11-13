# reStation (`v0.1.0`)

- **Active Version:** `v0.1.0` (`2026-11-13`)
- **Lifecycle State:** `active` ([`active.state`](active.state))
- **Group / Path:** ``
- **Logo Assets:** [`Dev\assets\reStation.svg`](Dev/assets/reStation.svg) | [`Dev\assets\reStation.png`](Dev/assets/reStation.png) | [`Dev\assets\reStation.ico`](Dev/assets/reStation.ico)

---

## 1. What It Is & Core Goal
`reStation` is a reusable **PlayStation 1 (`PS1`) static recompilation engine, offline MIPS R3000A lifter, and modular BYO-media player**, routed via [`reEngine`](<PROJECTS_ROOT>/Personal/reEngine/README.md) and installed side-by-side under `<PROGRAMS_ROOT>\reEngine\engines\reStation\v<Ver>\` (with immutable SDK history in `<SHARED_LIBS_ROOT>\reStation\v<Ver>\`). Rather than duplicating CPU/GPU/SPU/CD-ROM/MDEC emulation and disc ingestion inside every `<Game>Recomp` repository, `reStation` hosts the shared platform backend (`Foundation.Core` + `Foundation.Retro`) and loads external `.recomp` game modules paired with the user's own legal disc/archive copy (preloaded in gitignored `data\rom\` or selected via `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`).

**Clean-Room & Zero-Asset Guarantee:** `reStation` distributes zero copyrighted Sony BIOS images, retail game assets, or title executables. Every title runs from user-supplied media verified via SHA-256 against its `.recomp` module manifest.

---

## 2. Quickstart & Sandbox CLI Flags

```powershell
reStation.exe --module .\modules\kings-field\module.recomp.json --disc "D:\Roms\PS1\Kings Field (USA).chd"
reStation.exe --test-mode --headless --frames 240 --data-dir .\Sandbox\data --log-dir .\Sandbox\logs
reStation.exe --test-mode --visible-test --data-dir .\Sandbox\data --log-dir .\Sandbox\logs
```

| CLI Flag | Purpose |
| :--- | :--- |
| `--module <path>` | Load an external `.recomp` module manifest + compiled game slice/patches. |
| `--disc <path>` | Extract and SHA-256-verify user optical/archive media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`). |
| `--fps-mode <original\|enhanced>` | Select native PS1 cadence or interpolated high-FPS presentation (`--target-fps`, `--vsync`). |
| `--backend <opengl\|software>` | Select `IRecompRenderBackend` (`OpenGL 3.3 Core` default or `SoftwareCpu` fallback/oracle). |
| `--data-dir` / `--config` / `--log-dir` | Redirect writable runtime data, `settings.json`, and telemetry logs (`--test-mode` fail-closed). |
| `--headless` / `--visible-test` | Automated CI testing (`--headless`) or short foreground showcase (`--visible-test`). |

---

## 3. Architecture At a Glance
- **Application Archetype:** Shared Platform Recomp Engine & Player (`Tier A`, consuming `Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`).
- **Graphics Contract:** Pluggable `IRecompRenderBackend` (OpenGL 3.3 Core + CPU Software fallback in `v0.x–v1.0.0`, extensible to `ParallelCpu` and `SDL3_GPU`).
- **Accuracy Targets:** Hardware 2x PS1 CD streaming (`150 sectors/s`), ADSR release + ring-buffered SPU/XA mixing (`44.1 kHz`), PGXP geometry stabilization, and multi-scene save-state/WAV/PNG parity harnesses.

---

## 4. Project Navigation & Living Docs
- **Feature Inventory & Code Map:** [`FEATURES.md`](FEATURES.md)
- **Milestones & Strategic Horizon:** [`ROADMAP.md`](ROADMAP.md)
- **Version History Ledger:** [`CHANGELOG.md`](CHANGELOG.md)
- **Blueprints (`@pland`):** [`Dev\docs\plans\0.1.0.2026-11-13.md`](Dev/docs/plans/0.1.0.2026-11-13.md)

---

## 5. Manual Shelf (`Dev\docs\`)
- [`ARCHITECTURE.md`](Dev/docs/ARCHITECTURE.md) | [`INTERFACES.md`](Dev/docs/INTERFACES.md) | [`USER_GUIDE.md`](Dev/docs/USER_GUIDE.md) | [`OPERATIONS.md`](Dev/docs/OPERATIONS.md)
