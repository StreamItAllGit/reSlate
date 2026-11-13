# Changelog — reStation

All notable changes to `reStation` will be documented in this file. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and Universal SemVer (`MAJOR.MINOR.PATCH`).

## [Unreleased]
### Added
- **T1 Contract-Lock & SDK Sync:** `Dev\src\Directory.Build.props`, 7-project `Dev\src\reStation.slnx`, `Dev\version.txt` (`0.1.0`), `reStation.Contracts` (`IReStationModuleManifest` / `ReStationModuleManifest`, `IReStationCpu`, `IRecompModule`, `IReStationHost`, `IGameFramePatch`, `IPsxRenderBackend`, `PsxTelemetrySnapshot`), `Foundation v0.3.1` + `reEngine v0.1.0` SDK assets in `Dev\lib\`, `reEngine-Setup-v0.1.0-win-x64.exe` in gitignored `Dev\assets\bootstrap\`, and 7-Zip in `Dev\assets\native\`.
- **T2 CPU, GTE, BIOS, Timers/DMA & 2x CD-ROM (`KFR-PARITY-01`):** Implemented `PsxMemoryBus`, `PsxCpu`, `PsxInterpreter` (with `ResumeFromSnapshot`), `PsxGte` + `PgxpTracker`, `PsxTimersAndDma`, `PsxJoypadPort`, `PsxCdRom` (`225,792` @ 2x / `451,584` @ 1x / Form1-only `32,768` cycles, XA & 2340B MDEC routing, `CdSectorsPerSec`), generalized `PsxBiosHle`, and Zero-PII `MemCardMcrStore`.
- **T3 SPU ADSR/XA, MDEC & Disc Ingestion (`KFR-PARITY-02`):** Implemented 4-stage ADSR `SpuVoice` (`KeyOff()` clean exponential decay to `0`), persistent-history `XaAdpcmDecoder` (`3:7` / `6:7` -> `44.1 kHz`), `PsxSpu` (`Foundation.Core.AudioRingBuffer(65_536, 2)` + `XaDrops` telemetry), `PsxMdec` RLE/IDCT, `Iso9660RawReader` / `CueSheetReader` / `DiscVerifierAndExtractor`, `QuickSaveManager` (`"RST1"`), and `ModernInputController`.
- **T4 Pluggable Rendering (`KFR-PARITY-03`):** Implemented `PsxGpu`, deterministic `1x–8x` PGXP `SoftwareCpuRenderBackend`, `OpenGl33Bindings`, `OpenGl33RenderBackend` (`R16UI` VRAM, scaled FBOs, VBO batching, lazy `glReadPixels`), and `IPsxGpuBackendHooks`.
- **T5–T6 Compiler & SDL3/GL Player (`KFR-PARITY-04`):** Implemented `reStation.Compiler` (`<= 320 LOC` `.g.cs` chunks, `IRecompGameModule`, `--compile-dll`) and `reStation.Player` (`Sdl3Native` GL 3.3 Core + renderer fallback, `ModuleAssemblyLoader`, `PreloadRomDetector`, `FirstRunMediaGateView`, `SlideOverSettingsDrawer`, `UiOverlayPresenter`, `FixtureRunnerHost`, `VisibleTestHarness`).
- **T7–T8 Packaging, Authoritative Build Script & Tests:** Authored `reStation.Launcher`, `reStation.Installer` (`AllowAutoStart = false`, `--autostart*`/`--auto-start*` strip, `--test-mode` `reEngine` containment), [`Dev\scripts\build_release.ps1`](Dev/scripts/build_release.ps1), and `reStation.Tests` (`54` methods / `84` passing cases across 8 files).

### Changed
- **Wave 3 Integration & Manual Shelf Sync:** Wired `HostVideoSurface.SyncViewport` -> `Backend.Resize(windowWidth, windowHeight, scale)` so `OpenGl33RenderBackend` blits to live viewport dimensions; aligned `DiscFileEntry.Sha256` JSON tag to `sha256Hex`; added `[JsonExtensionData]` on `RecompSettings`; hardened `--target-fps` CLI validation; updated `Dev\docs\{ARCHITECTURE,INTERFACES}.md` and `.gitignore`.

## [0.1.0] - 2026-11-13
### Added
- Initialized Tier A 6-folder `reStation` repository (`active.state`, `AGENTS.md`, Root Quartet, 4-doc Manual Shelf, and initial architecture blueprint [`0.1.0.2026-11-13.md`](Dev/docs/plans/0.1.0.2026-11-13.md)).
- Created original prism + `'re'` vector/raster logos in [`Dev\assets\reStation.svg`](Dev/assets/reStation.svg), [`reStation.png`](Dev/assets/reStation.png), and 4-size Windows [`reStation.ico`](Dev/assets/reStation.ico).
