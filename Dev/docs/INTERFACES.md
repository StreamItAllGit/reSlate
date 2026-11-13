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
