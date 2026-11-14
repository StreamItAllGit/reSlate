# reStation — User Guide

**Synced Version:** `0.1.5` (`2026-11-14`)

---

## 1. Overview & Zero-Asset Policy

`reStation` is a clean-room PlayStation 1 (`PS1`) static recompilation engine, MIPS R3000A + GTE lifter, and modular Bring-Your-Own-Media (`BYO-Media`) player designed to run under `reEngine` (`AllowAutoStart = false`, zero background daemons).

- **Zero Copyrighted Assets Shipped:** `reStation` distributes no Sony BIOS images, retail game binaries, disc dumps, or artwork. BIOS calls are handled cleanly via HLE (`PsxBiosHle`, supporting both 4-argument `InitPAD` and `PAD_init` controller layouts alongside clean digital pad negotiation), and retail game assets come strictly from your own legally dumped PS1 disc images.
- **Pluggable Rendering & Hardware Timing:** Includes GPU-accelerated `OpenGL 3.3 Core` (`1x`–`8x` PGXP precision, centered `4:3` / `16:9` aspect-ratio letterboxing, and hardware alpha-blended `F1`/`F3` overlay rendering) and a `Software CPU` fallback, cycle-timed `2x` CD-ROM streaming (`150 sectors/s`), MDEC video decode, and a 24-voice SPU with full ADSR release envelopes, hardware `SPUCNT` / CD-XA / main volume gating, and Form2 XA-ADPCM playback (`44.1 kHz`).

---

## 2. First-Run Setup via `reEngine` Side-by-Side

Running `reStation-Setup-v0.1.5-win-x64.exe` automatically bootstraps or updates `reEngine` (`v0.1.3`, no background service or Windows Startup entries created) and installs `reStation` side-by-side into:

```text
<reEngineInstallDir>\engines\reStation\v0.1.5\
├── reStation.exe               # Clean-root launcher
├── AGENTS.md                   # Runtime context anchor
└── app\
    ├── reStation.Player.exe    # SDL3 + OpenGL 3.3 / Software host
    ├── reStation.Compiler.exe  # PS1 ELF -> .recomp C# module compiler
    ├── LICENSE                 # Mozilla Public License 2.0 (MPL-2.0)
    ├── SDL3.dll
    ├── 7z.exe / 7z.dll         # Container extraction (with LICENSE-7ZIP.txt)
    └── version.txt
```

User disc media lives inside each `.recomp` game module's `data\rom\` directory rather than inside the shared engine directory. To uninstall, run `reStation-Uninstall.exe` or pass `--uninstall --install-dir <dir>` targeting either the `reEngine` base root, a parent directory containing `reEngine\`, or the versioned leaf engine folder directly.

By default, `reEngine` installs under `<PROGRAMS_ROOT>\reEngine\`, placing this engine track at `<PROGRAMS_ROOT>\reEngine\engines\reStation\v0.1.5\`. Older `reStation` tracks (`v0.1.0`, `v0.1.1`, `v0.1.2`, `v0.1.3`, `v0.1.4`) remain untouched side-by-side so existing `.recomp` game modules stay pinned safely to their tested engine version (`compatibleEngineTrack: "0.1.x"`, `engineVersion: "0.1.5"`).

### Ways to Launch
1. **Through `reEngine` (Recommended):** Double-click any `.recomp` cartridge or `module.recomp.json` package or run `reEngine.exe --open "<path>\app\MyGame.recomp"`. `reEngine` resolves the matching `engines\reStation\v0.1.5\` track and forwards `--preload-media` / `--wait` automatically.
2. **Direct Launcher:** Run `reStation.exe --module "<path>\app\MyGame.recomp"` (or `module.recomp.json`, optionally passing `--disc "<path>\disc.chd"`, `--preload-media "<path>"`, `--wait`, or `--version` to print `reStation v0.1.5`).
3. **Interactive Gate:** Launch `reStation.exe` with no arguments to open the **First-Run Media Verification** window.

---

## 3. BYO-Media Disc Ingestion & `data\rom\` Preload Discovery

`reStation` supports standard PS1 disc images and compressed containers in the following priority order when scanning folders:

1. `.cue` (paired with `.bin`)
2. `.chd`
3. `.iso`
4. `.7z`
5. `.zip`
6. `.bin`

### Option A — Automatic Preload (`data\rom\`)
Place your legal PS1 disc image or archive inside the `.recomp` module's gitignored `data\rom\` directory (or custom relative `preloadRomDir` defined in `module.recomp.json`). Single-file `.recomp` ZIP cartridges stored under `app\<Game>.recomp` automatically resolve `<InstallRoot>\data\rom\` alongside unpacked layouts:

```text
<ModuleRoot>\
├── app\
│   └── <Game>.recomp           # Single-file ZIP cartridge (or module.recomp.json)
└── data\
    └── rom\
        └── MyGame.chd          # (.cue+.bin / .iso / .7z / .zip / .chd)
