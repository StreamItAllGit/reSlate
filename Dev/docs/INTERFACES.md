# reStation — Interfaces & Contracts

**Synced Version:** `0.1.0` (`2026-11-13`)

---

## 1. CLI Contract
- `--module <path>`: Loads `module.recomp.json` (`.recomp` manifest) and associated clean-room compiled game assembly + timing patches.
- `--disc <path>`: Extracts and SHA-256-verifies user media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`) via `Foundation.Retro.MediaContainerExtractor` (when omitted, auto-discovers preloaded user media in the module's gitignored `data\rom\` directory).
- `--backend <opengl|software>`: Selects `IPsxRenderBackend` (`OpenGl33RenderBackend` or `SoftwareCpuRenderBackend` oracle).
- `--fps-mode <original|enhanced>`, `--target-fps <0|30|60|120|144|240|unlocked>`, `--vsync <on|off>`.
- `--fixture <spec.json>`: Executes deterministic `Foundation.Retro.SaveStateFixtureRunner` scene-jump verification and exits with pass/fail code + PNG/WAV artifacts.
- `--data-dir <path>`, `--config <path>`, `--log-dir <path>`, `--test-mode`, `--headless`, `--visible-test`, `--frames <n>`, `--verify`, `--open-settings`, `--enable-logs`, `--show-fps`.

## 2. Core Contracts (`Foundation.Retro` & `reStation.Contracts`)
- `Foundation.Retro.IRecompModuleManifest` (`ModuleId`, `DisplayName`, `PlatformId`, `Version`, `RequiredDiscChecksums`) extended by `IReStationModuleManifest` (`SerialNumber`, `EntryAssembly`, `DefaultBootExecutable`, `ModuleTypeName`, `DefaultFramerateMode`, `SaveStateFixtures`).
- `Foundation.Retro.IRecompRenderBackend` (`BackendId`, `ScaleFactor`, `BeginFrame`, `PresentRgba`) extended by `IPsxRenderBackend` (`Initialize`, `Resize`, `UploadVramRect`, `CopyVramRect`, `FillDisplayRect`, `SubmitPrimitive`, `EndFrameAndPresent`, `CaptureRgba`).
- `Foundation.Retro.SaveStateFixtureRunner`: Deterministic scene-jump harness for multi-level visual (PNG) and audio (WAV) regression checks.

## 3. T1-Locked `reStation.Contracts` Surface (`Dev\src\reStation.Contracts\`)
- `ReStationEngineInfo`: `AppId="restation"`, `EngineId="reStation"`, `Version="0.1.0"`, `CompatibleEngineTrack="0.1.x"`, `PlatformId="ps1"`, `DefaultPreloadRomDir="data/rom"`.
- `IReStationModuleManifest : IRecompModuleManifest` adds `SerialNumber`, `EntryAssembly`, `DefaultBootExecutable`, `ModuleTypeName`, `DefaultFramerateMode` (`RecompTimingMode`, JSON `lockedCap|unlocked`), `EngineVersion`, `CompatibleEngineTrack`, `PreloadRomDir`, `SaveStateFixtures`. `ReStationModuleManifest` (camelCase Standard 18 DTO, superset of `reEngine.Core.Manifests.RecompPackageManifest` incl. `title`, `entryExecutable`, `[JsonExtensionData]`) exposes `JsonOptions`, `LoadFromFile`, `SaveToFile` (atomic), `Validate`.
- `IReStationCpu`: `R`, `Hi`, `Lo`, `Pc`, `Cop0Sr`, `TotalCycles`, `NextEventCycle`, `FrameCount`, `ActiveExeId`, `ShouldUnwind`, `InSubTickRender`, `ExitRequested`, flattened bus `Read8/16/32` + `Write8/16/32`, COP2 `GteReadData/GteWriteData/GteReadControl/GteWriteControl/GteExecute`, `Call`, `JumpIndirect`, `DispatchFunction`, `Syscall`, `Break`, `PollEvents`, `RegisterModule`, `RequestExit`, `Lwl/Lwr/Swl/Swr`, `Mult/Multu/Div/Divu`, `const ReturnSentinelPc = 0xFFFFFFFC`.
- `IRecompModule` (`ExeId`, `ExeName`, `TextStart`, `TextEnd`, `TryExecuteFunction(IReStationCpu, uint)`) and `IReStationHost : IRecompHost` (`Cpu`, `ModuleManifest`, `GameDataDirectory`, `SaveDirectory`, `RegisterModule`, `SetFramePatch`, `LoadExecutable(string, int)`, `RunFrame()`, `GetTelemetry()`) — game modules cast `InitializeAsync`'s host to `IReStationHost`.
- `IGameFramePatch` (`TryInterceptCall(IReStationCpu, uint canonPc)`, `Reset()`).
- `IPsxRenderBackend : IRecompRenderBackend, IDisposable` (`Initialize`, `Resize`, `UploadVramRect`, `CopyVramRect`, `FillDisplayRect`, `SubmitPrimitive`, `EndFrameAndPresent`, `CaptureRgba`) + `PsxRenderBackendIds` (`opengl`, `software`).
- `PsxTelemetrySnapshot` record (`FrameNumber`, `CpuMs`, `GpuMs`, `WaitMs`, `CurrentFps`, `P99FrameMs`, `ResolutionScale`, `BackendId`, `CdSectorsPerSec`, `MdecMacroblocksPerSec`, `ActiveSpuVoices`, `XaDrops`, `AudioFillRatio`, `AudioUnderruns`) with `ToHeartbeatLine()`, `ToHudLine()`, `ToFrameStats()`.

## 4. T1-Locked Cross-Slice `reStation.Core` Signatures (extend additively only)
- `PsxCpu(RecompSettings, string dataDirectory, IPsxRenderBackend, ReStationLogger? = null) : IReStationCpu` — `Gpu`, `Spu`, `Mdec`, `Pgxp`, `Disc`, `FramePatch`, `OnVBlankFrame`, `PendingQuickLoadSlot`, `AttachDisc(DiscManifest)`, `SetControllerState(port, pressedButtons, lx, ly, rx, ry)`, `WriteSnapshotState/ReadSnapshotState`.
- `PsxGpu(RecompSettings, PgxpTracker, IPsxRenderBackend)` — `WriteGp0`, `WriteGp1`, `ReadGpuStat`, `ReadGpuRead`, `TickTo(long, out bool)`, `NextVBlankCycle`, `PresentFrame`, `Vram`, display/draw-state properties, snapshots.
- `PsxSpu(RecompSettings)` — `Output` (`AudioRingBuffer(65536, 2)`), `Read16/Write16`, `WriteDmaWord/ReadDmaWord`, `SubmitXaSectors(ReadOnlySpan<byte>, byte codingInfo)`, `ResetXaState`, `TickSample`, `RenderFrames`, `XaDrops`, `AudioFillRatio`, `AudioUnderruns`, `ActiveVoiceCount`, snapshots.
- `PsxMdec()` — `DmaIn`, `DmaOut`, `ReadStatus`, `WriteControl`, `MacroblocksDecoded`.
- `PsxCdRomTiming.ComputeReadLatencyCycles(byte mode, bool isXaOrForm2, bool isRawMdec, bool shouldFastLoadData)` (`225,792` / `451,584` / Form1-only `32,768`).
- `PgxpTracker` (`Record`, `TryResolve`, `ClearFrame`, `Pack`), `RecompSettings` (`Load`, `Save`, `Clamp`), `CliOptions` (`Parse`, `CreateSandbox`, `ApplyOverrides`, `HelpText`), `ReStationLogger` (`Info/Warn/Error`, `TryHeartbeat`, `GetRecentLines`).
- `DiscManifest`/`DiscFileEntry`/`DiscIngestResult`, `DiscVerifierAndExtractor.IngestAsync(input, dataDir, IReStationModuleManifest, logger?, toolsDir?, ct)`, `QuickSaveManager` (`"RST1"`, `GetSlotPath/Save/TryLoad`), `KeyBindingsConfig` + `BindableAction`, `ModernInputController` + `PsxPadButton` + `HostInputState`.
- `SoftwareCpuRenderBackend()`, `OpenGl33Bindings.Initialize(Func<string, IntPtr>)`, `OpenGl33RenderBackend(OpenGl33Bindings)`.
- Player: `ModuleAssemblyLoader.LoadGameModule(manifest, dir, out ctx)`, `PreloadRomDetector.Detect/TryGetPrimaryMedia`, `FixtureRunnerHost.LoadSpecs/RunAsync`. Compiler: `RecompCompilerOptions`, `RecompCompileResult`, `RecompCompilerPipeline.ParseArgs/Run`.
