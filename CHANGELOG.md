# Changelog

All notable changes to `reSlate` (formerly `reStation`, repository path `Personal/reStation`) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0-hf.1] - 2026-11-18
### Fixed
- **Installer `reEngine` Root Collision Guard (`HasDirectInstallation` & `NormalizeWizardStationInstallDir`):** Updated `ReStationInstallerBootstrap.HasDirectInstallation` and `ReStationWizardPathNormalizer` to reject populated `reEngine` directories (via `IsReEngineDirectory` and strict `"product_name"` / `"appId"` inspection inside `app\install-manifest.json`) before falling back to `HasDirectInstallation`, preventing `reSlate-Setup` from treating `reEngine`'s manifest as a direct `reSlate` installation and overwriting `reEngine.exe` instead of installing into `engines\reSlate\v0.3.0-hf.1\`.
- **Poisoned Registry & Existing-Engine Path Resolution (`ReStationInstallPathResolver`):** Updated `DiscoverExistingInstall` and `ResolveFromExistingEngineInstall` so an existing `Uninstall\reSlate` path pointing at a `reEngine` root cannot hijack `preferredInstallDir` or inherit `reEngine`'s `settings.json` profile, resolving cleanly to `<reEngineDir>\engines\reSlate\v0.3.0-hf.1` with regression tests covering both populated `reEngine` directories and poisoned registry states (`268/268` dual Release + Debug tests, `0` warnings).

## [0.3.0] - 2026-11-18
### Added
- **Live Tier 3 Native AOT/JIT Dispatch & Canonical `isPureStaticReady`:** Wired `Tier3ExecutionCoordinator` (`RyuJitBlockBackend` primary, Zero-RWX `ThreadedIrBackend` secondary, `PsxMemoryBus.OnRamWriteRange` coherence) directly into `PsxCpu.Call`/`DispatchFunction`, failing closed in `staticAotOnly` and quarantining `PsxInterpreter` exclusively to `--execution-tier interpreterOracle`; added canonical `BringupProfileDocument.IsPureStaticReady` (`[Obsolete]` `IsR4PromotionReady` read-alias) and `[JsonIgnore]` `IsBringupCaptureClean`.
- **Unified `SIO0 0x81` Memory Cards & BIOS `_new_card`:** Added `SioMemCardProtocol` (`0x52`/`0x57`/`0x53`, `FLAG 0x08`, immediate NACK on mini handheld dock `0x58..0x5F`) wired into `PsxJoypadPort`, `PsxBiosHle.MemCard` (`B0:0x4E/0x4F/0x50`, `A0:0xAB/0xAC`), Slot 1+2 `<dataDir>\memcards\` `MemCardMcrStore` (non-destructively migrating `<dataDir>\saves\memcard_*.mcr`), and `"MCD2"` `"RSTX"` quick-saves.
- **`reSlate.Decomp` `D4_CleanSource` Craftsmanship Gate:** Integrated `DecompD4StrictnessValidator` (`Corpus\tiers\D4_CleanSource` + `D4_FunctionalRewrite` read-alias) and added `PsxD4CraftsmanshipValidator` enforcing 6 decompiler-tell checks, rejecting `_01.._NN.c` fragments and `-Wno-everything`/`-Wno-all`/`-w` flags, verifying `-Wall -Wextra -Wno-unused-parameter` `0`-warning compiles, and standardizing `camelCase` `d4b_lockstep_report.json`.
- **`reEngine.Host.Contracts`, Debug/Test Loopback & 4-Drive Sandbox E2E:** Added `"reEngine.Host"` shared ALC forwarding, `HostContractBridge`, `#if DEBUG || RESTATION_TEST_AUTOMATION` `DebugAutomationHook` and `InstallerAutomationHook` (`0` automation/WinForms refs and `ExitCode = 2` in pure `Release`), `build_release.ps1` + `package_helpers.ps1` (7-file NTFS-hardlinked `Releases\` with `-Portable.zip`, `.ready`, `SharedLibs\reSlate\`, dual hub junctions, `<= 400` code-only SLOC gate), and `run_stage2_sandbox_e2e.ps1`.
- **Dual-Config Verification (`266/266` Release & Debug Tests):** Reconciled and expanded the automated suite to `266/266` passing tests (`0` warnings) in both `-c Release` and `-c Debug` under `APP_ISOLATED_TEST=1`.