```

On launch (or while waiting on the First-Run Media Gate), `reStation` automatically discovers the highest-priority file in `data\rom\`, extracts/verifies its contents, and boots directly into the game without prompting.

### Option B — First-Run Media Gate & Live Disc Swap
If no preloaded media is found:
1. Drag and drop a `.recomp.json` / `.recomp` module and/or a PS1 disc image onto the **First-Run Media Verification** drop zone, or click **Select .recomp Module...** and **Select Disc Image...**.
2. `reStation` extracts containers safely into `<dataDir>\gamedata\disc\`, unpacks ISO-9660 entries into `<dataDir>\gamedata\`, verifies every `requiredDiscChecksums` SHA-256 (and byte length) declared by the module, and saves `<dataDir>\gamedata\disc_manifest.json`.
3. To swap discs or switch modules later while playing, press `F1` to open the **Disc** tab (`Change Disc Image...` / `Load .recomp Module...`) or drop a new disc file directly onto the player window.

---

## 4. Compiling `.recomp` Modules (`reStation.Compiler`)

To lift a PS1 executable or disc into a standalone `.recomp` C# module using the bundled compiler inside `app\`:

```powershell
app\reStation.Compiler.exe (--disc <image> | --gamedata-dir <dir> | --exe-path <exe>) `
  [--decomp-dir <dir>] [--functions-json <file>] `
  --out-dir <dir> --module-id <id> --serial <serialNumber> --title <title> `
  [--version <semver>] [--compile-dll]
```

