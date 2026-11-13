# reStation (`v0.1.0`)

- **Active Version:** `v0.1.0` (`2026-11-13`)
- **Lifecycle State:** `active` ([`active.state`](active.state))
- **Group / Path:** ``
- **Logo Assets:** [`Dev\assets\reStation.svg`](Dev/assets/reStation.svg) | [`Dev\assets\reStation.png`](Dev/assets/reStation.png) | [`Dev\assets\reStation.ico`](Dev/assets/reStation.ico)

---

## 1. What It Is & Core Goal
`reStation` is a reusable **PlayStation 1 (`PS1`) Static Recompilation Engine, Shared-IR Runtime JIT Binary Translator, Clean-Room HLE Runtime & Modular BYO-Media Player**—**not** a generic compatibility emulator. Routed via [`reEngine`](<PROJECTS_ROOT>/Personal/reEngine/README.md) and installed side-by-side under `<PROGRAMS_ROOT>\reEngine\engines\reStation\v<Ver>\` (with immutable SDK history in `<SHARED_LIBS_ROOT>\reStation\v<Ver>\`), `reStation` exists to unlock native C# gameplay/interpolation hooks (`60–240+ Hz` 3D on `30 Hz` ticks, `WASD` + raw mouse-look, `16:9`/`21:9` frustum expansion, PGXP precision, HD override hooks) atop ahead-of-time static `.recomp` translation and runtime CoreCLR RyuJIT dynamic translation—treating CPU interpretation strictly as a `[WARN]`-logged safety-net fallback and `SoftwareCpu` strictly as a deterministic parity test oracle.

Rather than duplicating shared ISA translation, HLE peripheral/BIOS services, and host integration inside every `<Game>Recomp` repository, `reStation` hosts the shared platform stack (`Foundation.Core` + `Foundation.Retro`) and loads external `.recomp` game modules paired with the user's own legal disc/archive copy (preloaded in gitignored `data\rom\` or selected via `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`).

**Clean-Room & Zero-Asset Guarantee:** `reStation` distributes zero copyrighted Sony BIOS images, retail game assets, or title executables. Every title runs from user-supplied media verified via SHA-256 against its `.recomp` module manifest (or verified clean-room HLE checks).

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
| `--backend <opengl\|software>` | Select `IRecompRenderBackend` (`v0.1.0`: `OpenGL 3.3 Core` default or `SoftwareCpu` parity oracle; expanding in `v0.3.0–v1.0.0` to `opengl` + `vulkan` + `parallelCpu` + `software`). |
| `--data-dir` / `--config` / `--log-dir` | Redirect writable runtime data, `settings.json`, and telemetry logs (`--test-mode` fail-closed). |
| `--headless` / `--visible-test` | Automated CI testing (`--headless`) or short foreground showcase (`--visible-test`). |

---

## 3. Architecture At a Glance
- **Application Archetype:** Shared Platform Recomp / Translation Engine (`Tier A`, consuming `Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`).
- **4-Tier Execution Stack (Built Toward `v1.0.0`):** **(1)** Native C# Hooks (`IGameFramePatch`) & Clean-Room BIOS/GTE/GP0 HLE $\rightarrow$ **(2)** Static AOT `.recomp` Assemblies (`IRecompGameModule`) $\rightarrow$ **(3)** Shared MIPS$\rightarrow$IR CoreCLR RyuJIT (`DynamicMethod`) + `4 KB`/`64B` epoch invalidation + `--jit-bringup` raw-disc profiler & Zero-RWX Threaded-IR fallback (`v0.2.0`) $\rightarrow$ **(4)** `[WARN]`-logged `PsxInterpreter` & `SoftwareCpu` oracle.
- **Graphics Contract (`v0.1.0` $\rightarrow$ `v1.0.0` Tri-Backend Law):** Pluggable `IPsxRenderBackend` (`OpenGL 3.3 Core` + `SoftwareCpu` parity oracle in `v0.1.0`; adding hardware `Vulkan` and deterministic tile/row-band multithreaded `ParallelCpu` by `v0.3.0` so `v1.0.0` ships all 3 mandatory production backends).
- **Accuracy & Horizon (`v2.0.0`):** Hardware 2x PS1 CD streaming (`150 sectors/s`), ADSR release + ring-buffered SPU/XA mixing (`44.1 kHz`), PGXP stabilization, automated `R3 -> R4` static promotion (`jitDispatchCount == 0 && interpreterFallbackCount == 0` in `v0.4.0`), and **10-Platform Full-Stack Compatibility in `v2.0.0`** (with graceful Zero-RWX Reduced-Capacity Mode on `W^X`-locked hosts).

---

## 4. Project Navigation & Living Docs
- **Feature Inventory & Code Map:** [`FEATURES.md`](FEATURES.md)
- **Milestones & Strategic Horizon:** [`ROADMAP.md`](ROADMAP.md)
- **Version History Ledger:** [`CHANGELOG.md`](CHANGELOG.md)
- **Blueprints (`@pland`):** [`Dev\docs\plans\0.1.0.2026-11-13.md`](Dev/docs/plans/0.1.0.2026-11-13.md) *(Completed `v0.1.0` — `87/87` tests, signed `reEngine` side-by-side installer)*
- **Verification Report (`@inspecter`):** [`Dev\docs\reports\0.1.0.20261113-213430.-.sandbox-verify.md`](Dev/docs/reports/0.1.0.20261113-213430.-.sandbox-verify.md) *(5/5 `Sandbox\` Gates `PASS`)*

---

## 5. Manual Shelf (`Dev\docs\`)
- [`ARCHITECTURE.md`](Dev/docs/ARCHITECTURE.md) | [`INTERFACES.md`](Dev/docs/INTERFACES.md) | [`USER_GUIDE.md`](Dev/docs/USER_GUIDE.md) | [`OPERATIONS.md`](Dev/docs/OPERATIONS.md)
