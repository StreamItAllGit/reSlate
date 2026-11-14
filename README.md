# reStation (`v0.1.5`)

- **Active Version:** `v0.1.5` (`2026-11-14`, pre-1.0 `0.1.x` track)
- **Lifecycle State:** `active` ([`active.state`](active.state))
- **Group / Path:** ``
- **Logo Assets:** [`Dev\assets\reStation.svg`](Dev/assets/reStation.svg) | [`Dev\assets\reStation.png`](Dev/assets/reStation.png) | [`Dev\assets\reStation.ico`](Dev/assets/reStation.ico)

---

## 1. What It Is & Core Goal
`reStation` is a reusable **PlayStation 1 (`PS1`) Static Recompilation Engine, Shared-IR Runtime JIT Binary Translator, Clean-Room HLE Runtime & Modular BYO-Media Player**—**not** a generic compatibility emulator. Routed via [`reEngine`](<PROJECTS_ROOT>/Personal/reEngine/README.md) (`v0.1.3`) and installed side-by-side under `<PROGRAMS_ROOT>\reEngine\engines\reStation\v<Ver>\` (with immutable SDK history in `<SHARED_LIBS_ROOT>\reStation\v<Ver>\`), `reStation` unlocks native C# gameplay and presentation enhancements (`60–240+ Hz` 3D on `30 Hz` ticks, `WASD` + raw mouse-look, `16:9`/`21:9` frustum expansion, PGXP precision, HD texture overrides) atop ahead-of-time static `.recomp` translation and runtime CoreCLR RyuJIT translation—treating CPU interpretation strictly as a `[WARN]`-logged fallback and `SoftwareCpu` strictly as a deterministic parity test oracle.

Rather than duplicating shared ISA translation, HLE peripheral/BIOS services, and host integration inside every game module repository, `reStation` hosts the shared platform stack (`Foundation.Core` + `Foundation.Retro`) and loads external `.recomp` game modules paired with the user's own legal disc/archive copy (preloaded in gitignored `data\rom\` or selected via `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`).

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
| `--backend <opengl\|software>` | Select `IRecompRenderBackend` (`OpenGL 3.3 Core` with off-thread FIFO queue or `SoftwareCpu` parity oracle; expanding in `v0.3.0–v1.0.0` to `opengl` + `vulkan` + `parallelCpu` + `software`). |
| `--preload-media <path>` | Override `data\rom\` discovery with an explicit SHA-256-verified disc image or archive path (`v0.1.1+`). |
| `--input-script <spec.json>` | Execute deterministic keyboard, mouse, `F1` drawer, screenshot, and RAM-assertion scripts in live/headless sessions (`v0.1.4+`). |
| `--wait` / `--version` | Hold launcher parent under `JobObjectGuard` until session exit (`--wait`), or print `reStation v0.1.5` (`--version`). |
| `--data-dir` / `--config` / `--log-dir` | Redirect writable runtime data, `settings.json`, `settings.controls.json`, and telemetry logs (`--test-mode` fail-closed). |
| `--headless` / `--visible-test` | Automated unit/batch mode (`--headless`) or foreground topmost OpenGL 3.3 live verification (`--visible-test`). |

**CLI Exit-Code Contract & Upcoming Bridge Switches:** Root `reStation.exe` propagates deterministic exit codes: `0` pass, `1` runtime/`--verify`/`--fixture` failure, `2` CLI validation error, `3` fail-closed sandbox containment violation, `4` disc/archive ingestion failure, reserved `5` (`--jit-bringup` non-VBlank spin watchdog timeout, `v0.2.0`), reserved `6` (`--require-backend` hardware/ICD unavailable, `v0.3.0`), and `130` (`SIGINT`). Shipped `v0.1.x` bridge switches cover `--preload-media`, `--wait`, `--version`, and `--input-script`; upcoming switches wire `--jit-bringup` & `--force-zero-rwx` (`v0.2.0`), `--require-backend` (`v0.3.0`), and `--save-dir` (`v0.4.0`).

---

## 3. Architecture At a Glance
- **Application Archetype:** Shared Platform Recompilation Engine (`Tier A`, consuming `Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`).
- **4-Tier Execution Stack (Built Toward `v1.0.0`):** **(1)** Native C# Hooks (`IGameFramePatch` / `ISubTickFrameHook` via `IReStationCpuEx` & `IReStationHostEx`) & Clean-Room BIOS/GTE/GP0/SPU/SIO0 HLE (including digital non-`0x42` probe NACK, separated 4-arg `B0:0x12` `InitPAD` vs `B0:0x15` `PAD_init`, SPU unity defaults & `SPUCNT`/CD/Main Vol gating, and `PsxCpu.Break` `exe`/`ra` triage in `v0.1.5`) $\rightarrow$ **(2)** Static AOT `.recomp` Assemblies (`IRecompGameModule` / `IOverlayRecompModule` emission in `reStation.Compiler`, `0x8019F07C` SHA-256 RAM disambiguation, EOF `"RSTX"` `"BIOS"`/`"OVLY"` save trailers) $\rightarrow$ **(3)** Shared MIPS$\rightarrow$IR CoreCLR RyuJIT (`DynamicMethod`) + `4 KB`/`64B` epoch invalidation + `--jit-bringup` raw-disc profiler & Zero-RWX `ThreadedIrBackend` (`v0.2.0`) $\rightarrow$ **(4)** `[WARN]`-logged `PsxInterpreter` & `SoftwareCpu` oracle.
- **Graphics Contract (`v0.1.x` $\rightarrow$ `v1.0.0` Tri-Backend Law):** Pluggable `IPsxRenderBackend` (`OpenGL 3.3 Core` with thread-safe off-thread FIFO queue + `SoftwareCpu` parity oracle; adding hardware `Vulkan` and deterministic tile/row-band multithreaded `ParallelCpu` by `v0.3.0` so `v1.0.0` ships all 3 mandatory production backends).
- **Core PS1 Title Essentials & Lean UI Boundary (`v0.4.0` $\rightarrow$ `v1.0.0`):** [`reEngine`](<PROJECTS_ROOT>/Personal/reEngine/README.md) owns the multi-title Library Shelf and Engine Manager, while `reStation.Player` stays a lean single-session host (`FirstRunMediaGateView` + 5-tab `F1` `SlideOverSettingsDrawer`: `Video`, `Controls`, `Keybinds`, `System`, `Disc` + `F3` HUD). By `v0.4.0`, `reStation` adds Port 1+2 DualShock (`0x42–0x4D`) + rumble, unified `SIO0` `0x81` / BIOS `bu00:`/`bu10:` `.mcr` Slot 1+2 persistence, multi-track `.cue`/`.chd` Redbook CD-DA (`CdlPlay`), and fail-closed `.m3u` `ShellOpen` lid-swap (locking Multitap, GunCon/Mouse, Serial Link, PocketStation, and retail Sony BIOS ROMs out as Non-Goals).
- **Accuracy & Horizon (`v1.0.0` Gold $\rightarrow$ `v2.0.0`):** Hardware 2x PS1 CD streaming (`150 sectors/s`), ADSR release + ring-buffered SPU/XA/CD-DA mixing (`44.1 kHz`), PGXP stabilization, automated Tier 3 $\rightarrow$ `100%` static C# `.recomp` overlay promotion (`jitDispatchCount == 0 && threadedIrDispatchCount == 0 && interpreterFallbackCount == 0`, setting `isR4PromotionReady == true` across `CrownDepthsRecomp` plus at least one overlay-heavy decompilation track in `v0.4.0`/`v1.0.0`; standalone native C# ports ship outside `reStation`), **`v1.0.0` `win-x64` Going Gold** before **10-Platform Compatibility in `v1.1.0–v2.0.0`**.

---

## 4. Project Navigation & Living Docs
- **Feature Inventory & Code Map:** [`FEATURES.md`](FEATURES.md)
- **Milestones & Strategic Horizon:** [`ROADMAP.md`](ROADMAP.md)
- **Version History Ledger:** [`CHANGELOG.md`](CHANGELOG.md)
- **Implementation Plans:** [`Dev\docs\plans\0.1.5.2026-11-14.md`](Dev/docs/plans/0.1.5.2026-11-14.md) *(Shipped `v0.1.5` — digital joypad NACK, 4-arg `B0:0x12` `InitPAD` split, SPU unity/XA gating, `IOverlayRecompModule` emission, `PsxCpu.Break` `exe`/`ra` triage)* | [`0.1.4.2026-11-14.md`](Dev/docs/plans/0.1.4.2026-11-14.md) | [`0.1.3.2026-11-14.md`](Dev/docs/plans/0.1.3.2026-11-14.md) | [`0.1.2.2026-11-14.md`](Dev/docs/plans/0.1.2.2026-11-14.md) | [`archive\0.1.1.2026-11-13.md`](Dev/docs/plans/archive/0.1.1.2026-11-13.md) | [`archive\0.1.0.2026-11-13.md`](Dev/docs/plans/archive/0.1.0.2026-11-13.md)
- **Verification Reports:** [`Dev\docs\reports\archive\0.1.5.20261114_193351.-.sandbox-visible-test.md`](Dev/docs/reports/archive/0.1.5.20261114_193351.-.sandbox-visible-test.md) *(Packaged `Sandbox\` silent install, topmost `--visible-test` + `F1` drawer + 3-tier log sweep `PASS`)* | [`0.1.4.2026-11-14.-.visible-test-and-pad-audit.md`](Dev/docs/reports/archive/0.1.4.2026-11-14.-.visible-test-and-pad-audit.md)

---

## 5. Manual Shelf (`Dev\docs\`)
- [`ARCHITECTURE.md`](Dev/docs/ARCHITECTURE.md) | [`INTERFACES.md`](Dev/docs/INTERFACES.md) | [`USER_GUIDE.md`](Dev/docs/USER_GUIDE.md) | [`OPERATIONS.md`](Dev/docs/OPERATIONS.md)

---

## 6. License & Legal
- **Project License:** [`LICENSE`](LICENSE) — **Mozilla Public License 2.0 (`MPL-2.0`)**.
- **Third-Party Components:** Embedded container extraction tools (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` and accompanied by `LICENSE-7ZIP.txt` in `app\`.
- **Clean-Room Zero-Asset Policy & Non-Affiliation:** `reStation` distributes zero copyrighted game assets, PS1 BIOS ROMs, or Sony proprietary code. Users supply their own legally obtained PS1 disc media via `.recomp` game modules (`data\rom\`). *PlayStation* and *PS1* are registered trademarks of Sony Interactive Entertainment Inc.; `reStation` is an independent clean-room project with zero affiliation or endorsement.
