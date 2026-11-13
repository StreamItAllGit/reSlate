# reStation — Interfaces & Contracts

**Synced Version:** `0.1.0` (`2026-11-13`)

---

## 1. CLI Contract
- `--module <path>`: Loads `module.recomp.json` and associated `.recomp` compiled game assembly + timing patches.
- `--disc <path>`: Extracts and SHA-256-verifies user media (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, `.zip`) via `Foundation.Retro.MediaContainerExtractor`.
- `--backend <opengl|software>`: Selects `IRecompRenderBackend`.
- `--fps-mode <original|enhanced>`, `--target-fps <n|unlocked>`, `--vsync <on|off>`.
- `--data-dir <path>`, `--config <path>`, `--log-dir <path>`, `--test-mode`, `--headless`, `--visible-test`, `--frames <n>`, `--enable-logs`.

## 2. Core Contracts (Consumed from `Foundation.Retro` & Extended in `reStation`)
- `IRecompModuleManifest`: `moduleId`, `title`, `platformId`, `serial`, `entryAssembly`, `discChecksums[]`, `defaultFramerateMode`, `saveStateFixtures[]`.
- `IRecompRenderBackend`: `Initialize`, `SubmitPrimitives`, `Resize`, `PresentFrame`, `CaptureRgba`.
- `SaveStateFixtureRunner`: Deterministic scene-jump harness for multi-level visual (PNG) and audio (WAV) regression checks.
