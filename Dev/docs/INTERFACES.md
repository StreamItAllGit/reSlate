# reSlate — Interfaces, Public Contracts & Schemas

- **Version:** `v0.3.0` *(GitHub Public-Ready Beta · `appId: "reslate"` · `engineId: "reSlate"` · `SupportedEngineTracks: ["0.3.x", "0.2.x", "0.1.x"]`)*
- **Pinned Upstream Dependencies:** `Foundation v0.5.0` (`Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`, `Foundation.Launcher`, plus Debug/`RESTATION_TEST_AUTOMATION`-only `Foundation.Automation`) & `reEngine v0.3.0` (`reEngine.Core`, `reEngine.Host`)

---

## 1. Fast-Path Lookup & Routing Table

| Domain / Task | Assembly | Key Public Types & Contracts |
| :--- | :--- | :--- |
| **`SlateOne` `.decomp` Provider (`D0`–`D5`)** | `reStation.Contracts`, `reSlate.Decomp` | `IReStationDecompProvider`, `PsxDecompPlatformProvider`, `AotRecipeSpec`, `PsxGteSlaExtension`, `PsxParityAndByteMatchVerifier`, `PsxD4CraftsmanshipValidator` |
| **Local AOT Cache & Collectible Load** | `reStation.Core`, `reSlate.Player`, `reSlate.Compiler` | `PlayerLocalAotCompiler`, `PlayerMediaResolver`, `IRecompCompiledEntryLookup`, `ModuleAssemblyLoader`, `AotRecipeEmitter`, `DiscOverlaySliceResolver`, `ModuleManifestEmitter` |
| **Live Tier 3 Native JIT / IR** | `reStation.Core` | `MipsIrLowerer`, `IrBasicBlock`, `RamPageEpochTable`, `RyuJitBlockBackend`, `ThreadedIrBackend`, `Tier3ExecutionCoordinator`, `JitBringupRecorder` (`IsPureStaticReady`) |
| **Runtime CPU, GPU, SIO0 & Audio** | `reStation.Contracts`, `reStation.Core` | `IReStationCpu`, `IReStationCpuEx`, `IRecompModule`, `IOverlayRecompModule`, `IPsxRenderBackend`, `IRecompWidescreenProvider`, `PsxGpu`, `SioMemCardProtocol`, `MemCardMcrStore`, `PsxBiosHle.MemCard` |
| **Multi-Bind Input, Host Bridge & CLI** | `reStation.Contracts`, `reStation.Core`, `reSlate.Player` | `GamepadBindingCode`, `KeyBindingsConfig`, `GameActionDescriptor`, `SettingsMigrationHelper`, `CliOptions`, `HostContractBridge`, `DebugAutomationHook` (`#if DEBUG \|\| RESTATION_TEST_AUTOMATION`) |
| **Packaging, Migration & Sandbox E2E** | `reStation.Installer`, `reStation.Launcher`, `Dev\scripts\` | `ReStationInstallerTheme`, `ReStationInstallerBootstrap`, `ReStationLegacyMigrationHelper`, `LauncherArgsHelper`, `build_release.ps1`, `package_helpers.ps1`, `run_stage2_sandbox_e2e.ps1` |

---

## 2. Public SDK / Library APIs

### 2.1 `reStation.Contracts` (`net8.0`)

#### Clean-Room `.decomp` & AOT Contracts (`IReStationDecompProvider.cs`)
- `ReStationExecutionTierModes`: `StaticAotOnly = "staticAotOnly"`, `AotWithJitFallback = "aotWithJitFallback"`, `AotWithThreadedIr = "aotWithThreadedIr"`, `JitBringup = "jitBringup"`, `InterpreterOracle = "interpreterOracle"`, `Default = AotWithJitFallback`, `TryNormalize(string? mode, out string normalized)`, `Normalize(string? mode)`, `IsValid(string? mode)`, `ToFoundationTierMode(string? mode)`, `FromFoundationTierMode(RecompExecutionTierMode tier)`.
- `IReStationDecompProvider : IDecompPlatformProvider`:
  - `string EngineId { get; }` (`"reSlate"`), `string SleighLanguageId { get; }` (`"MIPS:LE:32:R3000A_GTE"`)
  - `Task<AotRecipeSpec> BuildAotRecipeAsync(DecompRunOptions options, CancellationToken ct = default)`
  - `DecompCleanRoomValidationResult ValidateCleanRoomPackage(string packageOrDirectory)`
- `AotRecipeSpec`: `SchemaVersion`, `PlatformId` (`"ps1"`), `ModuleId`, `DiscSerial`, `ExecutionTierMode`, `SleighSpecId`, `TargetArchitecture` (`"R3000A"`), `LittleEndian`, `PrimaryBootExecutable`, `IReadOnlyList<AotRecipeExecutableSpec> Executables`, `IReadOnlyList<uint> EntryAddresses`, `IReadOnlyList<string> CleanRoomArtifacts`, `GeneratedCacheAssemblyName`, `ExtensionData`, `void Validate(string sourceLabel = "aot-recipe.json")`.
- `AotRecipeExecutableSpec`: `BootExePath`, `Sha256`, `LoadAddress`, `EntryAddress`, `GlobalPointer`, `TextSizeBytes`, `SleighLanguageId`.

#### Manifest, CPU, Host & GPU Contracts
- `ReStationEngineInfo` (`IReStationModuleManifest.cs`): `AppId = "reslate"`, `EngineId = "reSlate"`, `Version = "0.3.0"`, `CompatibleEngineTrack = "0.3.x"`, `PlatformId = "ps1"`, `DefaultPreloadRomDir = "data/rom"`, `DefaultAotRecipePath = "aot-recipe.json"`, `SupportedEngineTracks = ["0.3.x", "0.2.x", "0.1.x"]`.
- `IReStationModuleManifest : IRecompModuleManifest` (`ReStationModuleManifest`): `SerialNumber`, `EntryAssembly`, `DefaultBootExecutable`, `ModuleTypeName`, `EngineVersion`, `CompatibleEngineTrack`, `DefaultFramerateMode` (`RecompTimingMode`), `PreloadRomDir`, `SupportedEngineTracks`, `ExecutionTierMode`, `AotRecipe`, `AotRecipeSpec`, `LoadFromFile(string manifestPath)`, `SaveToFile(string manifestPath)`, `Validate(string sourceLabel)` (bidirectional `EngineAliasCatalog.AreEquivalent` check accepting `"reSlate"` and `"reStation"`).
- `IRecompModule` / `IOverlayRecompModule : IRecompModule` (`ExeId`, `ExeName`, `TextStart`, `TextEnd`, `TryExecuteFunction(IReStationCpu, uint pc)`, plus overlay `LoadRamAddress`, `ContentSizeBytes`, `ContentSha256Hex`).
- `IReStationCpu` / `IReStationCpuEx`: Register/bus/GTE/MDEC accessors plus `UnwindTargetPc`, `SkipPolygonRasterization`, `BeginSubTickFrame()`, `bool HasCompiledEntry(uint canonPc)`, `void RunSlice(uint entryPc, uint returnSentinelPc = IReStationCpu.ReturnSentinelPc, bool inSubTickRender = false)`, `bool TryAdvanceToNextVBlank()`.
- `IReStationHost` / `IReStationHostEx`: `GameDataDirectory`, `SaveDirectory`, `RegisterModule`, `SetFramePatch`, `LoadExecutable`, `RunFrame`, `GetTelemetry()`, plus `IsOffscreen`, `HasPendingCapture`, `TargetFps`, `IsEnhancedFrameRateEnabled`, `IsModernMouseLookActive`, `WaitUntilTimestamp(long)`, `PresentSubTickFrame()`.
- `IRecompQuickSaveProvider`: `void CaptureQuickSaveChunks(IDictionary<string, byte[]> chunks)`, `void OnQuickSaveLoaded(IReadOnlyDictionary<string, byte[]> chunks)` (additive optional module hook invoked on quick-save/load for custom `"RSTX"` sections such as `"OVLY"` and built-in `"MCD2"`).
- `IPsxRenderBackend : IRecompRenderBackend, IDisposable`: `PsxRenderBackendIds.OpenGl` (`"opengl"`), `Software` (`"software"`), plus `Vulkan` (`"vulkan"`) and `ParallelCpu` (`"parallelCpu"`) identifiers reserved for `v0.3.1`, `Initialize(int initialWidth, int initialHeight, int scaleFactor)`, `Resize(int, int, int)`, `UploadVramRect(...)`, `CopyVramRect(...)`, `FillDisplayRect(...)`, `SubmitPrimitive(...)`, `EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)`.
- `IRecompWidescreenProvider`: `bool IsActiveTrueWidescreenSupported(IReStationCpu cpu)`.
- `IGameFramePatch` / `ISubTickFrameHook`: Per-call interception & enhanced-FPS sub-tick hooks.
- `PsxTelemetrySnapshot`: `ToHeartbeatMetrics()`, `ToHeartbeatLine()`, `ToHudLine()`, `ToFrameStats()`.

#### Multi-Bind & `Schema B` Input Contracts
- `GamepadBindingCode`: `None = 0`, `MinCode = 2001`, `MaxCode = 2032` (`South = 2001` .. `Paddle4 = 2023`, `DPadUp..Right = 2012..2015`, `LT = 2016`, `RT = 2017`, `Touchpad = 2020`), `StandardCodes`, `IsKeyboardCode(int)`, `IsMouseCode(int)`, `IsGamepadCode(int)`, `IsTriggerCode(int)`, `IsValidBindingCode(int)`, `Normalize(int)`, `FromSdlButtonIndex(int)`, `TryToSdlButtonIndex(int, out int)`, `FormatLabel(int)`.
- `GameActionDescriptor` (`IModuleInputProfileProvider`):
  - Multi-bind constructor: `(string ActionId, string Label, IReadOnlyList<int>? DefaultBindingCodes, IReadOnlyList<int>? DefaultControllerCodes, ushort GameplayPadMask = 0, ushort MenuPadMask = 0, bool IsQuickSave = false, bool IsQuickLoad = false)` (`MaxBindingsPerDevice = 3`).
  - Legacy `v0.1.x` compatible constructor: `(string ActionId, string Label, int DefaultBindingCode, ushort GameplayPadMask = 0, ushort MenuPadMask = 0, bool IsQuickSave = false, bool IsQuickLoad = false)`.
- `IModuleInputProfileProvider`: `ProfileDisplayName`, `IReadOnlyList<GameActionDescriptor> ModernActions`, optional `IReadOnlyList<GameControlOptionDescriptor> ControlOptions` (additive `GameControlOptionChoice` + `GameControlOptionDescriptor`), `bool IsGameplayContextActive(IReStationCpu cpu)`.
- `INativeGameActionHook` & `NativeInputModeIds`:
  - `ControllerDpadQuickActions = "quickActions"`, `ControllerDpadWalkStickRun = "dpadWalkStickRun"`, `ControllerStickWalkDpadRun = "stickWalkDpadRun"`, `IsometricMoveCropGrid = "cropGrid"`, `IsometricMoveScreenSpace = "screenSpace"`.
  - `NativeGameActionContext`: `(float MoveX, float MoveY, float LookX, float LookY, string ControllerDpadMode, string IsometricMoveMode, float RawLeftStickX, float RawLeftStickY, bool ControllerConnected)` plus optional `ModuleOptions` (`IReadOnlyDictionary<string, string>?`) & `GetOption(string optionId, string defaultValue = "")` (9-param ctor and legacy tuple overload preserved).

---

### 2.2 `reSlate.Decomp` (`MPL-2.0` Clean-Room Provider)
- `PsxDecompPlatformProvider : IReStationDecompProvider`: `RunDecompAsync(DecompRunOptions, CancellationToken)` (`D0`–`D5`), `BuildAotRecipeAsync(DecompRunOptions, CancellationToken)`, `ValidateCleanRoomOutput(string)`, `ValidateCleanRoomPackage(string)`.
- `PsxD4CraftsmanshipValidator`: Validates `D4_CleanSource` (with transition read-alias `D4_FunctionalRewrite`) via `reEngine.Core.Decomp.DecompD4StrictnessValidator`, scans all 6 decompiler-tell families, rejects `_01.._NN.c` fragments and `-Wno-everything`/`-Wno-all`/`-w` compiler suppressions, verifies `-Wall -Wextra -Wno-unused-parameter` (`0` warnings), and validates Standard 18 `camelCase` `d4b_lockstep_report.json`.
- `PsxCleanExeAndOverlayLoader`, `PsxGteSlaExtension`, `PsxParityAndByteMatchVerifier`, `PsxAssetExtractors`, `PsxExportAndAssetPipeline`: Clean-room PS-X EXE discovery, SLEIGH `R3000A_GTE` provisioning, LP32/byte-match checks, TIM/VAB/SEQ extraction, and cohesive subsystem export hints.

---

### 2.3 `reStation.Core`, `reSlate.Compiler`, `reSlate.Player` & Installer APIs
- `PlayerLocalAotCompiler` & `PlayerMediaResolver`: Resolves `reSlate.Compiler` -> `reStation.Compiler` fallback, validates zero `*.Generated.dll` inside `.recomp`, and extracts `<container>#<slice>` overlays.
- `Tier3ExecutionCoordinator` / `BringupProfileDocument` / `JitBringupRecorder`: Live native RyuJIT & Zero-RWX Threaded-IR execution wired into `PsxCpu`; emits canonical `"isPureStaticReady"` (`CompiledDispatchCount > 0 && JitDispatchCount == 0 && ThreadedIrDispatchCount == 0 && InterpreterFallbackCount == 0 && !IsSpinWatchdogTripped`, `false` during active bring-up) + `[Obsolete]` `"isR4PromotionReady"`, plus `[JsonIgnore]` `IsBringupCaptureClean`.
- `SioMemCardProtocol` / `PsxBiosHle.MemCard` / `MemCardMcrStore` / `QuickSaveTrailer`: Hardware `SIO0 0x81` (`0x52`/`0x57`/`0x53`, explicit dock `0x58..0x5F` NACK, `FLAG 0x08`) + BIOS `B0:0x4E/0x4F/0x50` `_new_card` over Slot 1+2 `<dataDir>\memcards\` (auto-migrating `<dataDir>\saves\memcard_*.mcr`) and `"MCD2"` `"RSTX"` quick-saves.
- `SettingsMigrationHelper` & `CliOptions`: `MigrateForConfigPathIfNeeded(string fullConfigPath)` atomic `<dataDir>\settings.json` -> `<dataDir>\config\settings.json` migration; `IsDeferredBackendRequired(out string?)` (`ExitCode = 6`).
- `HostContractBridge` & `DebugAutomationHook`: `IHostAutomationTarget` + `HostInputMapper` bridge in `reSlate.Player`; `DebugAutomationHook` (`#if DEBUG || RESTATION_TEST_AUTOMATION`) loopback server stripped completely from `Release` (`ExitCode = 2`).
- `ReStationInstallerBootstrap` / `ReStationLegacyMigrationHelper` (`reSlate-Setup`): Embedded `installer.manifest.json`, `.ready` marker emission, `data\config\settings.json` migration, 3-mode sandbox mutation guard.

