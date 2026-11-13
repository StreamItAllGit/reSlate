# reStation — System Architecture

**Synced Version:** `0.1.0` (`2026-11-13`)  
**Owner:** `reStation` Engine (`@devv` / `@dean`)

---

## 1. Purpose & Platform Separation
`reStation` isolates **PlayStation 1 (`PS1`) platform engineering** from individual game tracks (`<Game>Recomp`). Games publish slim `.recomp` modules containing clean-room lifted C# assemblies, disc SHA-256 manifests, and per-game frame patches (`IGameFramePatch`), while `reStation` provides:
- Shared **`Foundation.Core`** & **`Foundation.Retro`** infrastructure (`MediaContainerExtractor`, `HighResFramePacer`, `AudioRingBuffer`, `WavCaptureWriter`, `RgbaPngWriter`, `IVisibleTestHarness`, `SaveStateFixtureRunner`).
- **R3000A CPU / COP0 / GTE / BIOS HLE** static recomp execution engine (`NextEventCycle`, `MidDispatch`).
- **Hardware-Timed 2x CD-ROM (`150 sectors/s`)**, **MDEC RLE/IDCT**, and **24-Voice SPU + Form2 XA Audio** with full ADSR release envelopes (`KeyOff -> 0`) and smooth ring-buffered `44.1 kHz` playback.
- **Pluggable Rendering (`IRecompRenderBackend`)**: Hardware `OpenGL 3.3 Core` renderer + `SoftwareCpu` oracle/fallback (`1x`–`8x` PGXP), extensible later to `ParallelCpu` and `SDL3_GPU`.
- **Verbose Telemetry Heartbeats**: Emits `resolutionScale`, active `backend`, `cpuMs`/`gpuMs`/`waitMs`, CD sectors/s, MDEC macroblocks/s, active SPU voices, XA drops, and audio buffer fill percentage at `<= 15s` intervals.
