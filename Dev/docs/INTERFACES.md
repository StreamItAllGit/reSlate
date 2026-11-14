# reStation — Interfaces & Contracts

**Synced Version:** `0.1.3` (`2026-11-14`)

---

## 1. CLI Contract (`reStation.Player` / `reStation` Launcher)
Parsed fail-closed by [`CliOptions.Parse`](Dev/src/reStation.Core/Core/CliOptions.cs) and routed through `Foundation.Core.SandboxEnvironment`:
- `--module <path>`: Loads `module.recomp.json` or a single-file `.recomp` ZIP cartridge + compiled `IRecompGameModule` assembly via [`ModuleAssemblyLoader`](Dev/src/reStation.Player/ModuleAssemblyLoader.cs) (`AssemblyDependencyResolver` for unpacked layouts; in-memory `ZipArchive` / `MemoryStream` `AssemblyLoadContext` for `.recomp` archives).
- `--disc <path>` / `--preload-media <path>`: Extracts and SHA-256-verifies user media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`) via `DiscVerifierAndExtractor` (ingestion priority: `--disc` $\rightarrow$ `--preload-media` $\rightarrow$ `<moduleDir>\data\rom\` via `PreloadRomDetector`).
- `--wait`: Forces `reStation.Launcher` (`LauncherArgsHelper`) to propagate synchronous exit status and supervise child lifetime via Windows `JobObjectGuard`.
- `--backend <opengl|software>`: Selects `IPsxRenderBackend` (`OpenGl33RenderBackend` default or `SoftwareCpuRenderBackend` oracle).
- `--fps-mode <original|enhanced>`, `--target-fps <0|unlocked|30..500>`, `--vsync <on|off>`.
- `--fixture <spec.json>`: Runs deterministic `Foundation.Retro.SaveStateFixtureRunner` verification and writes PNG + WAV artifacts to `<logDir>\fixtures\`.
- `--data-dir <path>`, `--config <path>`, `--log-dir <path>` *(under `--test-mode`, allows sibling `Sandbox\data` + `Sandbox\logs` / `<root>\data` + `<root>\logs` while rejecting any escape outside the resolved sandbox root)*.
- `--test-mode`, `--headless`, `--visible-test`, `--frames <n>`, `--verify`, `--open-settings`, `--enable-logs`, `--show-fps`, `--version`, `--help` / `-h` / `/?`.
  *(Note: `reStation.Launcher` and `reStation.Player` share `--help` + `--version` parity, and [`LauncherArgsHelper.SyncWaitFlags`](Dev/src/reStation.Launcher/LauncherArgsHelper.cs) triggers synchronous wait + exit propagation whenever `--wait`, `--visible-test`, `--fixture`, `--verify`, `--test-mode`, `--headless`, `--version`, `--help`, `-h`, or `/?` are present.)*

---

## 2. Foundation & reEngine Contracts Consumed
- **`Foundation.Retro`:** `IRecompModuleManifest`, `IRecompRenderBackend`, `IRecompHost`, `IRecompGameModule`, `SaveStateFixtureSpec`, `SaveStateFixtureRunner`, `IVisibleTestHarness`, `MediaContainerExtractor`.
- **`Foundation.Core`:** `SandboxEnvironment`, `GranularSettingsManager`, `CappedSharedLogSink`, `HeartbeatLogger`, `CrashBundleService`, `HighResFramePacer`, `AudioRingBuffer(65_536, 2)`, `PixelOverlayCanvas`, `RgbaPngWriter`, `WavCaptureWriter`, `ToolResolver`, `JobObjectGuard`.
- **`Foundation.Launcher` & `Foundation.Installer`:** `RootLauncherHost`, `StandardInstallerHost`, `InstallerAppDescriptor`, `InstallerThemeOptions`.
- **`reEngine.Core`:** `RecompPackageManifest`, `RecompPackageReader.DiscoverPreloadMedia`, `RecompPackageReader.ResolveModuleRootDirectory`, `RecompPackageReader.IsZipArchive`, `ReEngineBootstrapHelper`.

---

## 3. Locked `reStation.Contracts` Surface (`Dev\src\reStation.Contracts\`)
- [`ReStationEngineInfo`](Dev/src/reStation.Contracts/IReStationModuleManifest.cs): `AppId="restation"`, `EngineId="reStation"`, `Version="0.1.3"`, `CompatibleEngineTrack="0.1.x"`, `PlatformId="ps1"`, `DefaultPreloadRomDir="data/rom"`.
- [`IReStationModuleManifest : IRecompModuleManifest`](Dev/src/reStation.Contracts/IReStationModuleManifest.cs): Adds `SerialNumber`, `EntryAssembly`, `DefaultBootExecutable`, `ModuleTypeName`, `DefaultFramerateMode` (`RecompTimingMode` -> `lockedCap|unlocked`), `EngineVersion`, `CompatibleEngineTrack`, `PreloadRomDir`, `SaveStateFixtures`. `ReStationModuleManifest` DTO extends `RecompPackageManifest` parity (`title`, `entryExecutable`, `[JsonIgnore] ResolvedPackagePath`, `[JsonExtensionData]`), exposing `JsonOptions`, `LoadFromFile` (supporting `.json` and ZIP `.recomp` archives), `SaveToFile` (atomic), `Validate`.
- [`IReStationCpu`](Dev/src/reStation.Contracts/IReStationCpu.cs): `R`, `Hi`, `Lo`, `Pc`, `Cop0Sr`, `TotalCycles`, `NextEventCycle`, `FrameCount`, `ActiveExeId`, `ShouldUnwind`, `InSubTickRender`, `ExitRequested`, flattened bus (`Read8/16/32`, `Write8/16/32`), COP2 (`GteReadData`, `GteWriteData`, `GteReadControl`, `GteWriteControl`, `GteExecute`), `Call`, `JumpIndirect`, `DispatchFunction`, `Syscall`, `Break`, `PollEvents`, `RegisterModule`, `RequestExit`, `Lwl/Lwr/Swl/Swr`, `Mult/Multu/Div/Divu`, `const ReturnSentinelPc = 0xFFFFFFFC`.
- [`IReStationCpuEx : IReStationCpu`](Dev/src/reStation.Contracts/IReStationCpuEx.cs): Adds `SubTickDeadlineCycle`, `Span<byte> Ram`, `new long NextEventCycle { get; set; }`, `UnwindTargetPc`, `SkipPolygonRasterization`, `BeginSubTickFrame()`, `HasCompiledEntry(uint)`, `RunSlice(uint entryPc, uint returnSentinelPc = ReturnSentinelPc, bool inSubTickRender = false)`, and `TryAdvanceToNextVBlank()`.
- [`IRecompModule`](Dev/src/reStation.Contracts/IRecompModule.cs) & [`IOverlayRecompModule : IRecompModule`](Dev/src/reStation.Contracts/IOverlayRecompModule.cs): Base slot identity (`ExeId`, `ExeName`, `TextStart`, `TextEnd`, `TryExecuteFunction`), extended for dynamic overlays with `LoadRamAddress`, `ContentSizeBytes`, `ContentSha256Hex`.
- [`IReStationHost : IRecompHost`](Dev/src/reStation.Contracts/IRecompModule.cs) & [`IReStationHostEx : IReStationHost`](Dev/src/reStation.Contracts/IReStationHostEx.cs): Adds `IsHeadless`, `HasPendingCapture`, `TargetFps`, `IsEnhancedFrameRateEnabled`, `IsModernMouseLookActive`, `MouseSensitivity`, `WaitUntilTimestamp(long)`, `PollSubTickLook()`, `PresentSubTickFrame()`.
- [`IGameFramePatch`](Dev/src/reStation.Contracts/IGameFramePatch.cs) & [`ISubTickFrameHook : IGameFramePatch`](Dev/src/reStation.Contracts/ISubTickFrameHook.cs): Adds `SubTickEntryPc`, `MaxSubTickCycles`, `ShouldRunSubTick`, `OnSubTickBegin`, `OnSubTickEnd`, plus default interface implementations `TryInterceptSubTickCall(IReStationCpuEx, uint)` and `OnAfterCall(IReStationCpuEx, uint)`.
- [`IPsxRenderBackend : IRecompRenderBackend, IDisposable`](Dev/src/reStation.Contracts/IPsxRenderBackend.cs): `Initialize`, `Resize(width, height, scaleFactor)`, `UploadVramRect`, `CopyVramRect`, `FillDisplayRect`, `SubmitPrimitive`, `EndFrameAndPresent`, `CaptureRgba` + `PsxRenderBackendIds` (`"opengl"`, `"software"`).
- [`PsxTelemetrySnapshot`](Dev/src/reStation.Contracts/PsxTelemetrySnapshot.cs): 14-field record with `Empty`, `ToHeartbeatMetrics()`, `ToHeartbeatLine()`, `ToHudLine()`, `ToFrameStats()`.

---

## 4. `reStation.Core`, `Compiler`, `Player`, `Installer` & Packaging Contracts
- **Core Settings & Logging:** `RecompSettings` (`Load`, `Save`, `Clamp`, Standard 18 camelCase keys + `[JsonExtensionData] ExtensionData`, `UseGlobalFormatRules`, `ShouldEnableRuntimeLogs`); `ReStationLogger` (`Info`/`Warn`/`Error`/`SetEnabled`/`TryHeartbeat(completed, total, item, force)`/`GetRecentLines` emitting `yyyy-MM-ddTHH:mm:ss.fffZ [v0.1.3] [PID:<pid>/T:<tid:D2>] [LEVEL] [category] <msg>`).
- **CPU / Hardware / BIOS:** `PsxCpu(RecompSettings, dataDirectory, IPsxRenderBackend, ReStationLogger? = null)` exposing dynamic `RegisterModule`, `Bus.NotifyRamWriteRange(addr, len)`, `Gpu`, `Spu`, `Mdec`, `Pgxp`, `Disc`, `TimersAndDma`, `Joypad`, `FramePatch`, `OnVBlankFrame`, `PendingQuickLoadSlot`, `CdSectorsPerSec`, `TryAdvanceToNextVBlank()`, `AttachDisc`, `ResumeFromSnapshot`, `SetControllerState`, `WriteSnapshotState/ReadSnapshotState`, `Dispose()`; `PsxCdRomTiming.ComputeReadLatencyCycles` (`225_792` / `451_584` / Form1 `32_768`); `PsxBiosHle` (`ExecOverlay` calls `FramePatch?.Reset()`) & `MemCardMcrStore`.
- **SPU / XA / MDEC:** `AdsrPhase {Off, Attack, Decay, Sustain, Release}`, `SpuVoice` (`Adsr`, `Phase`, `Active`, `Envelope`, `SustainLevel`, `KeyOn()`, `KeyOff()`, `TickEnvelope()`, `StepSample`), `XaAdpcmDecoder` (`DecodeSector`, `GetMaxOutputFrames`, `GetSourceSampleRate`, `Old1L/Old2L/Old1R/Old2R`, `Reset()`), `PsxSpu` (`Voices`[24], `Output`, `XaQueueCapacityFrames = 32_768`, `XaDrops`, `OutputOverflows`, `XaFramesDecoded`, `AudioFillRatio`, `AudioUnderruns`, `ActiveVoiceCount`, `Read16/Write16`, `SubmitXaSectors`, `TickSample`, `RenderFrames`), `PsxMdec` (`MacroblocksPerSec`, `MacroblocksDecoded`, `OutputWordsAvailable`, `DmaIn`/`DmaOut`/`DmaInBlock`/`DmaOutBlock`, `ReadStatus`, `WriteControl`).
- **Disc Ingestion / Saves / Input:** `DiscManifest` / `DiscFileEntry` (`sha256Hex`, `relativeFile`, `lba`, `byteLength`, `sectorCount`, `isForm2Raw`) / `DiscIngestResult`; `Iso9660RawReader.Open` & `CueSheetReader.Parse`; `DiscVerifierAndExtractor.IngestAsync`; `QuickSaveManager` (`"RST1"` v1 + optional EOF `"RSTX"` trailer via `SaveToFileWithTrailer`/`LoadFromFile`/`QuickSaveLoadResult`, `GetSlotPath`/`Save`/`TryLoad`/`ListSlots`/`Delete`); `QuickSaveTrailer` EOF layout (`uint16 TrailerVersion=1`, `uint16 chunkCount`, `[4B ASCII tag + int32 len + payload]*`, `uint32 LE bodyLength`, `uint32 LE TrailerMagic=0x58545352` `"RSTX"`, bounded `MemoryStream` over GZip payload); `KeyBindingsConfig` (`BindableAction`) & `ModernInputController` (`ApplyKeyBindings`, `ApplyToCpu`, `ParseScriptForFrame`).
- **GPU Backends:** [`IPsxGpuBackendHooks`](Dev/src/reStation.Core/Gpu/IPsxGpuBackendHooks.cs) (`AttachPgxp`, `SetTextureWindow`, `TryReadVramRect`, `RenderUiOverlay`); `PsxGpu` (`WriteGp0`/`WriteGp1`, `ReadGpuStat`/`ReadGpuRead`, `SkipPolygonRasterization`, `FrameDrawOps`, `LastDrawnX`, `LastDrawnY`, `HasLastDrawnOrigin`, `BeginSubTickFrame()`, `TickTo`, `PresentFrame(bool preferLastDrawnOrigin)`, `Vram`, `FramesPresented`); `SoftwareCpuRenderBackend()`; `OpenGl33Bindings.Initialize(Func<string, IntPtr>)`; `OpenGl33RenderBackend(OpenGl33Bindings)` (`QueuedOpCount` FIFO command marshaling in `OpenGl33RenderBackend.Queue.cs`, centered `4:3`/`16:9` aspect blit & hardware alpha `_overlayTex`).
- **Compiler CLI & Pipeline:** [`RecompCompilerPipeline`](Dev/src/reStation.Compiler/RecompCompilerPipeline.cs) (`ParseArgs`/`Run`) consuming `RecompCompilerOptions` (`--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--compile-dll`) and producing `RecompCompileResult`.
- **Player, Launcher, Installer & Packaging:** `ModuleAssemblyLoader` (`AssemblyLoadContext` unpacked & in-memory ZIP `.recomp` cartridges), `PreloadRomDetector.Detect`/`TryGetPrimaryMedia`, `HostVideoSurface` (`SyncViewport`, `PresentRgba`, `SwapGlOnly`), `FixtureRunnerHost.LoadSpecs`/`RunAsync`, `VisibleTestHarness`, `LauncherArgsHelper` (`SyncWaitFlags`, `PathValueFlags`, `AttachJobGuard`), `ReStationInstallerTheme` (`AllowAutoStart = false`, dynamic `<AssemblyName>reStation-Setup-v$(Version)-win-x64</AssemblyName>`), `ReStationInstallerBootstrap.Prepare` / `NormalizeUninstallArgs` (`--test-mode` side-by-side isolation plus base-directory `--uninstall` resolution across `<root>`, `<root>\reEngine`, direct leaf, and omitted `--install-dir`), [`Dev\scripts\sanitize_release_docs.ps1`](Dev/scripts/sanitize_release_docs.ps1) (UTF-8 sanitization & leak checks across `Installers\RELEASE_NOTES.md` and `Releases\*.md`), and single-pass [`Dev\scripts\build_release.ps1`](Dev/scripts/build_release.ps1) emitting `Builds\v<Ver>\` (with `app\LICENSE`, zero `data\rom\`), signed `Installers\reStation-Setup-v<Ver>-win-x64.exe`, max-2 unpacked `Releases\` + `Latest\`, `<SHARED_LIBS_ROOT>\reStation\v<Ver>\` (including `LICENSE`, `sdk-manifest.json`, `engine.manifest.json` with `"supportedEngineTracks": ["0.1.x"]`, and `README.md`), and `Archive\reStation-v<Ver>-src.7z`.

---

## 5. CLI Exit Codes, Environment Variables & File Formats

### 5.1 Process Exit Codes (`reStation.exe`, `reStation.Player.exe`, `reStation-Setup-*.exe`)

| Exit Code | Meaning | Typical Trigger |
| :---: | :--- | :--- |
| `0` | Success / Clean Help or Verify Exit | Normal run, `--help`, `--version`, `--verify` valid media, or `--visible-test` pass |
| `1` | Media / Fixture / Runtime Verification Fault | Missing/corrupt `--module`, invalid `"RST1"`/`"RSTX"` fixture, failed `--verify` |
| `2` | Invalid CLI Usage / Missing Required Flag Value | Unknown flag or dangling option without argument |
| `3` | Fail-Closed Sandbox Containment Violation | Out-of-sandbox `--install-dir`, `--disc`, `--save-dir`, `--data-dir`, or `..` traversal |
| `4` | Unhandled Fatal Emulation / Host Exception | Crash caught by `CrashBundleService` (`crashes\crash_restation_*.json`) |
| `130` | Operator / Signal Interruption | `Ctrl+C` (`SIGINT`) / console close shutdown |

### 5.2 Installer Bootstrapper & Environment Variables
- `RESTATION_INSTALL_DIR`: Seeded cleanly by `ReStationInstallerBootstrap` during live installs (and live GUI `--uninstall` when `--install-dir` is omitted and the binary is not already running from an installed engine folder).
- `RESTATION_LIB_DIR` / `RESTATION_SHARED_DIR`: Optional MSBuild/environment overrides telling downstream `<Game>Recomp` `.csproj` builds where to resolve `<Private>false</Private>` engine assemblies (`SharedLibs\reStation\v0.1.3\` or module `lib\`).
- **`--uninstall` Base-Dir Resolution Rules (`ReStationInstallerBootstrap.NormalizeUninstallArgs`):**
  1. Informational flags (`--help`, `-h`, `/?`, `help`, `--version`, `-v`, `--verify`, `--dry-run`) return `0` without touching `reEngine`.
  2. When `--uninstall` (or `reStation-Uninstall.exe`) executes with explicit `--install-dir` / `--target-dir`, under `--test-mode` `SandboxEnvironment.AssertPathContained` validates containment against `--data-dir` (or sandbox root); paths directly containing `reStation.exe`, `reStation.Player.exe`, `install-manifest.json` (`app\` or root), `.install_manifest.json`, or `reStation-Uninstall.exe` remain unchanged.
  3. Base directories missing direct markers probe `<base>\engines\reStation\v0.1.3` (`ComputeSideBySideDir`) and `<base>\reEngine\engines\reStation\v0.1.3`, rewriting `--install-dir` to the resolved side-by-side leaf before delegating to `StandardInstallerHost`.

---

## 6. Immutable SDK Drop & Packaging Artifacts (`v0.1.3`)

| Artifact / Manifest | Location | Contract / Schema Notes |
| :--- | :--- | :--- |
| **`sdk-manifest.json`** | `SharedLibs\reStation\v0.1.3\` (`+ Latest\`) | `schemaVersion: 1`; SHA-256 + byte-size inventory of Contracts, Core, Compiler, Foundation, Installer EXE, `LICENSE`, `README.md` |
| **`engine.manifest.json`** | `SharedLibs\reStation\v0.1.3\` (`+ Latest\`) | Declares `engineId: "reStation"`, `version: "0.1.3"`, `"supportedEngineTracks": ["0.1.x"]` |
| **Portable Engine Stage** | `Builds\v0.1.3\` | Contains `reStation.exe`, `AGENTS.md`, `app\{reStation.Player.exe, reStation.Compiler.exe, LICENSE, LICENSE-7ZIP.txt, version.txt}`; zero `data\rom\` |
| **Installer Bundle** | `Installers\` (`+ <INSTALLERS_ROOT>\`) | Signed zero-PII `reStation-Setup-v0.1.3-win-x64.exe` + UTF-8 sanitized `RELEASE_NOTES.md` |
| **6-File End-User Release** | `Releases\{v0.1.3,Latest}\` (`+ .7z`) | Installer EXE, sanitized `README.md`, `FEATURES.md`, `RELEASE_NOTES.md`, `CHANGELOG.md`, `SHA256SUMS.txt` |
| **Version Retention** | `Builds\`, `Releases\` | Auto-prunes unpacked `v*` folders past the `2` newest releases while retaining `.7z` archives and historical installers |

---

## 7. Downstream Game Module & Diagnostics Data Contracts

### 7.1 `.recomp` Module ABI (`reStation.Contracts` & `module.recomp.json`)
- **Required Module Manifest Fields:** `schemaVersion`, `moduleId`, `title`, `serialNumber`, `version`, `compatibleEngineTrack` (`"0.1.x"`), `engineVersion` (`"0.1.3"`), `entryAssembly`, `moduleTypeName`, `preloadRomDir` (defaults to `"data/rom"`), `entryExe`, `contentSha256Hex`, and runtime-resolved `ResolvedPackagePath`.
- **Module Lifecycle Interfaces:**
  - `IRecompGameModule` / `IOverlayRecompModule`: Exposes module metadata, static lifted entry points, and `ContentSha256Hex` dynamic overlay dispatch (activated across `PsxBiosHle` overlay loads).
  - `IReStationCpu` / `IReStationCpuEx` / `IReStationHost` / `IReStationHostEx`: Provides lifted code read/write memory bus access, `RegisterModule`, `ISubTickFrameHook` (`IsSubTickRenderGuard`), and host frame/audio/log services.
  - `IGameFramePatch` & `PsxTelemetrySnapshot`: Allows per-game VBlank timing/camera patches (`Reset()` on `ExecOverlay`) and telemetry formatting (`ToHeartbeatLine()`).

### 7.2 Persistence & Runtime Diagnostic Schemas
- **Quick-Save File Layout (`<safeModuleId>_slot<0..9>.rst`):** GZip-compressed `"RST1"` v1 state body optionally followed by an EOF `"RSTX"` trailer (`uint16 TrailerVersion = 1`, `uint16 chunkCount`, length-prefixed `[4B ASCII tag + int32 len + payload]` chunks including `"BIOS"` and `"OVLY"`, `uint32 LE bodyLength`, `uint32 LE TrailerMagic = 0x58545352`), written atomically via `.tmp` replacement and bounded `MemoryStream` reads.
- **Memory Card & Settings Storage:** `MemCardMcrStore` persists standard `131,072`-byte (`16 × 8,192`-byte blocks, `"MC"` header, XOR frame checksum) `.mcr` files per `SerialNumber`/`ModuleId`; `RecompSettings` persists Standard 18 camelCase `settings.json` keys with `[JsonExtensionData] ExtensionData`, `UseGlobalFormatRules`, and `ShouldEnableRuntimeLogs`.
- **Crash Bundle (`CrashBundlePayload`, `schemaVersion: 1`):** Written to `<logDir>\crashes\crash_restation_<yyyyMMdd_HHmmss_fff>.json` with `appId`, `version`, `crashedAtUtc`, `pid`, `osDescription`, `frameworkDescription`, `workingSetBytes`, `exceptionType`, `exceptionMessage`, `stackTrace`, `innerException`, and the trailing `100` `ReStationLogger` entries.

---

## 8. Renderer Backend & Preload Media Discovery Contracts

### 8.1 Dual-Backend Render ABI (`IPsxRenderBackend` & `IPsxGpuBackendHooks`)
- **Backend Implementations:** `OpenGl33RenderBackend` (hardware GL 3.3 Core via `OpenGl33Bindings` + FIFO queue in `OpenGl33RenderBackend.Queue.cs`) and `SoftwareCpuRenderBackend` (deterministic pixel-reference CPU rasterizer).
- **Hook Surface:** `AttachPgxp(PgxpTracker?)`, `SetTextureWindow(...)`, `TryReadVramRect(...)`, and `RenderUiOverlay(rgba, width, height, ...)` (compositing `_overlayTex` `GL_RGBA8` over `4:3` / `16:9` centered letterbox viewports with `4 Hz` closed-drawer throttle).
- **Headless Contract:** Under `--headless` when no interactive GPU context is bound, `HostVideoSurface` logs single-shot `[WARN] [GL_FALLBACK] reason=headless` cleanly and falls back deterministically for `RgbaPngWriter` capture.

### 8.2 Preload Media Resolution Order (`PreloadRomDetector`)
- **Search Order:** Explicit `--disc <path>` -> explicit `--preload-media <path>` -> automatic module `data\rom\` discovery (resolved via `RecompPackageReader.ResolveModuleRootDirectory` relative to both unpacked `module.recomp.json` folders and zipped `app\<Game>.recomp` cartridges).
- **Supported Media Extensions & Containment:** `.chd`, `.bin`+`.cue`, `.iso`, `.7z`, `.zip` (validated through `MediaContainerExtractor` + `SandboxEnvironment.AssertPathContained` against the active module/sandbox boundary).