---

## 3. CLI Flags Summary
- **Player / Launcher (`reSlate.exe` / `reSlate.Player.exe`):** `--module`, `--disc`, `--preload-media`, `--backend opengl|software`, `--require-backend <id>` (`ExitCode = 6` on deferred `vulkan`/`parallelCpu`), `--scale`, `--fps-mode`, `--target-fps`, `--vsync`/`--no-vsync`, `--frames`, `--dump-screenshot`, `--dump-audio`, `--data-dir`, `--config` (default `config/settings.json`), `--log-dir`, `--save-dir`, `--fixture`, `--port`, `--automation-port` / `--automation-token` (`Debug`/`RESTATION_TEST_AUTOMATION` only; `ExitCode = 2` in `Release`), `--tray-only`, `--dry-run`, `--verify`, `--open-settings`, `--enable-logs`, `--show-fps`, `--jit-bringup`, `--force-zero-rwx`, `--execution-tier`, `--profile`, `--reengine-dir`, `--suite-dir`, `--doctor`, `--wait`, `--version`, `--help`.
- **Compiler (`reSlate.Compiler.exe`):** `--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--aot-recipe`, `--execution-tier`, `--emit-clean-room`, `--out-package`, `--compile-dll`, `--compile-generated-dll`, `--data-dir`, `--thin-shell`, `--help`.
- **Installer (`reSlate-Setup-v<Ver>-win-x64.exe`):** `--profile Lightweight|Full`, `--reengine-dir`, `--suite-dir`, `--install-dir`/`--target-dir`, `--data-dir`, `--config`, `--log-dir`, `--migrate-legacy`, `--confirm-legacy-cleanup`, `--uninstall`, `--silent`/`--quiet`, `--dry-run`, `--verify`, `--tray-only`, `--help`.
- **Release & E2E Scripts (`Dev\scripts\`):** `build_release.ps1` (`-Version`, `-CertThumbprint`, `-AuditScriptPath`, `-SkipSourceArchive`, `-SkipTests`); `run_stage2_sandbox_e2e.ps1` (`-Version`, `-InstallerPath`, `-TestAutomationExe`, `-ReleaseExe`, `-SandboxRoot`, `-RequireAllFourDrives`, `-SkipInstallerSandbox`, `-SkipUiAutomation`, `-SkipReleaseGate`, `-DryRun`, `-TimeoutSeconds`).

---

## 4. File Contracts & Manifests
- **`aot-recipe.json`:** `schemaVersion` (`1`), `platformId` (`"ps1"`), `moduleId`, `discSerial`, `executionTierMode`, `sleighSpecId` (`"MIPS:LE:32:R3000A_GTE"`), `targetArchitecture` (`"R3000A"`), `littleEndian` (`true`), `primaryBootExecutable`, `executables[]`, `entryAddresses[]`, `cleanRoomArtifacts[]`, `generatedCacheAssemblyName`.
- **`module.recomp.json`:** Supports `engineId: "reSlate"` or legacy `"reStation"`, `supportedEngineTracks: ["0.3.x", "0.2.x", "0.1.x"]`, `executionTierMode`, `aotRecipe` (`"aot-recipe.json"`), and zero bundled `*.Generated.dll` entries inside clean-room `.recomp` archives.
- **`engine.manifest.json`, `.ready` & `sdk-manifest.json`:** Staged by `build_release.ps1` in `Builds\v<Ver>\` (`.ready` + `engine.manifest.json` with `"decompProviderAssembly": "app/reSlate.Decomp.dll"`) and in `app\` & `SharedLibs\reSlate\v<Ver>\` (`"decompProviderAssembly": "reSlate.Decomp.dll"`), advertising `"engineId": "reSlate"`, `"appId": "reslate"`, `"entryExecutable": "reSlate.exe"`, and `"supportedEngineTracks": ["0.3.x", "0.2.x", "0.1.x"]`.
- **Save & Memory Card Formats:** `QuickSaveManager` `"RST1"` (`SchemaVersion = 1`) + `"RSTX"` sections (`"MCD2"`, `"OVLY"`); 128 KB raw `.mcr` slots in `<dataDir>\memcards\`.

---

## 5. Versioning & Compatibility Notes
- **Backward Compatibility (`reSlate` Partial Rename):** `reStation.Contracts.dll` and `reStation.Core.dll` retain their PE assembly names (`reStation.*` C# namespaces) so existing `0.1.x` and `0.2.x` `.recomp` cartridges bind cleanly at runtime, while launcher/player/compiler/decomp/installer outputs switch to `reSlate.exe`, `reSlate.Player.exe`, `reSlate.Compiler.exe`, `reSlate.Decomp.dll`, `reSlate-Setup-v<Ver>-win-x64.exe`, and `reSlate-v<Ver>-win-x64-Portable.zip`. `ReStationModuleManifest.Validate` uses bidirectional `reSlate` <-> `reStation` alias matching (`EngineAliasCatalog.AreEquivalent`).
- **Scope Split (`v0.3.0` vs `v0.3.1`):** `v0.3.0` ships `reSlate` identity, `reEngine.Host.Contracts` bridge, live Tier 3 native JIT/IR (`isPureStaticReady`), `SIO0 0x81` + BIOS `_new_card` memory cards, `60 Hz` SPU lock + single-edge menu input, `D4_CleanSource` craftsmanship verification, and Debug/Test Stage 2 Sandbox E2E (`258/258` Release & Debug tests). Shared cross-console `Vulkan`, `ParallelCpu`, and `ISubTickFrameHook`/`hdpacks\` defer cleanly to `v0.3.1` alongside `reEngine.Host`.
