# reStation (`v0.2.6-hf.1`)

- **Active Version:** `v0.2.6-hf.1` (`2026-11-15`, pre-1.0 `0.2.x` track)
- **Lifecycle State:** `active` ([`active.state`](active.state))
- **Group / Scope:** `Personal/reStation` (`Engine Tier A (5-Folder)`, `reEngine v0.2.1-hf.1`, `Foundation v0.4.1-hf.1`)
- **Logo Assets:** [`Dev\assets\reStation.svg`](Dev/assets/reStation.svg) | [`Dev\assets\reStation.png`](Dev/assets/reStation.png) | [`Dev\assets\reStation.ico`](Dev/assets/reStation.ico)

---

## 1. What It Is & Core Goal
`reStation` is a clean-room **PlayStation 1 (`PS1`) Static Recompilation Engine, `.decomp` Lifter, Player-PC Local AOT Compiler, Opt-In Shared-IR/RyuJIT Diagnostic Translator & Modular BYO-Media Player**—**not** a generic compatibility emulator. Routed via `reEngine` (`v0.2.1-hf.1`) and installed per-user without admin elevation under `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reStation\v0.2.6-hf.1\` (`--profile Lightweight|Full`, `--reengine-dir`, and opt-in `--migrate-legacy`), `reStation` separates authoring from runtime cleanly:

- **Offline Clean-Room Authoring (`reStation.Decomp`):** Implements `IDecompPlatformProvider` (`platformId: "ps1"`) under `MPL-2.0` with an original PS-X EXE + overlay Ghidra loader, R3000A + GTE COP2 `.slaspec`, C/asset normalizer, `D4` LP32 parity verifier, and `D5` MIPS byte-match verifier—emitting deterministic `aot-recipe.json` packages with zero retail media or precompiled `*.Generated.dll` blobs.
- **Player-PC Local AOT Cache (`<dataDir>\cache\`):** On first launch with verified `data\rom\` media (`PlayerMediaResolver` + `AotCacheManager`, supporting both standard PS-X EXEs and `<container>#<slice>` overlays ordered by `aot-recipe.json`), `PlayerLocalAotCompiler` synthesizes `<Game>.Generated.dll` matching `generatedCacheAssemblyName` locally on the player's machine and loads it via a collectible `ModuleAssemblyLoader` (`isCollectible: true`, `0`-byte unload leak), with `--jit-bringup` / `--force-zero-rwx` Shared-IR/RyuJIT/Threaded-IR available as an opt-in diagnostic fallback tier (`ExitCode = 5` non-VBlank spin watchdog).
- **Live Gameplay & Modern Presentation:** Unlocks native C# `60–240+ Hz` interpolation, default `4:3` framing (`16:9` gated by toggle + `IRecompWidescreenProvider`), frame-boundary `Software <-> OpenGL 3.3` hot-swapping, PGXP precision, compact `1060x34` `F3` HUD strip, VBlank-queued mid-stack `F5`/`F9` quick-saves, and simultaneous 3-slot keyboard/mouse (`1001..1007`) + controller (`2001..2032`) bindings with `Schema B` D-Pad & isometric modes.

**Clean-Room & Zero-Asset Guarantee:** `reStation` distributes zero copyrighted Sony BIOS images, retail game assets, pre-lifted `*.g.cs` files, or `*.Generated.dll` binaries. Every title runs from user-supplied media verified via SHA-256 against its `.recomp` module manifest.

---

## 2. Quickstart & Runtime CLI Flags

```powershell
reStation.exe --module .\modules\kings-field\module.recomp.json --disc "D:\Roms\PS1\Kings Field (USA).chd"
reStation.exe --verify --frames 240 --data-dir .\TestData --log-dir .\Dev\logs
reStation.exe --tray-only --data-dir .\TestData --log-dir .\Dev\logs
reStation-Setup-v0.2.6-hf.1-win-x64.exe --profile Lightweight --reengine-dir "%LOCALAPPDATA%\Programs\StreamItAll\reEngine"
```

