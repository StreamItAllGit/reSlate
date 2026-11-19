# reSlate — System Architecture & Symbol Map

- **Version:** `v0.3.0` *(GitHub Public-Ready Beta · `appId: "reslate"` · `engineId: "reSlate"` · Track `0.3.x`, Alias-Compatible with `0.2.x` / `0.1.x`; repository path `Personal/reStation`)*
- **Maintained By:** `@devv` (Technical Truth) & `@tessa` (Clarity) · **Enforcer:** `@dean` (Gate 2)

---

## 1. System Context & Runtime Model

`reSlate` is a dedicated static recompilation engine — never a generic interpreter-driven compatibility layer — for `SlateOne` (`MIPS32 R3000A + GTE`) titles, decoupled across two clean-room stages: (1) an offline `.decomp` authoring pipeline (`reSlate.Decomp.dll` + `reSlate.Compiler.exe`) that converts user-supplied `PS-X EXE`/disc layouts into shareable `aot-recipe.json` + `module.recomp.json` metadata (`0` copyrighted ROMs or pre-built generated DLLs), and (2) a player-PC runtime (`reSlate.exe` -> `reSlate.Player.exe` + `reStation.Core.dll`) that compiles `<Game>.Generated.dll` locally inside `<dataDir>\cache\` via `AotCacheManager`, loads it in a collectible `ModuleAssemblyLoader`, executes unlifted/bring-up blocks natively via live `Tier3ExecutionCoordinator` (`RyuJitBlockBackend` primary / Zero-RWX `ThreadedIrBackend` secondary, with `PsxInterpreter` quarantined strictly to `--execution-tier interpreterOracle`), and runs a `60.0 Hz`-locked VBlank/SPU frame loop with `OpenGL 3.3` <-> `Software CPU` swapping (`Vulkan` and `ParallelCpu` explicitly deferred to `v0.3.1`). To keep existing `0.1.x`/`0.2.x` cartridges ABI-compatible, C# namespaces remain `reStation.*` and `reStation.Contracts.dll` / `reStation.Core.dll` retain their assembly names while `EngineAliasCatalog.AreEquivalent` bridges `reSlate` <-> `reStation`.

```mermaid
flowchart TB
    subgraph ROUTER["reEngine v0.3.0 Host Suite (%LOCALAPPDATA%\\Programs\\StreamItAll\\reEngine\\)"]
        RE["reEngine Router / Offline Toolchain (toolchain\\)"]
        SLIB["Foundation v0.5.0 & reEngine v0.3.0 (reEngine.Core + reEngine.Host in Dev\\lib\\ RO)"]
    end

    subgraph DECOMP["Authoring Pipeline (Offline Clean-Room)"]
        DEC["reSlate.Decomp.dll (IDecompPlatformProvider, R3000A_GTE, D4_CleanSource 6-Tell & -Wall Gate)"]
        CMP["reSlate.Compiler.exe (--emit-clean-room -> aot-recipe.json)"]
    end

    subgraph RUNTIME["Player Runtime (reSlate.exe -> reSlate.Player.exe -> reStation.Core.dll)"]
        LNC["reSlate.exe (JobObjectGuard + --profile / --reengine-dir / --doctor)"]
        AOT["PlayerLocalAotCompiler + AotCacheManager -> data\\cache\\<Game>.Generated.dll"]
        ALC["ModuleAssemblyLoader (Collectible ALC + reEngine.Host Shared)"]
        CPU["PsxCpu + Tier3ExecutionCoordinator (RyuJIT / Zero-RWX IR) + SIO0 0x81 / B0:0x4E-0x50 MemCards"]
        VID["PsxGpu.SetBackend: OpenGL 3.3 <-> Software CPU (Vulkan/ParallelCpu deferred to v0.3.1)"]
        UI["HostContractBridge + UiOverlayPresenter + SlideOverSettingsDrawer (+ Debug-Only DebugAutomationHook)"]
    end

    RE -->|"routes --module / --require-backend / --doctor"| LNC
    RE -.->|"Full toolchain\\"| DEC
    SLIB --> DEC
    SLIB --> RUNTIME
    DEC -->|"aot-recipe.json + D4_CleanSource"| CMP
    CMP -->|".recomp ZIP (zero Generated.dll)"| AOT
    LNC -->|"--wait supervised"| AOT
    AOT --> ALC --> CPU
    CPU --> VID --> UI
