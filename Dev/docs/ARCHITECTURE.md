# reStation — System Architecture

**Synced Version:** `0.1.0` (`2026-11-13`)  
**Owner:** `reStation` Engine (`@devv` / `@dean`)

---

## 1. Purpose & Platform Separation
`reStation` isolates **PlayStation 1 (`PS1`) platform engineering** from individual game modules (`<Game>Recomp`). Games publish slim `.recomp` assemblies containing clean-room lifted C# code, disc SHA-256 checksums, save-state fixture specs, and optional per-game frame patches (`IGameFramePatch`), while `reStation` supplies:
- **Shared Foundation Reuse:** Built directly on `Foundation.Core`, `Foundation.Retro` (`MediaContainerExtractor`, `SaveStateFixtureRunner`, `IVisibleTestHarness`), `Foundation.Launcher` (`RootLauncherHost`), and `Foundation.Installer` (`StandardInstallerHost`), plus `reEngine.Core` (`RecompPackageReader`, `ReEngineBootstrapHelper`).
- **`reEngine` Router & Side-by-Side History:** Publishes immutable SDK/runtime drops to `<SHARED_LIBS_ROOT>\reStation\v<Ver>\`, deploys side-by-side under `<reEngineRoot>\engines\reStation\v<Ver>\`, auto-bootstraps `reEngine` during installation, auto-discovers user media in gitignored `data\rom\` preloads, and permits additive non-breaking `PATCH+1` drops from child `<Game>Recomp` runs.

---

## 2. Solution & Tier A Layout (`Dev\src\reStation.slnx`)
All `.cs` and `.ps1` files obey the `<= 400 LOC` ceiling (`<= 350 LOC` target; `<= 320 LOC` per lifted `.g.cs` chunk), Zero-PII `<PathMap>` Release builds (`net8.0` / `net8.0-windows`), and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`:

| Project | Target | Responsibility |
| :--- | :--- | :--- |
| [`reStation.Contracts`](Dev/src/reStation.Contracts/reStation.Contracts.csproj) | `net8.0` | Locked ABI (`IReStationModuleManifest`, `IReStationCpu`, `IRecompModule`, `IReStationHost`, `IGameFramePatch`, `IPsxRenderBackend`, `PsxTelemetrySnapshot`). |
| [`reStation.Core`](Dev/src/reStation.Core/reStation.Core.csproj) | `net8.0` | Zero-SDL3 / zero-WinForms PS1 hardware runtime (`Core`, `Cpu`, `Gte`, `Hardware`, `CdRom`, `Bios`, `Audio`, `Mdec`, `Gpu`, `DiscIngestion`, `Saves`, `Input`). |
| [`reStation.Compiler`](Dev/src/reStation.Compiler/reStation.Compiler.csproj) | `net8.0` | PS-X EXE + disc media (`SYSTEM.CNF`) lifter emitting chunked C# (`PartNNN.g.cs`), `IRecompGameModule` wrappers, Standard 18 `module.recomp.json`, and `--compile-dll`. |
| [`reStation.Player`](Dev/src/reStation.Player/reStation.Player.csproj) | `net8.0-windows` | SDL3/OpenGL 3.3 Core host, `AssemblyLoadContext` module loader, `PreloadRomDetector`, first-run gate, `F1` drawer, `F3` HUD, `--fixture` & `--visible-test`. |
| [`reStation.Launcher`](Dev/src/reStation.Launcher/reStation.Launcher.csproj) | `net8.0-windows` | Root `reStation.exe` launcher embedding `Foundation.Launcher.dll` (`RootLauncherHost`) with synchronous exit propagation on headless/test flags. |
| [`reStation.Installer`](Dev/src/reStation.Installer/reStation.Installer.csproj) | `net8.0-windows` | 4-pillar signed zero-PII setup EXE (`AllowAutoStart = false`), compressed `.dll.gz` dependencies, conditional `payload.zip` + `reEngine` bootstrapper, `--test-mode` isolation. |
| [`reStation.Tests`](Dev/src/reStation.Tests/reStation.Tests.csproj) | `net8.0-windows` | Deterministic xUnit verification suite (`54` test methods / `84` executions across 8 files, `0` skipped). |

---

## 3. PS1 Hardware Runtime Subsystems (`reStation.Core`)