| CLI Flag | Purpose |
| :--- | :--- |
| `--module <path>` | Load an external `.recomp` directory, `module.recomp.json`, or single-file ZIP `.recomp` archive. |
| `--disc <path>` / `--preload-media <path>` | Extract and SHA-256-verify user optical/archive media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`). |
| `--fps-mode <original\|enhanced>` | Select native PS1 cadence or interpolated high-FPS presentation (`--target-fps`, `--vsync`). |
| `--backend <opengl\|software>` | Switch live between `OpenGL 3.3 Core` (`ArrayPool` FIFO queue) and `SoftwareCpu` (`SDL_Texture` composite); `v0.3.0` adds `vulkan` + `parallelCpu`. |
| `--profile <Lightweight\|Full>` | Installer/launcher suite profile (`Lightweight` local AOT player; `Full` offline Ghidra + OpenJDK `.decomp` tooling). |
| `--reengine-dir` / `--migrate-legacy` | Override the user `reEngine` suite root or opt-in migrate preserved `data\rom\` & `settings.json` from `v0.1.x`. |
| `--jit-bringup` / `--force-zero-rwx` | Opt-in Tier 3 CoreCLR RyuJIT bring-up profiler (`bringup.profile.json`, `ExitCode = 5`) or zero-RWX `ThreadedIrBackend` diagnostic mode (`--execution-tier`). |
| `--verify` / `--tray-only` | Run non-interactive verification checks (`--verify`) or start in offscreen tray/background mode (`--tray-only`, automated via `APP_ISOLATED_TEST=1`). |
| `--wait` / `--version` | Hold launcher parent under `JobObjectGuard` until exit (`--wait` / `--jit-bringup`), or print `reStation v0.2.6-hf.1` (`--version`). |
| `--data-dir` / `--config` / `--log-dir` / `--port` | Override writable runtime data (`cache\`, `saves\`, `memcards\`, `settings.json`), config file, `Dev\logs\` (`RS-06`), or diagnostic port; installer also supports `--dry-run`. |

**CLI Exit-Code Contract:** Root `reStation.exe` propagates deterministic exit codes: `0` pass, `1` runtime/`--verify` failure, `2` CLI validation error, `3` fail-closed path containment violation (`AppEnvironment.AssertPathContained`), `4` disc/archive ingestion failure, **`5` (`--jit-bringup` non-VBlank spin watchdog)**, reserved `6` (`--require-backend`, `v0.3.0`), and `130` (`SIGINT`).

---

## 3. Architecture At a Glance
- **Application Archetype:** Shared Platform Recompilation Engine (`Tier A (5-Folder)`, consuming `Foundation v0.4.1-hf.1` & `reEngine.Core v0.2.1-hf.1`; `SupportedEngineTracks: ["0.2.x", "0.1.x"]`).
- **4-Tier Execution Stack (Built Toward `v1.0.0`):** **(1)** Native C# Hooks (`IGameFramePatch`, `ISubTickFrameHook`, `INativeGameActionHook`, `IRecompWidescreenProvider`) & Clean-Room BIOS/GTE/GP0/SPU/SIO0 HLE $\rightarrow$ **(2)** Player-PC Local `<dataDir>\cache\` AOT (`PlayerLocalAotCompiler` + collectible `ModuleAssemblyLoader`, keeping `.recomp` archives free of `*.Generated.dll`) $\rightarrow$ **(3)** Opt-In Shared MIPS$\rightarrow$IR CoreCLR RyuJIT (`DynamicMethod`) + `4 KB`/`64B` epoch invalidation + `--jit-bringup` / Zero-RWX `ThreadedIrBackend` (`v0.2.0`) $\rightarrow$ **(4)** `[WARN]`-logged `PsxInterpreter` (with mid-stack `ResumeFromSnapshot`) & `SoftwareCpu` oracle.
- **Graphics Contract (`v0.2.0` $\rightarrow$ `v1.0.0` Tri-Backend Law):** Pluggable `IPsxRenderBackend` supporting live frame-boundary hot-swap (`OpenGL 3.3 Core` <-> `SoftwareCpu` with VRAM/PGXP preservation and automatic Software fallback banner); `v0.3.0` adds hardware `Vulkan` and deterministic `ParallelCpu` so `v1.0.0` ships all 3 mandatory production backends.
- **Core PS1 Title Essentials & Lean UI Boundary (`v0.4.0` $\rightarrow$ `v1.0.0`):** `reEngine` owns the multi-title Library Shelf and Engine Manager, while `reStation.Player` stays a lean single-session host (`FirstRunMediaGateView`, 4:3 default framing, `F3` `1060x34` HUD strip, `F1` `SlideOverSettingsDrawer` with `Video`/`Audio`/`Controls`/`Keybinds`, scrollable 5-column `800px` `Keybinds` multi-bind grid with Numpad/Nav labels). By `v0.4.0`, `reStation` adds Port 1+2 DualShock (`0x42–0x4D`) + rumble, unified `.mcr` Slot 1+2, multi-track `.cue`/`.chd` Redbook CD-DA, and `.m3u` `ShellOpen` lid-swap.

---

## 4. Project Navigation & Living Docs
- **Feature Inventory & Code Map:** [`FEATURES.md`](FEATURES.md)
- **Milestones & Strategic Horizon:** [`ROADMAP.md`](ROADMAP.md)
- **Version History Ledger:** [`CHANGELOG.md`](CHANGELOG.md)
- **Implementation Plans:** [`Dev\docs\plans\0.2.0.2026-11-15.md`](Dev/docs/plans/0.2.0.2026-11-15.md) *(Shipped `v0.2.0` — `.decomp`, local `<dataDir>\cache\` AOT, suite installer, opt-in Tier 3 IR/RyuJIT, `T7` runtime/input fixes)* | [`0.1.5.2026-11-14.md`](Dev/docs/plans/0.1.5.2026-11-14.md) | [`0.1.4.2026-11-14.md`](Dev/docs/plans/0.1.4.2026-11-14.md) | [`archive\0.1.3.2026-11-14.md`](Dev/docs/plans/archive/0.1.3.2026-11-14.md) | [`archive\0.1.2.2026-11-14.md`](Dev/docs/plans/archive/0.1.2.2026-11-14.md) | [`archive\0.1.1.2026-11-13.md`](Dev/docs/plans/archive/0.1.1.2026-11-13.md) | [`archive\0.1.0.2026-11-13.md`](Dev/docs/plans/archive/0.1.0.2026-11-13.md)
- **Archived Audit Reports:** [`Dev\docs\reports\archive\0.1.5.2026-11-15_0325.-.hmbtn-joypad-ack-timers-gl-stalls-drawer-and-aspect.md`](Dev/docs/reports/archive/0.1.5.2026-11-15_0325.-.hmbtn-joypad-ack-timers-gl-stalls-drawer-and-aspect.md) | [`0.1.5.2026-11-15_0203.-.playtest-quickload-gl-hotswap-hud-mouse-and-multibind.md`](Dev/docs/reports/archive/0.1.5.2026-11-15_0203.-.playtest-quickload-gl-hotswap-hud-mouse-and-multibind.md) | [`0.1.5.2026-11-14_2115.-.ps1-decomp-aot-cache-wizard-and-public-readiness.md`](Dev/docs/reports/archive/0.1.5.2026-11-14_2115.-.ps1-decomp-aot-cache-wizard-and-public-readiness.md) | [`0.1.0.20261114-020830.-.lockstep-audit.md`](Dev/docs/reports/archive/0.1.0.20261114-020830.-.lockstep-audit.md)

---

## 5. Manual Shelf (`Dev\docs\`)
- [`ARCHITECTURE.md`](Dev/docs/ARCHITECTURE.md) | [`INTERFACES.md`](Dev/docs/INTERFACES.md) | [`USER_GUIDE.md`](Dev/docs/USER_GUIDE.md) | [`OPERATIONS.md`](Dev/docs/OPERATIONS.md)

---

## 6. License & Legal
- **Project License:** [`LICENSE`](LICENSE) — **Mozilla Public License 2.0 (`MPL-2.0`)**.
- **Third-Party Components:** Embedded container extraction tools (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` and accompanied by `LICENSE-7ZIP.txt` in `app\`.
- **Clean-Room Zero-Asset Policy & Non-Affiliation:** `reStation` distributes zero copyrighted game assets, PS1 BIOS ROMs, or Sony proprietary code. Users supply their own legally obtained PS1 disc media via `.recomp` game modules (`data\rom\`). *PlayStation* and *PS1* are registered trademarks of Sony Interactive Entertainment Inc.; `reStation` is an independent clean-room project with zero affiliation or endorsement.
