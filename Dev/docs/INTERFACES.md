# reSlate — Interfaces, Public Contracts & Schemas

- **Version:** `v0.2.8` *(shipped baseline under former name `reStation`)* | **Next Target:** `v0.3.0` *(`reSlate` Public-Ready Beta · `SupportedEngineTracks: ["0.3.x", "0.2.x", "0.1.x"]`)*
- **Pinned Upstream Dependencies:** `Foundation v0.4.1-hf.1` -> `v0.5.0+` (`Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`, plus Debug/Test-only `Foundation.Automation`) & `reEngine v0.3.0` (`reEngine.Core`, `reEngine.Host` — shipped)

---

## 1. Fast-Path Lookup & Routing Table

| Domain / Task | Assembly | Key Public Types & Contracts |
| :--- | :--- | :--- |
| **`SlateOne` `.decomp` Provider (`D0`–`D5`)** | `reStation.Contracts`, `reSlate.Decomp` (`reStation.Decomp` in `0.2.x`) | `IReStationDecompProvider`, `PsxDecompPlatformProvider`, `AotRecipeSpec`, `PsxGteSlaExtension`, `PsxParityAndByteMatchVerifier`, `PsxD4CraftsmanshipValidator` (`v0.3.0`) |
| **Local AOT Cache & Collectible Load** | `reStation.Core`, `reStation.Player`, `reStation.Compiler` | `PlayerLocalAotCompiler`, `PlayerMediaResolver`, `IRecompCompiledEntryLookup`, `ModuleAssemblyLoader`, `AotRecipeEmitter`, `DiscOverlaySliceResolver`, `ModuleManifestEmitter` |
| **Opt-In Tier 3 IR / JIT / Bring-Up** | `reStation.Core` | `MipsIrLowerer`, `IrBasicBlock`, `RamPageEpochTable`, `RyuJitBlockBackend`, `ThreadedIrBackend`, `Tier3ExecutionCoordinator`, `JitBringupRecorder` |
| **Runtime CPU, GPU & Audio** | `reStation.Contracts`, `reStation.Core` | `IReStationCpu`, `IReStationCpuEx`, `IRecompModule`, `IOverlayRecompModule`, `IPsxRenderBackend`, `IRecompWidescreenProvider`, `PsxGpu`, `PsxJoypadPort`, `PsxTimersAndDma` |
| **Multi-Bind Input (`Schema A`/`B`)** | `reStation.Contracts`, `reStation.Core` | `GamepadBindingCode` (`2001..2032`), `KeyBindingsConfig` (`4..511`, `1001..1007`), `GameActionDescriptor` (`int[<=3]`), `INativeGameActionHook`, `RecompSettings` |
| **Packaging, Migration & CLI** | `reStation.Installer`, `reStation.Launcher`, `Dev\scripts\` | `ReStationInstallerTheme`, `ReStationInstallerBootstrap`, `ReStationLegacyMigrationHelper`, `CliOptions`, `build_release.ps1` |

---

## 2. Public SDK / Library APIs

### 2.1 `reStation.Contracts` (`net8.0`)

#### Clean-Room `.decomp` & AOT Contracts (`IReStationDecompProvider.cs`)
- `ReStationExecutionTierModes`: `StaticAotOnly = "staticAotOnly"`, `AotWithJitFallback = "aotWithJitFallback"`, `AotWithThreadedIr = "aotWithThreadedIr"`, `JitBringup = "jitBringup"`, `InterpreterOracle = "interpreterOracle"`, `Default = AotWithJitFallback`, `TryNormalize(string? mode, out string normalized)`, `Normalize(string? mode)`, `IsValid(string? mode)`, `ToFoundationTierMode(string? mode)`, `FromFoundationTierMode(RecompExecutionTierMode tier)`.
- `IReStationDecompProvider : IDecompPlatformProvider`:
  - `string EngineId { get; }` (`"reStation"`), `string SleighLanguageId { get; }` (`"MIPS:LE:32:R3000A_GTE"`)
  - `Task<AotRecipeSpec> BuildAotRecipeAsync(DecompRunOptions options, CancellationToken ct = default)`
  - `DecompCleanRoomValidationResult ValidateCleanRoomPackage(string packageOrDirectory)`
- `AotRecipeSpec`: `SchemaVersion`, `PlatformId` (`"ps1"`), `ModuleId`, `DiscSerial`, `ExecutionTierMode`, `SleighSpecId`, `TargetArchitecture` (`"R3000A"`), `LittleEndian`, `PrimaryBootExecutable`, `IReadOnlyList<AotRecipeExecutableSpec> Executables`, `IReadOnlyList<uint> EntryAddresses`, `IReadOnlyList<string> CleanRoomArtifacts`, `GeneratedCacheAssemblyName`, `ExtensionData`, `void Validate(string sourceLabel = "aot-recipe.json")`.
- `AotRecipeExecutableSpec`: `BootExePath`, `Sha256`, `LoadAddress`, `EntryAddress`, `GlobalPointer`, `TextSizeBytes`, `SleighLanguageId`.

#### Manifest, CPU, Host & GPU Contracts
- `ReStationEngineInfo` (`IReStationModuleManifest.cs`): `AppId = "restation"`, `EngineId = "reStation"`, `Version = "0.2.8"`, `CompatibleEngineTrack = "0.2.x"`, `PlatformId = "ps1"`, `DefaultPreloadRomDir = "data/rom"`, `DefaultAotRecipePath = "aot-recipe.json"`, `SupportedEngineTracks = ["0.2.x", "0.1.x"]`.
- `IReStationModuleManifest : IRecompModuleManifest` (`ReStationModuleManifest`): `SerialNumber`, `EntryAssembly`, `DefaultBootExecutable`, `ModuleTypeName`, `EngineVersion`, `CompatibleEngineTrack`, `DefaultFramerateMode` (`RecompTimingMode`), `PreloadRomDir`, `SupportedEngineTracks`, `ExecutionTierMode`, `AotRecipe`, `AotRecipeSpec`, `LoadFromFile(string manifestPath)`, `SaveToFile(string manifestPath)`, `Validate(string sourceLabel)`.
- `IRecompModule` / `IOverlayRecompModule : IRecompModule` (`ExeId`, `ExeName`, `TextStart`, `TextEnd`, `TryExecuteFunction(IReStationCpu, uint pc)`, plus overlay `LoadRamAddress`, `ContentSizeBytes`, `ContentSha256Hex`).
- `IReStationCpu` / `IReStationCpuEx`: Register/bus/GTE/MDEC accessors plus `UnwindTargetPc`, `SkipPolygonRasterization`, `BeginSubTickFrame()`, `bool HasCompiledEntry(uint canonPc)`, `void RunSlice(uint entryPc, uint returnSentinelPc = IReStationCpu.ReturnSentinelPc, bool inSubTickRender = false)`, `bool TryAdvanceToNextVBlank()`.
- `IReStationHost` / `IReStationHostEx`: `GameDataDirectory`, `SaveDirectory`, `RegisterModule`, `SetFramePatch`, `LoadExecutable`, `RunFrame`, `GetTelemetry()`, plus `IsOffscreen`, `HasPendingCapture`, `TargetFps`, `IsEnhancedFrameRateEnabled`, `IsModernMouseLookActive`, `WaitUntilTimestamp(long)`, `PresentSubTickFrame()`.
- `IRecompQuickSaveProvider`: `void CaptureQuickSaveChunks(IDictionary<string, byte[]> chunks)`, `void OnQuickSaveLoaded(IReadOnlyDictionary<string, byte[]> chunks)` (additive optional module hook invoked on quick-save/load for custom `"RSTX"` sections such as `"OVLY"`).
- `IPsxRenderBackend : IRecompRenderBackend, IDisposable`: `PsxRenderBackendIds.OpenGl` (`"opengl"`) / `Software` (`"software"`), `Initialize(int initialWidth, int initialHeight, int scaleFactor)`, `Resize(int, int, int)`, `UploadVramRect(...)`, `CopyVramRect(...)`, `FillDisplayRect(...)`, `SubmitPrimitive(...)`, `EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)`.
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

### 2.2 `reStation.Decomp` (`MPL-2.0` Clean-Room Provider)
- `PsxDecompPlatformProvider : IReStationDecompProvider`: `RunDecompAsync(DecompRunOptions, CancellationToken)` (`D0`–`D5`), `BuildAotRecipeAsync(DecompRunOptions, CancellationToken)`, `ValidateCleanRoomOutput(string)`, `ValidateCleanRoomPackage(string)`.
- `PsxCleanExeAndOverlayLoader`: `TryParseExeHeader`, `LoadExeFromFile`, `LoadExeFromBytes`, `LoadExeIntoRam`, `LoadOverlayIntoRam`, `DiscoverProgramLayout`, `LocateOfflineToolchain`, `GenerateGhidraBatchArtifacts`, `ExecuteGhidraBatchAsync`.
- `PsxGteSlaExtension`: `LoadSlaSpecText()`, `ValidateSlaSpec(string)`, `GenerateLanguageDefinitionsXml()`, `GenerateProcessorSpecXml()`, `GenerateCompilerSpecXml()`, `GenerateCleanPsxTypesHeader()`, `ProvisionExtensionDirectory(string targetExtensionDir)`.
- `PsxParityAndByteMatchVerifier`: `ExpectedLp32Layouts`, `VerifyLp32Parity`, `ComputeLp32StructLayouts`, `VerifyCodeBytesRoundTrip`, `VerifyExeByteMatch`, `VerifyExeFileContained`.
- `PsxAssetExtractors`: `AssertGitignoredCacheTarget`, `ExtractTim`, `ExtractVab`, `ExtractSeq`, `ScanAndExtractDirectory`.
- `PsxExportAndAssetPipeline`: `NormalizeGhidraC`, `NormalizeExportDirectory`, `BuildDeterministicAotRecipe`, `ValidateCleanRoomOutput`, `ValidateCleanRoomPackage`.

---

### 2.3 `reStation.Core`, `reStation.Compiler` & Runtime Host APIs
- `PlayerLocalAotCompiler`: `ResolveEffectiveDataDir(string, LocalAotLoadOptions?)`, `ValidateCleanRoomPackage(string, string)`, `EnsureCompiledInCacheAsync(ReStationModuleManifest, AotRecipeSpec, string, LocalAotLoadOptions?, string?)`, `ResolveCompilerToolPath(string?)`.
- `PlayerMediaResolver`: `TryLoadRecipe`, `ResolveAndVerifyMedia`, `ComputeFileSha256Hex`, `TryFindContainerSliceOffset(ReadOnlySpan<byte>, int, string, out int)` (scans `2048`-byte sectors first, then `4`-byte offsets for `<container>#<slice>` entries).
- `ModuleAssemblyLoader` (`reStation.Player`): `Assembly? CompanionAssembly`, `PreloadCompanionAssembly(string assemblyPath)`, collectible `Unloading` `ClearLoadedCompanion` cleanup, `_companionAliases` simple-name checking, `LoadedFromLocalAotCache = true` when companion AOT resolves, and `concreteManifest.AotRecipeSpec ??= aotRecipe` attachment on stream-loaded `.recomp` archives.
- `DiscOverlaySliceResolver` & `ModuleManifestEmitter` (`reStation.Compiler`): Internal `DiscOverlaySliceResolver.Resolve` orders `DiscoveredExe` IDs against `AotRecipeSpec.Executables` and slices `<container>#<slice>` overlays into `IOverlayRecompModule` specs (`IsOverlay = true`, `ContentSha256Hex`); `RecompCompilerPipeline.SelectUniqueSeedsForExe` isolates overlapping module entry seeds; `ModuleManifestEmitter.ResolveGeneratedAssemblySimpleName` sets generated `.Module.csproj` `<AssemblyName>` / `entryAssembly` to match `AotRecipeSpec.GeneratedCacheAssemblyName`.
- `Tier3ExecutionCoordinator` / `RamPageEpochTable` / `MipsIrLowerer` / `JitBringupRecorder`: `SmcDemotionThreshold = 8`, `ExecuteBlock`, `TryInvalidateRamRange`, `CaptureStamp`, `IsStampValid`, `InvalidateRamRange`, `LowerBlock`, `WriteProfileAtomic`.
- `RecompSettings`: `IsWidescreen16By9` (`false` default), `MetadataJson` (`Dictionary<string, string>?`, Standard 18 `"metadataJson"` per-game option bag), `GetModuleOption`, `SetModuleOption`, `IsBindingUsedByAnyModernAction`, `ControllerDpadMode` (`"quickActions"`), `IsometricMoveMode` (`"cropGrid"`), `Clamp()` (preserves consumer `SchemaVersion >= CurrentSchemaVersion` and trimmed non-empty `AppId`), `ModernBindingsJsonConverter` (reads legacy scalar `int` or `int[<=3]` arrays), `GetModernActionBindings`, `GetModernControllerBindings`, `SetModernActionSlot`, `SetModernControllerSlot`, `ClearModernActionSlot`.
- `KeyBindingsConfig`: `MaxScancode = 511`, `MouseLeft = 1001`, `MouseRight = 1002`, `MouseMiddle = 1003`, `MouseX1 = 1004`, `MouseX2 = 1005`, `MouseWheelUp = 1006`, `MouseWheelDown = 1007`, `MaxMouseCode = 1007`, `FormatBindingLabel(int)` (includes SDL3 USB HID Numpad `84..99` `Num/`..`Num.` plus `73` `Ins`, `74` `Home`, `77` `End`, `230` `RAlt`).
- `ReStationLogger`: `TryResolveDevLogsDirectory()` (`RS-06` dev-mode `Dev\logs\` discovery), `TryHeartbeat(PsxTelemetrySnapshot, bool force = false)`.
- `PsxGpu`: `SetBackend(IPsxRenderBackend)` (preserves `1024x512` VRAM, texture window & PGXP), `EffectiveWidescreen`.
- `HostVideoSurface`: `TrySwitchBackend(string backendId, RecompSettings, ReStationLogger, out string? error)` (live frame-boundary GL <-> Software swap with graceful Software revert on GL failure).
- `ReStationLegacyMigrationHelper` (`reStation.Installer`): `NormalizeProfileId`, `ResolveLegacyV011EngineRoots`, `DetectLegacyV011HostArtifacts`, `PersistInstallProfileSettings`, `TryMigrateLegacyHostArtifacts`.

---

## 3. CLI Flags Summary
- **Player / Launcher:** `--module`, `--disc`, `--preload-media`, `--backend`, `--scale`, `--fps-mode`, `--target-fps`, `--vsync`/`--no-vsync`, `--frames`, `--dump-screenshot`, `--data-dir`, `--config`, `--log-dir`, `--port`, `--tray-only`, `--verify`, `--open-settings`, `--enable-logs`, `--show-fps`, `--jit-bringup`, `--force-zero-rwx`, `--execution-tier`, `--profile`, `--reengine-dir`, `--suite-dir`, `--wait`, `--version`, `--help` (`--autostart`/`--auto-start` explicitly rejected).
- **Compiler:** `--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--aot-recipe`, `--execution-tier`, `--emit-clean-room`, `--out-package`, `--compile-dll`, `--compile-generated-dll`, `--data-dir`, `--thin-shell`, `--help`.
- **Installer:** `--profile Lightweight|Full`, `--reengine-dir`, `--suite-dir`, `--install-dir`/`--target-dir`, `--data-dir`, `--config`, `--log-dir`, `--migrate-legacy`, `--confirm-legacy-cleanup`, `--uninstall`, `--silent`/`--quiet`, `--dry-run`, `--verify`, `--tray-only`, `--help`.
- **Release Script (`Dev\scripts\build_release.ps1`):** `-Version`, `-CertThumbprint`, `-SkipSourceArchive`, `-SkipTests`.

---

## 4. File Contracts & Manifests
- **`aot-recipe.json`:** `schemaVersion` (`1`), `platformId` (`"ps1"`), `moduleId`, `discSerial`, `executionTierMode`, `sleighSpecId` (`"MIPS:LE:32:R3000A_GTE"`), `targetArchitecture` (`"R3000A"`), `littleEndian` (`true`), `primaryBootExecutable`, `executables[]`, `entryAddresses[]`, `cleanRoomArtifacts[]`, `generatedCacheAssemblyName`.
- **`module.recomp.json`:** Supports `supportedEngineTracks: ["0.2.x", "0.1.x"]`, `executionTierMode`, `aotRecipe` (`"aot-recipe.json"`), and zero bundled `*.Generated.dll` entries inside clean-room `.recomp` archives.
- **`engine.manifest.json` & `sdk-manifest.json`:** Emitted by `build_release.ps1` (staged inside portable `Builds\v<Ver>\` + `app\`, alongside required `.ready` marker for `reEngine v0.3.0+`) advertising `"engineId": "reSlate"`, `"entryExecutable": "reSlate.exe"`, `"decompProviderAssembly": "app/reSlate.Decomp.dll"`, and `"supportedEngineTracks": ["0.3.x", "0.2.x", "0.1.x"]` (with bidirectional `reSlate` <-> `reStation` alias resolution in `EngineAliasCatalog` and `ReStationModuleManifest.Validate`).
- **Save Format:** Binary `QuickSaveManager` `"RST1"` (`SchemaVersion = 1`) + additive `"RSTX"` trailer blocks.

---

## 5. Versioning & Compatibility Notes
- **Backward Compatibility (`reSlate` Partial Rename):** `reStation.Contracts.dll` and `reStation.Core.dll` retain their PE assembly names (`reStation.*` C# namespaces) so existing `0.1.x` and `0.2.x` `.recomp` cartridges bind cleanly at runtime, while launcher/player/compiler/decomp/installer outputs switch to `reSlate.exe`, `reSlate.Player.exe`, `reSlate.Compiler.exe`, `reSlate.Decomp.dll`, `reSlate-Setup-v<Ver>-win-x64.exe`, and `reSlate-v<Ver>-win-x64-Portable.zip`. `ReStationModuleManifest.Validate` uses bidirectional `reSlate` <-> `reStation` alias matching (`EngineAliasCatalog.AreEquivalent`).
- **Prior `0.2.x` Baseline:** `v0.2.0` introduced additive contracts (`IRecompWidescreenProvider`, `INativeGameActionHook` `NativeGameActionContext` overload, multi-bind `GameActionDescriptor`); `v0.2.1`–`v0.2.8` shipped non-breaking `PATCH+1` updates (`184/184` passing tests).
- **`v0.3.0` Planned Interface Additions & Report Remediations:**
  - **Upstream `reEngine v0.3.0` (`reEngine.Host.dll`) Alignment:** Consumes `reEngine.Host.Contracts` (`HostInputFrame`, `HostRenderBackendKind`, `HostWidgetBounds`, `IHostAutomationTarget`, `HostDrawerTabId`, `HostDrawerDescriptor`, `F3HudTelemetry`) and `reEngine.Host.Input.HostInputMapper`, while keeping slimmed GL33/Vulkan-capable `HostVideoSurface` and `SlideOverSettingsDrawer` (`IHostAutomationTarget`) in `reSlate.Player` alongside `"reEngine.Host"` in `ModuleAssemblyLoader.SharedAssemblyNames`.
  - **CLI & Exit-Code Extensions:** Accepts `reEngine v0.3.0` forwarded flags (`--require-backend`, `--save-dir`, `--fixture`, `--dump-audio`, `--doctor`, `--execution-tier`, `--automation-port`, `--automation-token`), adds `.ready` marker emission, `ExitCode = 6` (`--require-backend`), `"vulkan"` / `"parallelCpu"` backends, `ISubTickFrameHook` + `<dataDir>\hdpacks\` (`ExitCode = 3` containment), `SIO0 0x81` + `B0:0x4E/0x4F/0x50` `_new_card` + `"MCD2"` `"RSTX"` chunk, canonical `data\config\settings.json` (auto-migrated from `<dataDir>\settings.json`), and Debug/Test-only `#if DEBUG || RESTATION_TEST_AUTOMATION` `DebugAutomationHook` (`127.0.0.1` loopback; Release builds exclude `Foundation.Automation.dll` completely and fail closed with `ExitCode = 2`).
  - **Schema & Decomp Contracts (`2026-11-18` Reports):** Adds canonical `"isPureStaticReady"` (`IsPureStaticReady`: `true` only on zero-fallback pure-static completion, never during active JIT bring-up) alongside `[Obsolete]` `"isR4PromotionReady"` read-alias in `BringupProfileDocument` / `JitBringupRecorder`; integrates `DecompD4StrictnessValidator` (`Corpus\tiers\{D1_Raw..D5_ByteExact}` + transition alias, `0` C bodies in `.decomp`/`d4_rewrites\*.json`, `-Wno-everything` ban, `-Wall -Wextra -Wno-unused-parameter` `0` warnings) and `PsxD4CraftsmanshipValidator` (6 decompiler-tell families + cohesive module check).
