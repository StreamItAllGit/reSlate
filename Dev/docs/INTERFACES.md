# reStation — Interfaces & Contracts

**Synced Version:** `0.1.0` (`2026-11-13`)

---

## 1. CLI Contract (`reStation.Player` / `reStation` Launcher)
Parsed fail-closed by [`CliOptions.Parse`](Dev/src/reStation.Core/Core/CliOptions.cs) and routed through `Foundation.Core.SandboxEnvironment`:
- `--module <path>`: Loads `module.recomp.json` (`.recomp` manifest) + compiled `IRecompGameModule` assembly via `ModuleAssemblyLoader`.
- `--disc <path>`: Extracts and SHA-256-verifies user media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`) via `DiscVerifierAndExtractor` (when omitted, `PreloadRomDetector` discovers `<moduleDir>\data\rom\`).
- `--backend <opengl|software>`: Selects `IPsxRenderBackend` (`OpenGl33RenderBackend` default or `SoftwareCpuRenderBackend` oracle).
- `--fps-mode <original|enhanced>`, `--target-fps <0|unlocked|30..500>`, `--vsync <on|off>`.
- `--fixture <spec.json>`: Runs deterministic `Foundation.Retro.SaveStateFixtureRunner` verification and writes PNG + WAV artifacts to `<logDir>\fixtures\`.
- `--data-dir <path>`, `--config <path>`, `--log-dir <path>` *(under `--test-mode`, rejects escapes outside `--data-dir` / sandbox root)*.
- `--test-mode`, `--headless`, `--visible-test`, `--frames <n>`, `--verify`, `--open-settings`, `--enable-logs`, `--show-fps`, `--version`, `--help` / `-h` / `/?`.
  *(Note: `reStation.Launcher` waits synchronously and propagates exit codes whenever `--visible-test`, `--fixture`, `--verify`, `--test-mode`, `--headless`, `--version`, `--help`, `-h`, or `/?` are present.)*

---

## 2. Foundation & reEngine Contracts Consumed
- **`Foundation.Retro`:** `IRecompModuleManifest`, `IRecompRenderBackend`, `IRecompHost`, `IRecompGameModule`, `SaveStateFixtureSpec`, `SaveStateFixtureRunner`, `IVisibleTestHarness`, `MediaContainerExtractor`.
- **`Foundation.Core`:** `SandboxEnvironment`, `GranularSettingsManager<T>`, `CappedSharedLogSink`, `HeartbeatLogger`, `CrashBundleService`, `HighResFramePacer`, `AudioRingBuffer(65_536, 2)`, `PixelOverlayCanvas`, `RgbaPngWriter`, `WavCaptureWriter`, `ToolResolver`.
- **`Foundation.Launcher` & `Foundation.Installer`:** `RootLauncherHost`, `StandardInstallerHost`, `InstallerAppDescriptor`, `InstallerThemeOptions`.
- **`reEngine.Core`:** `RecompPackageManifest`, `RecompPackageReader.DiscoverPreloadMedia`, `ReEngineBootstrapHelper`.

---

## 3. Locked `reStation.Contracts` Surface (`Dev\src\reStation.Contracts\`)
- [`ReStationEngineInfo`](Dev/src/reStation.Contracts/IReStationModuleManifest.cs): `AppId="restation"`, `EngineId="reStation"`, `Version="0.1.0"`, `CompatibleEngineTrack="0.1.x"`, `PlatformId="ps1"`, `DefaultPreloadRomDir="data/rom"`.
- [`IReStationModuleManifest : IRecompModuleManifest`](Dev/src/reStation.Contracts/IReStationModuleManifest.cs): Adds `SerialNumber`, `EntryAssembly`, `DefaultBootExecutable`, `ModuleTypeName`, `DefaultFramerateMode` (`RecompTimingMode` -> `lockedCap|unlocked`), `EngineVersion`, `CompatibleEngineTrack`, `PreloadRomDir`, `SaveStateFixtures`. `ReStationModuleManifest` DTO extends `RecompPackageManifest` parity (`title`, `entryExecutable`, `[JsonExtensionData]`), exposing `JsonOptions`, `LoadFromFile`, `SaveToFile` (atomic), `Validate`.
- [`IReStationCpu`](Dev/src/reStation.Contracts/IReStationCpu.cs): `R`, `Hi`, `Lo`, `Pc`, `Cop0Sr`, `TotalCycles`, `NextEventCycle`, `FrameCount`, `ActiveExeId`, `ShouldUnwind`, `InSubTickRender`, `ExitRequested`, flattened bus (`Read8/16/32`, `Write8/16/32`), COP2 (`GteReadData`, `GteWriteData`, `GteReadControl`, `GteWriteControl`, `GteExecute`), `Call`, `JumpIndirect`, `DispatchFunction`, `Syscall`, `Break`, `PollEvents`, `RegisterModule`, `RequestExit`, `Lwl/Lwr/Swl/Swr`, `Mult/Multu/Div/Divu`, `const ReturnSentinelPc = 0xFFFFFFFC`.
- [`IRecompModule`](Dev/src/reStation.Contracts/IRecompModule.cs): `ExeId`, `ExeName`, `TextStart`, `TextEnd`, `TryExecuteFunction(IReStationCpu, uint)`.
- [`IReStationHost : IRecompHost`](Dev/src/reStation.Contracts/IRecompModule.cs): `Cpu`, `ModuleManifest`, `GameDataDirectory`, `SaveDirectory`, `RegisterModule`, `SetFramePatch`, `LoadExecutable(string, int)`, `RunFrame()`, `GetTelemetry()`.
- [`IGameFramePatch`](Dev/src/reStation.Contracts/IGameFramePatch.cs): `TryInterceptCall(IReStationCpu, uint canonPc)`, `Reset()`.
- [`IPsxRenderBackend : IRecompRenderBackend, IDisposable`](Dev/src/reStation.Contracts/IPsxRenderBackend.cs): `Initialize`, `Resize(width, height, scaleFactor)`, `UploadVramRect`, `CopyVramRect`, `FillDisplayRect`, `SubmitPrimitive`, `EndFrameAndPresent`, `CaptureRgba` + `PsxRenderBackendIds` (`"opengl"`, `"software"`).
- [`PsxTelemetrySnapshot`](Dev/src/reStation.Contracts/PsxTelemetrySnapshot.cs): 14-field record (`FrameNumber`, `CpuMs`, `GpuMs`, `WaitMs`, `CurrentFps`, `P99FrameMs`, `ResolutionScale`, `BackendId`, `CdSectorsPerSec`, `MdecMacroblocksPerSec`, `ActiveSpuVoices`, `XaDrops`, `AudioFillRatio`, `AudioUnderruns`) with `Empty`, `ToHeartbeatLine()`, `ToHudLine()`, `ToFrameStats()`.

---

## 4. `reStation.Core`, `Compiler`, `Player`, `Installer` & Packaging Contracts
- **Core Settings & Logging:** `RecompSettings` (`Load`, `Save`, `Clamp`, Standard 18 camelCase keys + `[JsonExtensionData] ExtensionData`, `UseGlobalFormatRules`, `ShouldEnableRuntimeLogs`); `ReStationLogger` (`Info`/`Warn`/`Error`/`SetEnabled`/`TryHeartbeat`/`GetRecentLines`).
- **CPU / Hardware / BIOS:** `PsxCpu(RecompSettings, dataDirectory, IPsxRenderBackend, ReStationLogger? = null)` exposing `Gpu`, `Spu`, `Mdec`, `Pgxp`, `Disc`, `TimersAndDma`, `JoypadPort`, `FramePatch`, `OnVBlankFrame`, `PendingQuickLoadSlot`, `CdSectorsPerSec`, `AttachDisc`, `ResumeFromSnapshot`, `SetControllerState`, `WriteSnapshotState/ReadSnapshotState`; `PsxCdRomTiming.ComputeReadLatencyCycles` (`225_792` / `451_584` / Form1 `32_768`); `PsxBiosHle` & `MemCardMcrStore`.
- **SPU / XA / MDEC:** `AdsrPhase {Off, Attack, Decay, Sustain, Release}`, `SpuVoice` (`Adsr`, `Phase`, `Active`, `Envelope`, `SustainLevel`, `KeyOn()`, `KeyOff()`, `TickEnvelope()`, `StepSample`), `XaAdpcmDecoder` (`DecodeSector`, `GetMaxOutputFrames`, `GetSourceSampleRate`, `Old1L/Old2L/Old1R/Old2R`, `Reset()`), `PsxSpu` (`Voices`[24], `Output`, `XaQueueCapacityFrames = 32_768`, `XaDrops`, `OutputOverflows`, `XaFramesDecoded`, `AudioFillRatio`, `AudioUnderruns`, `ActiveVoiceCount`, `Read16/Write16`, `SubmitXaSectors`, `TickSample`, `RenderFrames`), `PsxMdec` (`MacroblocksPerSec`, `MacroblocksDecoded`, `OutputWordsAvailable`, `DmaIn`/`DmaOut`/`DmaInBlock`/`DmaOutBlock`, `ReadStatus`, `WriteControl`).
- **Disc Ingestion / Saves / Input:** `DiscManifest` / `DiscFileEntry` (`sha256Hex`, `relativeFile`, `lba`, `byteLength`, `sectorCount`, `isForm2Raw`) / `DiscIngestResult`; `Iso9660RawReader.Open` & `CueSheetReader.Parse`; `DiscVerifierAndExtractor.IngestAsync`; `QuickSaveManager` (`"RST1"` v1, `GetSlotPath`/`Save`/`TryLoad`/`ListSlots`/`Delete`); `KeyBindingsConfig` (`BindableAction`) & `ModernInputController` (`ApplyKeyBindings`, `ApplyToCpu`, `ParseScriptForFrame`).
- **GPU Backends:** [`IPsxGpuBackendHooks`](Dev/src/reStation.Core/Gpu/IPsxGpuBackendHooks.cs) (`AttachPgxp`, `SetTextureWindow`, `TryReadVramRect`); `PsxGpu` (`WriteGp0`/`WriteGp1`, `ReadGpuStat`/`ReadGpuRead`, `TickTo`, `PresentFrame`, `Vram`, `FramesPresented`); `SoftwareCpuRenderBackend()`; `OpenGl33Bindings.Initialize(Func<string, IntPtr>)`; `OpenGl33RenderBackend(OpenGl33Bindings)`.
- **Compiler CLI & Pipeline:** [`RecompCompilerPipeline`](Dev/src/reStation.Compiler/RecompCompilerPipeline.cs) (`ParseArgs`/`Run`) consuming `RecompCompilerOptions` (`--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--compile-dll`) and producing `RecompCompileResult`.
- **Player & Installer:** `ModuleAssemblyLoader.LoadGameModule`, `PreloadRomDetector.Detect`/`TryGetPrimaryMedia`, `HostVideoSurface.SyncViewport`, `FixtureRunnerHost.LoadSpecs`/`RunAsync`, `VisibleTestHarness`, `ReStationInstallerTheme` (`AllowAutoStart = false`), `ReStationInstallerBootstrap.Prepare` (`--test-mode` side-by-side isolation), and single-pass [`Dev\scripts\build_release.ps1`](Dev/scripts/build_release.ps1) emitting `Builds\v<Ver>\`, signed `Installers\reStation-Setup-v<Ver>-win-x64.exe`, 6-file `Releases\Latest\`, `<SHARED_LIBS_ROOT>\reStation\v<Ver>\`, and `Archive\reStation-v<Ver>-src.7z`.