### Changed
- **Product Rename & ABI-Preserving Cutover (`reStation` -> `reSlate`):** Switched deliverables to `reSlate.exe`, `reSlate.Player.exe`, `reSlate.Compiler.exe`, `reSlate.Decomp.dll`, `reSlate-Setup-v0.3.0-win-x64.exe`, `reSlate-v0.3.0-win-x64-Portable.zip`, `appId: "reslate"`, `engineId: "reSlate"`, `engines\reSlate\`, and `SharedLibs\reSlate\`, while preserving `reStation.{Contracts,Core}` PE ABI and `reStation.*` namespaces via bidirectional `EngineAliasCatalog.AreEquivalent`.
- **Canonical Settings & `v0.3.0` CLI (`CliOptions`):** Migrated runtime configs cleanly from `<dataDir>\settings.json` to `<dataDir>\config\settings.json` via `SettingsMigrationHelper`, unified `reSlate.Player` CLI parsing directly on `CliOptions`, added `--require-backend` (`ExitCode = 6` on deferred `v0.3.1` `vulkan`/`parallelCpu`), `--save-dir`, `--fixture`, `--dump-audio`, `--doctor`, and `--execution-tier`, and synced `Foundation v0.5.0` + `reEngine v0.3.0`.
- **Non-Emulator Clean-Room Wording:** Scrubbed legacy emulator/trademark wording across engine contracts, UI dialogs, release doc sanitization, and test fixtures.

### Fixed
- **Installer Existing-Directory & Saved-Profile Auto-Detection:** Added `ReStationInstallPathResolver` so interactive and silent `reSlate-Setup` launches prefill existing `HKCU` `reSlate`/`reEngine`/`reStation` paths and `LastInstallBaseDir` (without double-nesting `\StreamItAll\`) alongside saved `installProfile` from `settings.json`, while gating against `SandboxResolution.Inactive`, active sandbox boundaries, and Stage 1 / `APP_ISOLATED_TEST=1` host-mutation suppression.
- **RyuJIT / IR Dispatch & Frame Cadence:** Fixed `RyuJitBlockBackend` `Rfe`/`Lwc2`/`Swc2`/`Mfc0` regs `8`/`13`/`14` and `MipsIrLowerer` `jalr` (`rd == 0`), locked hardware VBlank + `PsxSpu.RenderFrames(735)` strictly at `60.0 Hz`, fixed `VSync(n)` real VBlank delta counting + `IntRP`/`KernelEvent` de-duplication, decimated swap-only on `TargetFps=30`, filtered `SDL_KeyboardEvent.repeat`, and isolated `PollLiveSubTickLook` from keyboard latches.

### Security
- Enforced fail-closed `ExitCode = 2` on `--automation-port` in pure `Release` binaries (with zero automation/WinForms PE references), `ExitCode = 3` path containment, `AllowAutoStart = false`, zero-ROM/BIOS/secret hygiene, and a clean public Git history scrub.

## [0.2.8] - 2026-11-16
### Added
- **Per-Game Control Options (`ControlOptions` & `metadataJson`):** Added optional module-declared game control options rendered dynamically inside `F1 -> Controls` alongside live binding summaries, persisted cleanly in `settings.json` with backward-compatible contracts.
- **Regression Coverage (`184/184` Passing Tests):** Verified per-game control serialization, unstretched Hor+ `16:9` aspect proportions, automatic `4:3` gating on 24-bit MDEC FMVs, latched mid-tick keyboard/mouse edges, BIOS `CloseEvent` reuse, and bounded SPU audio draining.

### Fixed
- **Unstretched Hor+ `16:9` Widescreen:** Removed horizontal GTE projection squash and rendered widened side-wing geometry in both `OpenGL 3.3` and `Software` while keeping 2D HUD overlays and 24-bit FMVs centered at `4:3`.
- **Decoupled `60 Hz` VBlank Loop & Persistent Mouse Capture:** Locked outer hardware VBlank pacing to `60.0 Hz`, forwarded Enhanced FPS targets to `ISubTickFrameHook`, latched SDL key/click edges across sub-tick polls, and kept relative mouse capture active across focus shifts and bound `Esc` actions.
- **Event Leaks & Runaway SPU Audio Lag:** Implemented `B0:0x09 CloseEvent` slot reuse, isolated `IntRP` heads and SPU buffers across `ExecOverlay`, and capped queued SDL audio stream latency cleanly.

## [0.2.7] - 2026-11-15
### Added
- **Kernel `IntRP` Chains, `GetC0Table` & Quick-Save Extension Hook:** Wired `ToT[0]`/`SysEnqIntRP`/`SysDeqIntRP`/`GetC0Table` interrupt priority chains and optional `IRecompQuickSaveProvider` callbacks for custom `"RSTX"` quick-save chunks (`180/180` passing tests).

### Fixed
- **Controller Poll IRQ Timing & `--jit-bringup` Watchdog Exit Preservation:** Advanced controller wake ticks on `I_STAT` polls, fixed modern input hook ordering before native game actions, added non-VBlank spin watchdog enforcement (`ExitCode = 5`), and defaulted unconfigured `LockedCap` modules to `Original` timing.

## [0.2.6-hf.1] - 2026-11-15
### Changed
- **SDK Sync & Isolated Test Harness Cleanup:** Migrated onto updated `Foundation` and `reEngine` packages using `AppEnvironment.AssertPathContained`, `UiPresentationMode.TrayOrBackground`, and `APP_ISOLATED_TEST=1` containment across `175/175` Release unit tests.

### Removed
- **Retired Legacy Test Harnesses:** Removed standalone non-production runner classes, test staging directories, and non-production CLI harness flags.

## [0.2.6] - 2026-11-15
### Fixed
- **Post-Ingestion Media Discovery & 2x CD-ROM VBlank Wakeups:** Searched extracted `data\gamedata` media alongside `data\rom\` with `.chd`/`.7z`/`.zip` support and preserved intermediate CD-ROM/DMA/timer events inside `TryAdvanceToNextVBlank`.
- **Cold Local AOT Pipe Isolation & Responsive Splash Pumping:** Disabled shared child build servers during first-run AOT compiles, pumped SDL events smoothly during compile waits, and staged `engine.manifest.json` cleanly in portable builds (`176/176` tests).

## [0.2.5] - 2026-11-15
### Fixed
- **Iterative Tail-Jump Dispatch & `F3` HUD Strip Clamping:** Eliminated recursive `JumpIndirect` stack overflows via an `O(1)` trampoline loop and clamped `F3` HUD strip width while the `Keybinds` drawer is open (`173/173` tests).

## [0.2.4] - 2026-11-15
### Fixed
- **Setup Wizard Parent Folder Auto-Sorting:** Sorted generic parent folder selections cleanly into `<parent>\reEngine\engines\<engine>\v<Ver>\` while preserving explicit `--install-dir` CLI overrides (`170/170` tests).

## [0.2.3] - 2026-11-15
### Fixed
- **Propagate Non-Zero Bring-Up Exit Codes:** Preserved `Environment.ExitCode` values (such as `--jit-bringup` `ExitCode = 5`) cleanly across frame-loop exit and launcher wait forwarding (`167/167` tests).

## [0.2.2] - 2026-11-15
### Added
- **Recipe-Ordered `<container>#<slice>` Overlay Lifting & Collectible Companion Preload:** Added verified container slice extraction (`AotRecipeSpec` `ExeId` ordering, cross-module seed isolation, `generatedCacheAssemblyName` PE parity), collectible ALC unloading cleanup, USB HID numpad/nav key labels, and a widened `800px` `Keybinds` grid (`166/166` tests).