### Key Options
| Flag | Description |
| :--- | :--- |
| `--disc <image>` | Reads `SYSTEM.CNF` and all `PS-X EXE` files (unlimited EXE discovery; `12`-EXE cap removed in `v0.1.1`) from `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, or `.zip`. |
| `--gamedata-dir <dir>` | Scans an already-extracted game directory for `SYSTEM.CNF` and `PS-X EXE` binaries. |
| `--exe-path <exe>` | Lifts a single `PS-X EXE` file directly. |
| `--decomp-dir <dir>` / `--functions-json <file>` | Optional symbol/function boundary hints. When omitted, the compiler discovers `Pc0`, `JAL` call targets, stack prologues, and `.rdata` jump tables automatically. |
| `--out-dir <dir>` | Destination folder for generated C# chunks (`PartNNN.g.cs`, `DispatchNN.g.cs`, including `IOverlayRecompModule` headers for `.text` overlays), `<PascalId>GameModule.g.cs`, `<moduleId>.Module.csproj`, and `module.recomp.json`. |
| `--module-id`, `--serial`, `--title` | Required module ID (e.g., `kings_field`), PS1 disc serial (e.g., `SLUS-00158`), and display title. |
| `--version <semver>` | Module SemVer (defaults to `0.1.5`). |
| `--compile-dll` | Compiles `<moduleId>.Module.csproj` (`net8.0`, Release, deterministic) against engine libraries (`ReStationLibDir` / `RESTATION_LIB_DIR` / `RESTATION_SHARED_DIR` / `<outDir>\lib\`) to emit `<moduleId>.recomp.dll` and `<moduleId>.recomp.deps.json`. |

Exit codes: `0` on success, `1` on lift/build failure, `2` on invalid CLI arguments.

---

## 5. Controls, Mouse Capture & Default Keybinds

### Global Hotkeys & Mouse Capture
- **`F1`:** Open or close the **Slide-Over Settings Drawer** (releases mouse capture when opened).
- **`F3`:** Toggle the live **Telemetry HUD** overlay (saved to `settings.json`).
- **`F5` / `F9`:** Quick Save (`F5`) / Quick Load (`F9`) using Quick-Save Slot `0`.
- **Click Game Viewport:** Captures mouse for camera look when the drawer is closed (focus-grab click captures the cursor without firing spurious combat clicks); once captured, any rebound mouse buttons (`LClick` `1001`, `RClick` `1002`, `MClick` `1003`, `Mouse4` `1004`, `Mouse5` `1005`) route cleanly through your active keybind tier.
- **`Esc`:** Releases mouse capture (or closes the First-Run Media Gate).

### Control Modes & 3-Tier Input Profiles (`F1` → `Keybinds`)
Switch `ControlMode` in **Settings (`F1`) → Controls** (`Modern WASD + Mouse` `modernWasdMouse` with right-stick mouse look `0.2`–`4.0` vs. `Classic Tank` `classicTank`), and select your active binding tier in **Settings (`F1`) → Keybinds**:
- **Tier 1 — `Global 1:1` (`globalController`):** Engine-wide 1:1 PS1 controller mapping persisted atomically in `settings.controls.json` (shared across games).
- **Tier 2 — `Game 1:1` (`perGameController`):** Per-game 1:1 PS1 controller overrides stored in `settings.json` (`keyBindings`).
- **Tier 3 — `Modern PC` (`perGameModern`, default when module declares `IModuleInputProfileProvider`):** Module-defined semantic actions with context-aware Gameplay vs. Menu/Title PS1 button mappings stored in `settings.json` (`modernActionBindings`); falls back automatically to `Game 1:1` for modules without a Modern PC profile.

### Default Keyboard & Gamepad Mapping (`1:1` Controller Tiers)
| Action | Modern Default | Classic Default | SDL Gamepad |
| :--- | :--- | :--- | :--- |
| **D-Pad Up / Down / Left / Right** | `W` / `S` / `A` / `D` | `Up` / `Down` / `Left` / `Right` | Left Stick / Pad |
| **Cross / Circle** | `Space` / `LShift` | `Space` / `LShift` | South / East |
| **Square / Triangle** | `F` / `R` | `F` / `R` | West / North |
| **L1 / R1** | `Q` / `E` | `Q` / `E` | Left / Right Shoulder |
| **L2 / R2** | `Z` / `C` | `Z` / `C` | — |
| **Start / Select** | `Enter` / `Tab` | `Enter` / `Tab` | Start / Back |
| **Quick Save / Quick Load** | `F5` / `F9` | `F5` / `F9` | — |

---

## 6. `F1` 5-Tab Settings Drawer & `F3` Telemetry HUD

Press `F1` (or click the top-right **Settings (F1)** pill) to open the 5-tab right-docked drawer. Changes save atomically to `settings.json` (with `.bak` self-healing and rolling `config_history` backups).

1. **Video:**
   - **Render Backend:** `OpenGL 3.3` (`opengl`, default) or `Software CPU` (`software`, applies on next launch).
   - **Render Scale:** `1x`, `2x`, `4x` (default), or `8x`.
   - **Framerate Mode & Target FPS:** `Original (60/30)` or `Enhanced FPS` (`30`, `60`, `120`, `144`, `240`, or `Max` for unlocked).
   - **Toggles:** `VSync`, `16:9 Widescreen`, `PGXP Precision`, `Fullscreen`, and `FPS Overlay (F3)`.
2. **Controls:**
   - Toggle `Modern WASD + Mouse`, adjust `Mouse Sens` (`0.2`–`4.0` in `0.2` steps), view the active tier summary (`Global 1:1 PS1`, `Game 1:1 PS1`, or module `Modern PC`), or jump to **Customize Keybinds (3 Tiers)...**.
3. **Keybinds:**
   - Switch between **`Global 1:1`**, **`Game 1:1`**, and **`Modern PC`** pills, then click any action row and press a **keyboard key** (scancodes `4..511`) or **mouse button** (`LClick`, `RClick`, `MClick`, `Mouse4`, `Mouse5`) to rebind (`Esc` cancels; `F1` stays reserved), or click **Reset Global / Game / Modern PC Defaults**.
4. **System:**
   - **Use Global Format Rules** (`useGlobalFormatRules`, default `true`): Synchronizes date/number formatting with suite-wide rules.
   - **Fast Disc Load (Form1)** (`shouldFastLoadData`, default `true`): Accelerates Form1 `2048`-byte data sector loads (`32,768` cycles) while strictly keeping Form2/XA audio and raw MDEC video at true PS1 `1x`/`2x` timing (`150 sectors/s`).
   - **Master Volume:** `0%`–`100%` in `10%` increments.
   - **Runtime Logging:** Toggle `runtime.log` disk output (`Writing to runtime.log` vs `Off (in-memory ring only)`).
   - **Quick Save (`F5`) / Quick Load (`F9`):** Direct slot-0 buttons.
5. **Disc:**
   - Shows loaded module name/serial and active disc metadata, plus **Change Disc Image...** and **Load .recomp Module...** (applies next launch).

### `F3` Telemetry HUD
Pressing `F3` displays a compact top-left status bar showing the active timing badge (`[ORIG]`, `[ENH]`, or `[MAX]`) and live metrics:

```text
[ENH] FPS 60 | opengl 4x | CPU 1.4ms GPU 2.1ms | CD 150/s | XA drops 0 | Audio 52%
```

---

## 7. Quick-Save Slots (`"RST1"` + `"RSTX"`) & `.mcr` Memory Cards

All saves live inside `<dataDir>\saves\` (next to `settings.json`):

- **Quick-Save Snapshots (`<safeModuleId>_slot<0..9>.rst`):** Up to 10 slots (`0`–`9`; `F5`/`F9` and the System tab target Slot `0`). Files keep frozen `"RST1"` schema v1 compatibility plus optional EOF-anchored `"RSTX"` trailer chunks (`"BIOS"` event/timer/card state + `"OVLY"` module overlay bytes), verify `moduleId` parity to prevent cross-game corruption, GZip-compress complete `PsxCpu` machine state (RAM, CPU/GTE, GPU, SPU, MDEC, CD-ROM, Timers/DMA, BIOS HLE), and write atomically via `.tmp` replacement. Quick-loading cleanly unwinds lifted execution at the next VBlank boundary.
- **PS1 Memory Cards (`memcard_<serialOrModuleId>_slot1.mcr`):** `reStation` emulates standard `128 KB` (`131,072`-byte, 16-block × 8,192-byte, `"MC"` header with XOR frame checksums) `.mcr` cards via BIOS `bu00:` file syscalls. Each title receives an isolated memory card scoped by its `SerialNumber` (or `ModuleId` fallback), flushed atomically on every game save, erase, or format and compatible with standard PS1 `.mcr` tools.

---

## 8. License & Legal
- **Project License:** Mozilla Public License 2.0 (`MPL-2.0`) — bundled as `app\LICENSE` in every installation.
- **Third-Party Tools:** Bundled `7z.exe` and `7z.dll` container extractors are governed by `LGPL-2.1+` (`LICENSE-7ZIP.txt` alongside `app\`).
- **Clean-Room Zero-Asset Policy & Non-Affiliation:** `reStation` contains zero copyrighted game assets, PS1 BIOS dumps, or proprietary Sony code. PlayStation® and PS1® are registered trademarks of Sony Interactive Entertainment Inc.; `reStation` is an independent clean-room project and is not affiliated with or endorsed by Sony Interactive Entertainment. Users must supply their own legally obtained PS1 disc media.