### 3.1 CPU, Memory Bus, GTE, Timers/DMA & BIOS HLE (`Cpu/`, `Gte/`, `Hardware/`, `Bios/`)
- **CPU & Bus (`PsxMemoryBus`, `PsxCpu`, `PsxCpu.Scheduling`, `PsxInterpreter`):** Models 2 MB main RAM, 1 KB scratchpad, MMIO dispatch (`TimersAndDma`, `Gpu`, `CdRom`, `Spu`, `Mdec`, `PsxJoypadPort`), COP0 (`SR.IsC` cache isolation, exceptions, IRQ), PS-X EXE loader, `IGameFramePatch` call interception, dynamic `IRecompModule` dispatch (`ReturnSentinelPc = 0xFFFFFFFC`), R3000A interpreter fallback (including post-quicksave non-entry `ResumeFromSnapshot`), and `OnVBlankFrame` coroutine parking.
- **GTE & PGXP (`PsxGte`, `PsxGte.Commands`, `PgxpTracker`):** Implements `RTPS`, `RTPT`, `NCLIP`, `MVMVA`, `OP`, `NCS..DPCT`, `AVSZ3/4`, UNR `Divide`, overflow `FLAG`, and subpixel `1/w` + XY PGXP tracking.
- **BIOS HLE & Memory Cards (`PsxBiosHle`, `PsxBiosHle.Events`, `MemCardMcrStore`):** Generalized A0/B0/C0 vectors, syscalls `1–3`, event timers, pad polling, and Zero-PII 128 KB `.mcr` stores keyed by disc `SerialNumber` / `ModuleId`.

### 3.2 Hardware-Timed 2x CD-ROM (`CdRom/`, `KFR-PARITY-01`)
- [`PsxCdRomTiming.ComputeReadLatencyCycles`](Dev/src/reStation.Core/CdRom/PsxCdRomTiming.cs) enforces exact PS1 clock (`33,868,800 Hz`) cadence: **`225,792` cycles/sector** at `2x` (`150 sectors/s`, `(mode & 0x80) != 0`), **`451,584` cycles/sector** at `1x` (`75 sectors/s`), and accelerated **`32,768` cycles/sector** strictly for non-XA Form1 data sectors when `!isXaOrForm2 && !isRawMdec && shouldFastLoadData`.
- `PsxCdRom` + `PsxCdRom.Media` stream `18 * 128B` Form2 XA sectors directly to `PsxSpu.SubmitXaSectors`, route `2340B` raw MDEC sectors, and expose rolling `CdSectorsPerSec` telemetry.

### 3.3 SPU ADSR/XA Audio, MDEC & Disc/Save/Input (`Audio/`, `Mdec/`, `DiscIngestion/`, `Saves/`, `Input/`, `KFR-PARITY-02`)
- **SPU (`SpuVoice`, `XaAdpcmDecoder`, `PsxSpu`):** 24 voices over 512 KB SPU RAM with 4-stage ADSR (`Off`, `Attack`, `Decay`, `Sustain`, `Release`) at `44.1 kHz` where `KeyOff()` decays cleanly to `Envelope == 0` -> `Phase = Off`, `Active = false`. `XaAdpcmDecoder` retains persistent filter memory (`Old1L/Old2L/Old1R/Old2R`) across consecutive sectors with exact `3:7` (`18.9 kHz`) and `6:7` (`37.8 kHz`) resampling. `PsxSpu` owns a `32,768`-frame XA queue and **`Foundation.Core.AudioRingBuffer(65_536, 2)`**, reporting `XaDrops`, `OutputOverflows`, `AudioFillRatio`, `AudioUnderruns`, and `ActiveVoiceCount` with a 32-frame zero-ramp underrun guard.
- **MDEC (`PsxMdec`, `PsxMdec.Decode`):** DMA0/DMA1 FIFO, table uploads, zig-zag RLE, dequantization, 2-pass fixed-point IDCT, and YUV conversion to mono4/mono8/RGB24/RGB15 with `MacroblocksPerSec` telemetry.
- **Disc Ingestion (`DiscManifest`, `Iso9660RawReader`, `DiscVerifierAndExtractor`):** Parses `.iso` (2048B) / `.bin` (2352B) / `.cue`, extracts `.chd`/`.7z`/`.zip` via `Foundation.Retro.MediaContainerExtractor` (`ToolsBaseDirectory` containing `7z.exe`/`7z.dll`), enforces `SandboxEnvironment.AssertPathContained` against Zip-Slip, verifies `RequiredDiscChecksums` SHA-256 + byte lengths, and persists `gamedata\disc_manifest.json` (`sha256Hex`).
- **Saves & Input (`QuickSaveManager`, `KeyBindingsConfig`, `ModernInputController`):** Atomic `"RST1"` v1 GZip slot `.tmp` + move snapshots with `ListSlots`/`Delete`, plus WASD + mouse-look / classic controller mapping.