```

---

## 2. Assembly / `.dll` & Module Dependency Graph

```mermaid
flowchart LR
    EXT["Dev\\lib\\ Pin: Foundation.{Core,Retro,Installer,Launcher}.dll (v0.5.0) + reEngine.{Core,Host}.dll (v0.3.0)"]
    AUT["Foundation.Automation.dll (Debug || RESTATION_TEST_AUTOMATION ONLY)"]
    CON["reStation.Contracts.dll (net8.0, ABI Lock)"]
    CORE["reStation.Core.dll (net8.0, ABI Lock)"]
    DEC["reSlate.Decomp.dll (net8.0)"]
    CMP["reSlate.Compiler.exe (net8.0)"]
    PLY["reSlate.Player.exe (net8.0 / Debug net8.0-windows)"]
    LNC["reSlate.exe (net8.0-windows SingleFile Stub)"]
    INS["reSlate-Setup-v0.3.0-win-x64.exe (net8.0-windows)"]
    TST["reStation.Tests.dll (xUnit, APP_ISOLATED_TEST=1)"]

    EXT --> CON
    EXT --> CORE
    EXT --> DEC
    EXT --> INS
    EXT --> LNC
    EXT --> PLY
    AUT -.->|"Debug/Test Only"| PLY
    AUT -.->|"Debug/Test Only"| TST
    CON --> CORE
    CON --> DEC
    CON --> CMP
    CON --> PLY
    CORE --> CMP
    CORE --> PLY
    PLY --> LNC
    CON --> TST
    CORE --> TST
    DEC --> TST
    CMP --> TST
    PLY --> TST
    LNC --> TST
    INS --> TST