## [0.2.1] - 2026-11-15
### Fixed
- **Dynamic-Switch Delay-Slot Lifting & Consumer `settings.json` Preservation:** Tracked `dynSwitch` delay-slot jump targets with `.rodata` tail warning tolerance for local AOT compiles and preserved consumer `AppId`/`SchemaVersion` fields (`163/163` tests).

## [0.2.0] - 2026-11-15
### Added
- **Clean-Room `.decomp` Provider (`MPL-2.0`), Local `<dataDir>\cache\` AOT & Opt-In Tier 3 IR/JIT:** Replaced third-party/vendor headers with an original `R3000A + GTE` SLEIGH specification and clean-room validators, added local player-PC AOT compilation from verified `data\rom\` media, introduced optional Shared-IR/RyuJIT/Zero-RWX `ThreadedIrBackend` + `--jit-bringup` profiling, moved default installs per-user (`AllowAutoStart = false`), and added simultaneous 3-slot KBM + controller `Keybinds` (`161/161` tests).

### Fixed
- **Quick-Load Mid-Stack Resume, Joypad `/ACK` Timing & Live `GL33 <-> Software` Swapping:** Restored mid-stack `F9` state via interpreter resume, replaced latched joypad `/ACK` pulses with cycle-accurate finite timing, synchronized hardware timers on register reads, and added coalesced OpenGL VRAM staging with automatic `Software` fallback.

## [0.1.5] - 2026-11-14
### Added
- **Overlay Interface Emission, InitPAD Split & SPU Unity Defaults:** Emitted `IOverlayRecompModule` directly from lifted overlay metadata, NACKed unsupported analog/dock probes cleanly, separated 4-arg `InitPAD` from `PAD_init`, and calibrated SPU/XA unity mixing defaults (`138/138` tests).

## [0.1.4] - 2026-11-14
### Fixed
- **Controller Byte-Swap & 3-Tier Control Profiles:** Fixed controller halfword byte-swapping and active-LOW port masking while introducing simultaneous 3-tier KBM/mouse profiles (`134/134` tests).

## [0.1.3] - 2026-11-14
### Fixed
- **Side-by-Side `--uninstall` Normalization & Legal Bundle Sanitization:** Normalized base `--uninstall` directories cleanly, bundled `MPL-2.0` `LICENSE`, omitted empty engine `data\rom\.keep` markers, and added automated release-doc sanitization (`130/130` tests).

## [0.1.2] - 2026-11-14
### Fixed
- **Sub-Tick Render Marshaling, GTE Unclamped Projection & Single-File ZIP Cartridges:** Marshaled sub-tick renders to the window thread, restored unclamped GTE projection coordinates, and added native ZIP `.recomp` module loading (`129/129` tests).

## [0.1.1] - 2026-11-14
### Added
- **Additive Consumer Seams & Synchronous Router Wait Bridge:** Introduced `ISubTickFrameHook`, overlay SHA-256 verification hooks, `JobObjectGuard` synchronous wait handling, and additive `"RSTX"` quick-save trailers (`125/125` tests).

## [0.1.0] - 2026-11-13
### Added
- **Initial Modular Release:** Released reusable `R3000A` CPU, GTE, MDEC, 2x CD-ROM, SPU/XA mixer, `OpenGL 3.3` + `Software` backends, AOT lifter CLI, BYO media ingestion, and per-user side-by-side setup packaging (`87/87` tests).
