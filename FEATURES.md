# reStation — Feature Map & Architecture Registry

> Maintained by `@tessa`. Maps active (`v0.1.0`) and planned (`v0.2.0` $\rightarrow$ `v2.0.0`) `reStation` capabilities to slices in `Dev\src\` and shared `Foundation` contracts. Status tokens strictly follow [Standard 16 §3.2](<PROJECTS_ROOT>/Personal/_DevBible/16_PROJECT_DOSSIERS_AND_FEATURE_TRACKING.md) (`Live`, `In-Dev`, `Planned`, `Deprecated`).

---

## 1. At-a-Glance Capability Matrix

| Code | Capability | Status | Target Version | Target Slice / Dependency | Summary |
| :--- | :--- | :---: | :---: | :--- | :--- |
| `REST-01` | **Brand Logo & Identity** | `Live` | `v0.1.0` | [`Dev\assets\reStation.*`](Dev/assets/reStation.svg) | Original prism + `re` monogram vector `.svg`, `512px` `.png`, and 4-size Windows `.ico`. |
| `REST-02` | **`Foundation` SDK Integration** | `Live` | `v0.1.0` | `Dev\lib\` (`Foundation.Core`, `Foundation.Retro`, `Foundation.Installer`) | Shared frame pacer, ring-buffered SPU/XA mixing, `MediaContainerExtractor` (`.iso`/`.bin`+`.cue`/`.chd`/`.7z`/`.zip`), PNG/WAV capture, `--visible-test`, and zero-Startup installer. |
| `REST-03` | **PS1 R3000A CPU, COP0, GTE & BIOS HLE** | `Live` | `v0.1.0` | `reStation.Core\Cpu\` | Reusable static recomp runtime, `NextEventCycle` scheduling, `MidDispatch`, `IGameFramePatch` hooks, and clean-room Psy-Q BIOS HLE shims (`PsxInterpreter` strictly fallback). |
| `REST-04` | **Hardware-Timed 2x CD-ROM + MDEC + SPU/XA** | `Live` | `v0.1.0` | `reStation.Core\Audio\`, `CdRom\`, `Mdec\` | `150 sectors/s` 2x CD pacing (`225,792` cycles), ADSR release envelopes to `0`, persistent XA history + `3:7`/`6:7` resampling, and `44.1 kHz` stereo output. |
| `REST-05` | **Pluggable `IPsxRenderBackend` (`GL33` / `Software`)** | `Live` | `v0.1.0` | `reStation.Core\Gpu\` | Hardware `OpenGL 3.3 Core` (`"opengl"`) PS1 renderer + `SoftwareCpu` deterministic parity oracle (`"software"`, `1x`–`8x` PGXP, `4:3`/`16:9`). |
| `REST-06` | **Modular `.recomp` Loader & `reEngine` Integration** | `Live` | `v0.1.0` | `reStation.Player\`, `reStation.Compiler\` | Registers under [`reEngine`](<PROJECTS_ROOT>/Personal/reEngine/README.md), deploys via signed `reStation-Setup-v0.1.0-win-x64.exe` (`AllowAutoStart = false`) side-by-side (`engines\reStation\v0.1.0\`, `SharedLibs\reStation\v0.1.0\`), auto-detects `data\rom\`, and lifts `<= 320 LOC` `.recomp` modules. |
| `REST-07` | **Multi-Scene Parity & `--visible-test` Suite** | `Live` | `v0.1.0` | `reStation.Tests\` & `Sandbox\` | `87/87` xUnit parity/containment tests (`0` warnings), `--fixture` WAV/PNG capture parity checks, `<= 15s` heartbeats, foreground `--visible-test`, and 5/5 `Sandbox\` Gates `PASS` ([report](Dev/docs/reports/0.1.0.20261113-213430.-.sandbox-verify.md)). |
| `REST-08` | **Shared MIPS$\rightarrow$IR + Collectible CoreCLR RyuJIT (`4 KB`/`64B` Epochs)** | `Planned` | `v0.2.0` | `reStation.Core\Ir\`, `Core\Jit\` | Shared ISA$\rightarrow$IR lowering feeding collectible `DynamicMethod` CIL for dynamic RAM overlays & SMC, routing all CPU/DMA/BIOS/snapshot RAM mutations through `InvalidateRamRange` with delay-slot span tracking. |
| `REST-09` | **`--jit-bringup` Raw-Disc Profiler & Watchdog** | `Planned` | `v0.2.0` | `reStation.Core\Bringup\` | Boots verified discs via HLE + RyuJIT with a non-VBlank spin watchdog (`ExitCode = 3`) and atomically writes clean-room structural `bringup.profile.json` (`(codeSha256, entryPc, byteLen)` — zero retail bytes). |
| `REST-10` | **Zero-RWX Pre-Decoded Threaded-IR Block Cache** | `Planned` | `v0.2.0` | `reStation.Core\Jit\ThreadedIrBackend.cs` | Pure-data pre-decoded `IrInsn[]` dispatcher activated automatically when `isDynamicCodeSupported == false` (`W^X` hosts) or `--force-zero-rwx` CI tests, plus hot-SMC demotion. |
| `REST-11` | **Hardware `Vulkan` Render Backend (`"vulkan"`)** | `Planned` | `v0.3.0` | `reStation.Core\Gpu\Vulkan*` | Explicit Vulkan 1.2 / `SDL_Vulkan` SPIR-V backend with `R16UI` VRAM, PGXP perspective UVs, `GP0(E6h)`/`(C0h)` parity, and headless ICD/Lavapipe/offscreen fallback (`--require-backend`). |
| `REST-12` | **Deterministic Tile/Row-Band `ParallelCpu` Rasterizer (`"parallelCpu"`)** | `Planned` | `v0.3.0` | `reStation.Core\Gpu\Parallel*` | Multithreaded CPU rasterizer partitioning `hiVram` into disjoint bands/tiles replayed in exact GP0 order with `(TexPage, CLUT)` barriers — `100%` bit-exact to `"software"` at `1x`. |
| `REST-13` | **Sub-Tick Interpolation & Clean-Room HD Override SDK** | `Planned` | `v0.3.0` | `reStation.Core\Enhancements\` | Reusable `ISubTickFrameHook` interpolation helper (`60–240+ Hz` on `30 Hz` ticks) and content-addressed `(texPage, clutHash, rectHash)` HD texture pack loader (`<dataDir>\hdpacks\`). |
| `REST-14` | **Automated `R3 -> R4` Static C# Overlay Promoter** | `Planned` | `v0.4.0` | `reStation.Compiler\`, `Core\Cpu\` | Consumes `bringup.profile.json` via Shared IR to emit hash-verified `<= 320 LOC` static C# overlays (`IOverlayRecompModule`), achieving `jitDispatchCount == 0 && interpreterFallbackCount == 0` (`isR4PromotionReady`). |
| `REST-15` | **10-Platform Host Adapters & Cooperative WASM Pump** | `Planned` | `v1.1.0–v2.0.0` | `reStation.Player\`, Platform HALs | Cross-platform `SDL_ShowOpenFileDialog`/RID loading (`linux-x64`, `osx-arm64`/MoltenVK, `steamos-deck`), Android/iOS touch & Zero-RWX NativeAOT, cooperative `StepSingleVBlank()` (`browser-wasm`), and console HALs. |

---

## 2. Planned Feature Cards (`v0.2.0` $\rightarrow$ `v2.0.0`)

### `REST-08` / `REST-09` / `REST-10` — Shared-IR Runtime JIT, `--jit-bringup` & Zero-RWX Cache (`Planned` — `v0.2.0`)
- **Tier 3 Execution Stack:** Unifies `reStation.Compiler` and runtime dynamic translation on a single MIPS$\rightarrow$IR frontend (`IrInsn`, `MipsIrLowerer`) so `.recomp` modules and `--jit-bringup` sessions never drift.
- **4 KB + 64B Epoch Safety:** Replaces raw `Bus.Ram` bulk writes (`LoadExecutableIntoRam`, `ZeroRam`/`FillRam`/`CopyRam`, `ReadSnapshotState`) with `InvalidateRamRange`, tracks branch + delay-slot page spans across `KUSEG`/`KSEG0`/`KSEG1` mirrors, and demotes hot SMC pages (`> 8` writes) to `ThreadedIrBackend`.
- **Clean-Room Bring-Up (`bringup.profile.json`):** Records strictly structural metadata (SHA-256, PC ranges, indirect jump targets, overlay IDs) under a deterministic `5,644,800`-cycle non-VBlank watchdog (`ExitCode = 3`).

### `REST-11` / `REST-12` / `REST-13` — Tri-Backend Parity (`Vulkan` + `ParallelCpu`) & Enhancement SDK (`Planned` — `v0.3.0`)
- **Mandatory `v1.0.0` Backend Foundation:** Delivers explicit `"vulkan"` and multithreaded `"parallelCpu"` alongside `"opengl"` and deterministic `"software"` oracle, forwarding `GP0(E6h)` mask bits and implementing `GP0(C0h)` VRAM readback across all backends.
- **Parallel Determinism:** Tiles/row-bands replay binned primitives strictly in GP0 submission order with full worker barriers on `UploadVramRect`/`CopyVramRect`/`TryReadVramRect` or dirty `(TexPage, CLUT)` render-to-texture intersections (`1x` `SHA-256` match against `"software"`).
- **Enhancement SDK:** Standardizes `ISubTickFrameHook` interpolation and `<dataDir>\hdpacks\` HD overrides for `.recomp` games.

### `REST-14` — Automated `R3 -> R4` Static C# Promotion Pipeline (`Planned` — `v0.4.0`)
- **Zero-Fallback Promotion:** Lowers `bringup.profile.json` code ranges + dynamic `.BIN`/`.OVL` overlays into content-hash-keyed `IOverlayRecompModule` C# chunks (`<= 320 LOC`) and enforces `isR4PromotionReady` (`jitDispatchCount == 0 && threadedIrDispatchCount == 0 && interpreterFallbackCount == 0`).

### `REST-15` — 10-Platform Host Adapters & Cooperative `browser-wasm` (`Planned` — `v1.1.0–v2.0.0`)
- **Graceful Full-Stack Compatibility:** Deploys full RyuJIT + `OpenGL`/`Vulkan`/`ParallelCpu` across Desktop, SteamOS Deck, macOS (`MoltenVK`), and Android, and pre-linked Static `.recomp` + Zero-RWX `ThreadedIrBackend` + cooperative `StepSingleVBlank()` (`0` `SemaphoreSlim.Wait` main-thread deadlocks) across `ios-arm64`, `browser-wasm`, `switch`, `xbox`, and `playstation`.