```

### Dependency Rules (Allowed vs. Forbidden)
- **Allowed:** Pinned `Dev\lib\` SDK binaries (`Foundation v0.5.0`, `reEngine v0.3.0` including `reEngine.Host.dll`), unidirectional `Contracts -> Core / Decomp -> Compiler / Player -> Launcher`, isolated `Installer -> Foundation.Installer`, and `#if DEBUG || RESTATION_TEST_AUTOMATION` conditional `Foundation.Automation` + `<UseWindowsForms>` in `Player` (100% stripped from `Release`).
- **Forbidden:** Cross-repo writes to `Foundation\`, `reEngine\`, `_DevBible\`, or read-only `Research\*` repos; bundled `*.Generated.dll` or `*.g.cs` inside `.recomp`/`.decomp`; `GPLv3` `ghidra_psx_ldr` or vendor SDK headers (`.gdt`/`.til`); unapproved files exceeding Standard 32 `400` code-only SLOC ceiling (non-test); `Foundation.Automation.dll` / `System.Windows.Forms` PE references in `Release`; silent interpreter fallback outside `--execution-tier interpreterOracle`; or `AGENTS.md` staged inside `Builds\`/`Portable.zip`.

---

## 3. End-to-End Data Flow per Major Pipeline

### A. Clean-Room `SlateOne` `.decomp` Flow (`D0`–`D5`)
1. [`PsxDecompPlatformProvider.RunDecompAsync`](../src/reStation.Decomp/PsxDecompPlatformProvider.cs#L48-L132) validates `module.decomp.json` + `.slaspec`, runs `DecompD4StrictnessValidator` (`Corpus\tiers\D4_CleanSource\` with `D4_FunctionalRewrite` read-alias), and enforces [`PsxD4CraftsmanshipValidator`](../src/reStation.Decomp/PsxD4CraftsmanshipValidator.cs) across 6 decompiler-tell families + real `-Wall -Wextra -Wno-unused-parameter` `0`-warning checks whenever C sources or `d4_build_report.json` are present (`PSX_D4_CRAFTSMANSHIP_FAILED`).
2. [`PsxCleanExeAndOverlayLoader.DiscoverProgramLayout`](../src/reStation.Decomp/PsxCleanExeAndOverlayLoader.cs#L111-L154) and [`PsxIsoImageScanner`](../src/reStation.Decomp/PsxIsoImageScanner.cs) parse PS-X EXE + dynamic overlay addresses and provision `ghidra_ext/` + cohesive `PsxCleanExeLoader.py` exports (`PsxExportAndAssetPipeline`, `PsxParityAndByteMatchVerifier` `camelCase` `d4b_lockstep_report.json`).

### B. Player-PC Local AOT Cache & Live Tier 3 Execution (`data\cache\`)
1. [`Program.cs`](../src/reStation.Player/Program.cs) parses CLI directly via `CliOptions.Parse(args)`, migrates `<dataDir>\settings.json` to `<dataDir>\config\settings.json` non-destructively via `SettingsMigrationHelper.MigrateForConfigPathIfNeeded`, handles `--doctor` diagnostics and `--require-backend vulkan|parallelCpu` (`ExitCode = 6`), and resolves verified media (`PlayerMediaResolver`).
2. [`PlayerLocalAotCompiler.EnsureCompiledInCacheAsync`](../src/reStation.Core/Aot/PlayerLocalAotCompiler.cs) probes `reSlate.Compiler` -> `reStation.Compiler` to compile `<Game>.Generated.dll` into `<dataDir>\cache\`. [`ModuleAssemblyLoader`](../src/reStation.Player/ModuleAssemblyLoader.cs) shares `"reEngine.Host"`, synchronizes `resolvedTier` onto both `settings.ExecutionTierMode` and `cpu.Tier3Coordinator.ExecutionTierMode`, and lets `PsxCpu.Call`/`DispatchFunction` execute compiled AOT first, native `Tier3ExecutionCoordinator` (`RyuJitBlockBackend` / Zero-RWX `ThreadedIrBackend`) second, or fail closed in `staticAotOnly` (`PsxInterpreter` quarantined strictly to `interpreterOracle`). `JitBringupRecorder` writes canonical `isPureStaticReady` (`false` during active bring-up) + `[Obsolete]` `isR4PromotionReady` backward-compat field.

### C. Frame Timing, SIO0 Memory Cards, Input & Host Bridge
1. [`ReStationHost`](../src/reStation.Player/ReStationHost.cs) keeps hardware VBlank and `PsxSpu.RenderFrames(735)` locked at `60.0 Hz` (`44.1 kHz` audio); [`PsxCpu.Scheduling`](../src/reStation.Core/Cpu/PsxCpu.Scheduling.cs) tracks `VBlankSequence` cleanly with `IntRP`/`KernelEvent` de-duplication (`PsxBiosHle.Events`) so `VSync(2)` runs at `30 Hz` guest logic; [`Program.FrameLoop`](../src/reStation.Player/Program.FrameLoop.cs) decimates only swap/presentation on `TargetFps == 30`; [`Program.EventPump`](../src/reStation.Player/Program.EventPump.cs) ignores `SDL_KeyboardEvent.repeat != 0` and isolates `PollLiveSubTickLook` from keyboard latches.
2. [`SioMemCardProtocol`](../src/reStation.Core/Hardware/SioMemCardProtocol.cs) + [`PsxBiosHle.MemCard`](../src/reStation.Core/Bios/PsxBiosHle.MemCard.cs) + [`MemCardMcrStore`](../src/reStation.Core/Bios/MemCardMcrStore.cs) unify hardware `SIO0 0x81` (`0x52`/`0x57`/`0x53`, explicit dock `0x58..0x5F` NACK, `FLAG 0x08`) and BIOS `B0:0x4E/0x4F/0x50` `_new_card` over Slot 1+2 `<dataDir>\memcards\` (auto-migrating `<dataDir>\saves\memcard_*.mcr`) with `"MCD2"` `"RSTX"` quick-saves (`QuickSaveTrailer`).
3. [`HostContractBridge`](../src/reStation.Player/HostContractBridge.cs) exposes `reEngine.Host.Contracts.IHostAutomationTarget` (`HostWidgetBounds`, `F3HudTelemetry`, `HostInputMapper`); in `Debug`/`RESTATION_TEST_AUTOMATION` builds [`DebugAutomationHook`](../src/reStation.Player/DebugAutomationHook.cs) runs `127.0.0.1` loopback automation for [`run_stage2_sandbox_e2e.ps1`](../scripts/run_stage2_sandbox_e2e.ps1), while pure `Release` rejects `--automation-port` with `ExitCode = 2`.

---

## 4. File & Symbol Lookup Registry (`180` Tracked `Dev\src` + `Dev\scripts` Files · `138` Non-Test `.cs`/`.ps1` — Max `355` Code SLOC)

All non-test C# and PowerShell source files comply strictly with Standard 32's `400` code-only SLOC limit (`0` files over `400`). Key `v0.3.0` additions/splits and modified anchors include:

| File | Lines | CodeSLOC | Primary Exports & Responsibility |
| :--- | ---: | ---: | :--- |
| `Dev/scripts/build_release.ps1` | 210 | 175 | Single-pass `<=400` SLOC gate, `Release` build+test, Authenticode sign, installer, `Releases\`, `SharedLibs\reSlate\`, `Archive\` |
| `Dev/scripts/package_helpers.ps1` | 283 | 260 | `Assert-CodeOnlySlocGate`, `Assert-BinaryPurity`, `Assert-InstallerPurity`, `.ready`/manifest staging, 7-file hardlinked `Releases\`, `Portable.zip`, `SharedLibs\reSlate` |
| `Dev/scripts/run_stage2_sandbox_e2e.ps1` | 238 | 218 | 4-step visible Stage 2 Sandbox E2E (`run_sandbox_e2e.py`, visible `Setup Wizard`, visible SDL3 MediaGate & F1/F3 Player, Release `ExitCode=2`) |
| `Dev/scripts/stage2_e2e_helpers.ps1` | 283 | 255 | `Win32VisibleWindow`, `Show-VisibleWindowOnTop`, `Send-WindowClose`, loopback tree/PNG assertions & 4-drive child env |
| `Dev/scripts/sanitize_release_docs.ps1` | 191 | 171 | Relative link rewriter, Standard 22 §4 trademark scrub outside legal footer, end-user doc sanitization |
| `Dev/src/reStation.Contracts/IReStationModuleManifest.cs` | 206 | 138 | `ReStationEngineInfo` (`AppId="reslate"`, `EngineId="reSlate"`, `0.3.0`, `0.3.x`), `EngineAliasCatalog.AreEquivalent` manifest check |
| `Dev/src/reStation.Contracts/IPsxRenderBackend.cs` | 56 | 21 | `PsxRenderBackendIds` (`opengl`, `software`, `v0.3.1`-deferred `vulkan`/`parallelCpu`) |
| `Dev/src/reStation.Core/Bios/MemCardMcrStore.cs` | 375 | 342 | Slot 1+2 128 KB `.mcr` store under `<dataDir>\memcards\`, `<dataDir>\saves\` auto-migration, dirty VBlank debounce, raw bytes |
| `Dev/src/reStation.Core/Bios/PsxBiosHle.MemCard.cs` | 148 | 125 | BIOS `B0:0x4E/0x4F` 128B transfers (`NotifyRamWriteRange`), `A0:0xAB/0xAC`, `B0:0x50` `_new_card` |
| `Dev/src/reStation.Core/Bringup/JitBringupRecorder.cs` | 340 | 285 | `BringupProfileDocument.IsPureStaticReady` + write-never legacy `isR4PromotionReady` read compat, `[JsonIgnore]` `IsBringupCaptureClean` |
| `Dev/src/reStation.Core/Core/CliOptions.cs` | 284 | 258 | Unified CLI (`--require-backend`, `--save-dir`, `--fixture`, `--dump-audio`, `--automation-port/token`, `--doctor`, `--execution-tier`) |
| `Dev/src/reStation.Core/Core/SettingsMigrationHelper.cs` | 104 | 76 | Atomic non-destructive `<dataDir>\settings.json` -> `<dataDir>\config\settings.json` migrator |
| `Dev/src/reStation.Core/Cpu/PsxCpu.cs` / `.Scheduling.cs` / `.Snapshot.cs` | 387 / 349 / 47 | 353 / 314 / 39 | Live `Tier3ExecutionCoordinator` dispatch, `VBlankSequence`, quarantined `interpreterOracle`, static/snapshot split |
| `Dev/src/reStation.Core/Hardware/SioMemCardProtocol.cs` | 199 | 178 | Hardware `SIO0 0x81` protocol (`0x52`/`0x57`/`0x53`, `FLAG 0x08`, `0x58..0x5F` dock NACK) wired into `PsxJoypadPort` |
| `Dev/src/reStation.Decomp/PsxD4CraftsmanshipValidator.cs` | 423 | 348 | `D4_CleanSource` 6-tell regex gate, `_01.._NN.c` fragment rejection, `-Wall -Wextra -Wno-unused-parameter` verifier |
| `Dev/src/reStation.Installer/InstallerAutomationHook.cs` | 89 | 70 | `#if DEBUG \|\| RESTATION_TEST_AUTOMATION` WinForms `Wizard_*` IDs + `AutomationLoopbackServer` (Release fails closed `ExitCode=2`) |
| `Dev/src/reStation.Installer/Program.cs` | 400 | 355 | Installer CLI dispatch, interactive WinForms wizard launcher, `ResolveInitialPaths` wiring & post-install actions |
| `Dev/src/reStation.Installer/ReStationInstallPathResolver.cs` | 342 | 278 | HKCU existing `reSlate`/`reEngine`/`reStation` + `LastInstallBaseDir` path & saved profile discovery (`SandboxResolution.IsSandboxActive`- & `ShouldSuppressHostMutations`-gated) |
| `Dev/src/reStation.Player/Program.cs` / `.EventPump.cs` / `.FrameLoop.cs` / `.MediaGate.cs` | 373 / 243 / 385 / 229 | 330 / 228 / 348 / 209 | Direct `CliOptions` entrypoint, `SettingsMigrationHelper` wiring, `60 Hz` SPU lock, repeat filtering, `--doctor` |
| `Dev/src/reStation.Player/HostContractBridge.cs` / `DebugAutomationHook.cs` | 263 / 232 | 233 / 203 | `reEngine.Host.Contracts.IHostAutomationTarget` bridge & `#if DEBUG || RESTATION_TEST_AUTOMATION` STA loopback |
| `Dev/src/reStation.Tests/*.cs` (`30` suites) | 120..474 | 101..402 | `266/266` isolated tests passing in both `-c Release` and `-c Debug` (`APP_ISOLATED_TEST=1`, `0` warnings) |

