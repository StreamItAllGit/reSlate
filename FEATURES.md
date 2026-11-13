# reStation — Feature Map & Architecture Registry

> Maintained by `@tessa`. Maps planned and active `reStation` capabilities (`v0.1.0`) to their target slices and shared `Foundation` contracts.

---

## 1. At-a-Glance Capability Matrix

| Capability | Status | Target Slice / Dependency | Summary |
| :--- | :--- | :--- | :--- |
| **Brand Logo & Identity** | `Live` | [`Dev\assets\reStation.*`](Dev/assets/reStation.svg) | Original SVG, `512px` PNG, and 4-size Windows `.ico` prism monogram icon. |
| **`Foundation` SDK Integration** | `In Progress` | `Dev\lib\` (`Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`) | Shared frame pacer, SPU/XA audio ring buffer, `MediaContainerExtractor` (`.iso`/`.bin`+`.cue`/`.chd`/`.7z`/`.zip`), PNG/WAV capture, `--visible-test`, and zero-Startup installer. |
| **PS1 R3000A CPU, COP0, GTE & BIOS HLE** | `Planned (v0.1.0)` | `reStation.Core\Cpu\` | Reusable static recomp runtime, `NextEventCycle` scheduling, `MidDispatch`, and Psy-Q HLE shims. |
| **Hardware-Timed 2x CD-ROM + MDEC + SPU/XA** | `Planned (v0.1.0)` | `reStation.Core\Audio\`, `CdRom\`, `Mdec\` | `150 sectors/s` 2x CD pacing, ADSR release envelopes to `0`, Gaussian/sweep mixing, and ring-buffered `44.1 kHz` stereo output. |
| **Pluggable `IRecompRenderBackend` (`GL33` / `Software`)** | `Planned (v0.1.0)` | `reStation.Core\Gpu\` | Hardware OpenGL 3.3 Core PS1 renderer + `SoftwareCpu` oracle/fallback (`1x`–`8x`, PGXP, `4:3`/`16:9`). |
| **Modular `.recomp` Loader & `reEngine` Integration** | `Planned (v0.1.0)` | `reStation.Player\Modules\` | Registers under `reEngine`, installs side-by-side in `engines\reStation\v<Ver>\` (`SharedLibs\reStation\v<Ver>\` history), auto-detects `data\rom\`, and loads `.recomp` modules (`compatibleEngineTrack` + `>= engineVersion`). |
| **Multi-Scene Parity & `--visible-test` Suite** | `Planned (v0.1.0)` | `reStation.Tests\` & `Sandbox\` | Automated save-state scene hops, WAV/PNG capture parity checks, verbose telemetry heartbeats, and foreground `--visible-test` smoke verification. |
