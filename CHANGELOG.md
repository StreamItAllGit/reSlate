# Changelog — reStation

All notable changes to `reStation` will be documented in this file. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and Universal SemVer (`MAJOR.MINOR.PATCH`).

## [Unreleased]
### Added
- **T1 Contract-Lock:** `Dev\src\Directory.Build.props` (`net8.0`, `LangVersion 12.0`, `TreatWarningsAsErrors`, Release Zero-PII `PathMap`/`ContinuousIntegrationBuild`/`DebugType none`), 7-project `Dev\src\reStation.slnx`, and `Dev\version.txt` (`0.1.0`).
- Locked `reStation.Contracts` (`IReStationModuleManifest` + `ReStationModuleManifest` Standard 18 DTO + `ReStationEngineInfo`, `IReStationCpu`, `IRecompModule` + `IReStationHost`, `IGameFramePatch`, `IPsxRenderBackend` + `PsxRenderBackendIds`, `PsxTelemetrySnapshot`).
- Compiling cross-slice skeletons for `reStation.Core` (`PsxCpu`, `PsxGpu`, `PsxSpu`, `PsxMdec`, `PgxpTracker`, `PsxCdRomTiming` [`225,792`/`451,584`/`32,768` cycles], `RecompSettings`, `CliOptions`, `ReStationLogger`, `DiscManifest`, `DiscVerifierAndExtractor`, `QuickSaveManager`, `KeyBindingsConfig`, `ModernInputController`, `SoftwareCpuRenderBackend`, `OpenGl33Bindings`, `OpenGl33RenderBackend`), `reStation.Compiler`, `reStation.Player`, `reStation.Launcher`, `reStation.Installer`, and `reStation.Tests` (`ContractSmokeTest`, 8 passing cases).
- Synced `reEngine v0.1.0` SDK (`reEngine.Core.dll/.xml/.deps.json`) into `Dev\lib\`, bundled `reEngine-Setup-v0.1.0-win-x64.exe` into gitignored `Dev\assets\bootstrap\`, and 7-Zip (`7z.exe`, `7z.dll`, `LICENSE-7ZIP.txt`) into `Dev\assets\native\`.

### Changed
- `.gitignore` now ignores `Dev/assets/bootstrap/*.exe` + `Dev/lib/*.exe` while tracking `Dev/lib/*.dll|*.xml|*.deps.json` and `Dev/assets/native/**`.

### Planned
- Consume `Foundation v0.3.1` (`Foundation.Retro v0.1.1`) SDK in `Dev\lib\`.
- Migrate reusable PS1 R3000A CPU, COP0, GTE, MDEC, 2x CD-ROM, SPU/XA, and OpenGL 3.3 + Software rendering runtime into `reStation` (`v0.1.0` execution).

## [0.1.0] - 2026-11-13
### Added
- Initialized Tier A 6-folder `reStation` repository (`active.state`, `AGENTS.md`, Root Quartet, 4-doc Manual Shelf, and initial architecture blueprint [`0.1.0.2026-11-13.md`](Dev/docs/plans/0.1.0.2026-11-13.md)).
- Created original prism + `'re'` vector/raster logos in [`Dev\assets\reStation.svg`](Dev/assets/reStation.svg), [`reStation.png`](Dev/assets/reStation.png), and 4-size Windows [`reStation.ico`](Dev/assets/reStation.ico).