---

## 5. Settings & Persistence Ownership Map

| Persistence File / Store | Owning Component | Canonical Keys (`Standard 18` / `19`) |
| :--- | :--- | :--- |
| `<dataDir>\config\settings.json` (`Dev\data\settings.example.json`) | `RecompSettings` + `SettingsMigrationHelper` | Auto-migrated from `<dataDir>\settings.json`; `appId` (`"reslate"`), `executionTierMode`, `backendId`, `resolutionScale`, `isWidescreen16By9`, `frameRateMode`, `targetFps`, `isVsyncEnabled`, `isPgxpEnabled`, `isFpsOverlayEnabled`, `modernActionBindings`, `modernControllerBindings`, `metadataJson` |
| `<dataDir>\settings.controls.json` | `GlobalControllerProfile` | Global PS1 1:1 scancode bindings |
| `<dataDir>\cache\<moduleId>\<romSha256_16>-reSlate-0.3.0-<modVer>\<moduleId>.Generated.dll` | `PlayerLocalAotCompiler` + `AotCacheManager` | Local machine-compiled AOT assemblies + manifest index |
| `<dataDir>\saves\*.rst1` | `QuickSaveManager` + `QuickSaveTrailer` | Version 1 binary snapshot + `"RSTX"` additive sections (incl. `"MCD2"`) |
| `<dataDir>\memcards\memcard_<id>_slot{1,2}.mcr` | `MemCardMcrStore` + `SioMemCardProtocol` | Dual 128 KB PS1 memory cards (non-destructively migrated from `<dataDir>\saves\memcard_*.mcr`) |
| `Dev\logs\` (`RS-06`) & `<InstallDir>\logs\` | `ReStationLogger` | `runtime.log` (`<=15s` heartbeats), `crashes\crash_*.json`, `screenshots\` |

---

## 6. Failure Modes & Resilience

- **Mid-Stack Quick-Load (`F9`):** When `R[31] != ReturnSentinelPc`, `PsxCpu.DispatchFunction` invokes `PsxCpu.ResumeMidStack` (executing natively via `Tier3ExecutionCoordinator`, or via `PsxInterpreter.ResumeFromSnapshot` only when `--execution-tier interpreterOracle` is selected); `HasCompiledEntry` avoids false positives inside `[TextStart, TextEnd)`.
- **OpenGL Context Creation Failure:** `HostVideoSurface.TrySwitchBackend` preserves window handle, logs exact driver failure, reverts to `software` cleanly, and displays an inline Video-tab warning banner.
- **Joypad / Polled Timer Deadlocks:** `PsxJoypadPort` clears `/ACK` automatically after `96` cycles while keeping `_irqPending` (`0x200`) independent; `PsxTimersAndDma` advances timers before reading/writing `RCnt*`.
- **Unbuilt Offline Ghidra Toolchain:** `PsxDecompPlatformProvider` fails closed on D2 `invokeGhidra=true` (`PSX_GHIDRA_BATCH_FAILED`) while allowing D0–D3 recipe synthesis without network calls (`MISSING_OFFLINE_TOOLCHAIN` diagnostic when toolchain missing in `Full`).
- **Clean-Room & Containment Escapes:** `PsxCleanRoomValidator` & `AppEnvironment.AssertPathContained` throw fail-closed exceptions on path traversal, `ghidra_psx_ldr`, vendor SDK headers, retail media, or `*.g.cs`/`*.Generated.dll`.

---

## 7. Security & Privacy Model

- **Zero-PII Default Path:** User-scope `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.0\` (`v0.2.8` under `reStation\`; `LauncherArgsHelper` + `ReStationInstallerTheme`) eliminates hardcoded drive paths or workstation usernames.
- **No Auto-Start / No Telemetry:** `AllowAutoStart = false`; zero background listeners or network sockets.
- **Human-Gated Legacy Cleanup:** Stranded `v0.1.1` cleanup requires explicit `--migrate-legacy` + `--confirm-legacy-cleanup` (or interactive modal `Yes`) and always preserves `data\rom\`.

---

## 8. Testing Strategy Map

- **Unit & Integration Suite (`reStation.Tests.dll`):** Run via `APP_ISOLATED_TEST=1 dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true` AND `-c Debug -p:TreatWarningsAsErrors=true` (`266/266` passing in both configurations across `30` test files, `0` warnings, `0` failures, `0` skipped) + 2-tier log sweep (`%LOCALAPPDATA%\Programs\...\logs\` read-only + `Dev\logs\`).
- **Debug/Test-Only `Foundation.Automation` Stage 2 Sandbox E2E (`Standard 02 §4`, `v0.3.0`):** `Dev\scripts\run_stage2_sandbox_e2e.ps1` runs `Foundation\Dev\tools\run_sandbox_e2e.py` against the 4-drive Sandbox (`SANDBOX_C_DRIVE`, `SANDBOX_D_DRIVE`, `SANDBOX_REMOVABLE_DRIVE`, `SANDBOX_DISC_DRIVE`, asserting `hostDiffCount == 0`), drives a visible `-p:ReStationTestAutomation=true` Setup Wizard (`InstallerAutomationHook` inside `O:\` sandbox), drives visible SDL3 MediaGate & live `RunHostLoop` F1 Drawer / F3 HUD button interactions + `settings.json` persistence (`DebugAutomationHook` + `SDL3.dll`), and verifies pure `Release` rejects `--automation-port` with `ExitCode = 2` on both `reSlate.Player.exe` (`Assert-BinaryPurity`) and `reSlate-Setup` (`Assert-InstallerPurity`).

---

## 9. Design Decisions Log (ADR)

- **ADR-001 (2026-11-15 · v0.2.0) — Player-PC `<dataDir>\cache\` AOT Primary Over Shipped `.Generated.dll`:** Strip `*.Generated.dll` from shareable `.recomp` bundles and compile locally via `AotCacheManager` + collectible `ModuleAssemblyLoader`, keeping distributed packages cleanly separated from ROM/machine artifacts.
- **ADR-002 (2026-11-15 · v0.2.0) — Bespoke `MPL-2.0` `R3000A_GTE` `.slaspec`:** Replaced `GPLv3` `ghidra_psx_ldr` and vendor SDK headers with original clean-room SLEIGH definitions + `PsxCleanRoomValidator`.
- **ADR-003 (2026-11-15 · v0.2.0) — `IRecompWidescreenProvider` in `reStation.Contracts`:** Kept `Foundation\` strictly read-only while honoring `IsWidescreen16By9 = false` (`4:3`) default aspect until both user setting and game module approve `16:9`.
- **ADR-004 (2026-11-15 · v0.2.0) — Dual-Layer Software UI Compositing & `1060x34` Compact Strip:** Decoupled `SDL_Texture` UI overlay blending from CPU pixel compositing and isolated `F3` HUD rendering from full-screen allocations.
- **ADR-005 (2026-11-15 · v0.2.0) — Multi-Bind `Schema A` & `Schema B` Controller/Isometric Modes:** Unified `[1st, 2nd, 3rd]` KBM/wheel/gamepad bindings (`ModernBindingsJsonConverter`) with `!gameplay` Menu Arrow fallback without breaking `v0.1.x` scalar `GameActionDescriptor` signatures.
- **ADR-006 (2026-11-16 · v0.2.8) — Dynamic `ControlOptions` (`metadataJson`), Hor+ `16:9` Side Wings & Decoupled `60 Hz` VBlank:** Replaced hardcoded pastoral-sim (`MeadowValley BackToEarth`) drawer pills with `IModuleInputProfileProvider.ControlOptions` + `MetadataJsonConverter`, replaced GTE `0.75` squash with unstretched Hor+ `16:9` side wings (`PsxWidescreenGeometry` + `_wideFbo`/`_wideHiBuf` while keeping 24-bit MDEC FMVs at `4:3`), locked outer VBlank cadence to `60.0 Hz`, and implemented BIOS `B0:0x09 CloseEvent` + `ExecOverlay` `IntRP` isolation + bounded SPU stream draining.
- **ADR-007 (2026-11-18 · v0.3.0) — `reSlate` Identity Split & `reEngine.Host v0.3.0` Boundary:** Preserves `reStation.{Contracts,Core}` PE ABI, `reStation.*` namespaces, and repo folder `Personal/reStation` while shipping `reSlate.exe`, `reSlate.Player.exe`, `reSlate.Compiler.exe`, `reSlate.Decomp.dll`, `reSlate-Setup`, `reSlate-v0.3.0-win-x64-Portable.zip`, `engineId: "reSlate"`, `engines\reSlate\`, and `SharedLibs\reSlate\v0.3.0\`; consumes `reEngine.Host.Contracts` (`HostContractBridge` + Debug-only `DebugAutomationHook`) and defers `Vulkan` (`RS-304`), `ParallelCpu` (`RS-305`), and `ISubTickFrameHook`/`hdpacks\` (`RS-306`) to `v0.3.1`.
- **ADR-008 (2026-11-18 · v0.3.0) — Live Native AOT/JIT Mandate (`Zero Silent Emulator Fallback`):** Wires `Tier3ExecutionCoordinator` directly into `PsxCpu.Call`/`DispatchFunction` with `PsxMemoryBus.OnRamWriteRange` coherence, fails closed in `staticAotOnly`, quarantines `PsxInterpreter` exclusively to `--execution-tier interpreterOracle`, and emits canonical `isPureStaticReady`.

---

## 10. Zero-Touch Extension Playbooks

1. **Add a `.decomp` Analysis Stage or Asset Extractor:** Add a helper class in `Dev\src\reStation.Decomp\` (<= 400 lines), register it cleanly inside `PsxDecompPlatformProvider` / `PsxAssetExtractors`, and add a corresponding test in `PsxDecompAndSlaTests.cs`.
2. **Add a Game Module Native Hook or True-Widescreen Rule:** Implement `INativeGameActionHook` and/or `IRecompWidescreenProvider` inside the game `.recomp` assembly without editing `reStation.Core` or `Foundation`.
3. **Add a Settings Key:** Add the property on `RecompSettings` (preserving `[JsonExtensionData]` forward/backward compatibility), mirror its default in `Dev\data\settings.example.json`, and expose its control pill inside `SlideOverSettingsDrawer`.