### 3.4 Pluggable GPU Renderers (`Gpu/`, `KFR-PARITY-03`)
- [`PsxGpu`](Dev/src/reStation.Core/Gpu/PsxGpu.cs) maintains the `1024x512` 15-bit VRAM mirror, decodes GP0 (`0x02`, `0x1F`, `0x20–0x7F`, `0x80`/`0xA0`/`0xC0`, `0xE1–0xE6`) and GP1 display commands, ticks `564,480`-cycle VBlanks, and drives any `IPsxRenderBackend` + optional `IPsxGpuBackendHooks` (`AttachPgxp`, `SetTextureWindow`, `TryReadVramRect`).
- [`OpenGl33RenderBackend`](Dev/src/reStation.Core/Gpu/OpenGl33RenderBackend.cs) (default, `--backend opengl`) binds pure `delegate* unmanaged[Stdcall]` entry points via `OpenGl33Bindings.Initialize(Func<string, IntPtr>)` (zero SDL3 dependency in `Core`), uploads dirty-rect `R16UI` VRAM textures, batches `12,288`-vertex VBO draws with GLSL `#version 330 core` PGXP `1/w`, 4/8/15-bit CLUT decode, and single/two-pass PS1 semi-transparency into scaled FBOs, blits to the live `SurfaceWidth x SurfaceHeight` viewport, and performs lazy `glReadPixels` strictly inside `CaptureRgba`.
- [`SoftwareCpuRenderBackend`](Dev/src/reStation.Core/Gpu/SoftwareCpuRenderBackend.cs) provides a deterministic `1x–8x` PGXP scanline rasterizer oracle (`--backend software`, headless & fixture baseline).

---

## 4. Compiler, Player Host & Verification Flow (`Compiler`, `Player`, `Launcher`, `Installer`)
1. **Static Lifter (`reStation.Compiler`):** `DiscExeLoader` / `ExeScanHelpers` discover `SYSTEM.CNF` + PS-X EXE headers, functions, and jump tables; `MipsDecoder` / `MipsLifter` flatten R3000A + GTE ops against `IReStationCpu`; `FunctionChunkEmitter` splits lifted methods into `<= 320 LOC` `PartNNN.g.cs` + `DispatchNN.g.cs`; `ModuleManifestEmitter` generates `<PascalId>GameModule : IRecompGameModule`, Zero-PII `<PathMap>` `.csproj`, Standard 18 `module.recomp.json`, and optional `--compile-dll`.
2. **Runtime Host (`reStation.Player`):** `ReStationHost` connects `PsxCpu` + `ReStationLogger` (`<= 15s` heartbeats) + `CrashBundleService` + `reStation.Emu` coroutine; `HostVideoSurface` initializes OpenGL 3.3 Core (falling back cleanly to `SDL_CreateRenderer` + `SoftwareCpuRenderBackend` with `[GL_FALLBACK]` warning in `--headless`/no-GL environments) and synchronizes `Backend.Resize(winW, winH, scale)` before `Gpu.PresentFrame()` and RGBA overlay composition (`SyncViewport`); `ModuleAssemblyLoader` isolates modules in `AssemblyLoadContext`; `PreloadRomDetector` prioritizes `data\rom\` media; `SlideOverSettingsDrawer` (`F1`, 5 tabs incl. `useGlobalFormatRules`), `UiOverlayPresenter` (`F3` HUD), `FirstRunMediaGateView`, `FixtureRunnerHost` (`SaveStateFixtureRunner`), and `VisibleTestHarness` (`--visible-test`) complete the host.
3. **Launcher, Installer & Packaging (`Launcher`, `Installer`, `build_release.ps1`):** `reStation.Launcher` resolves `app\reStation.Player.exe` (waiting synchronously for CLI/test/fixture/visible-test switches). `reStation.Installer` embeds `.dll.gz` + `payload.zip` + conditional `reEngine` bootstrap EXE, enforces `AllowAutoStart = false` (stripping `--autostart*`/`--auto-start*`), supports `--test-mode` isolated `reEngine` + side-by-side `engines\reStation\<Ver>\` verification, and is packaged/signed in single-pass [`build_release.ps1`](Dev/scripts/build_release.ps1).
