# reSlate — User Guide

> **Release Status:** **Beta (Pre-1.0 — `v0.3.1` `reSlate` Public Beta)**. All `0.x` releases are Beta pre-releases (`--prerelease`) until `v1.0.0` Desktop General Availability.

> **Note:** Clean-room SlateOne (`MIPS32 R3000A`) runtime, local Ahead-of-Time (AOT) compiler, and static recompilation engine (**never an emulator**). Users provide their own legally owned disc media or executables. Zero copyrighted vendor BIOS files, retail ROMs, disc images, save states, or pre-generated game binaries are bundled or distributed.

---

## 1. What `reSlate` Is & Who It Is For

`reSlate` (formerly `reStation`) is the `SlateOne` (`R3000A` + `GTE`) static recompilation engine inside the `reEngine` desktop suite. It lets players run verified `.recomp` game packages on Windows using an automatic local AOT compiler (`data\cache\`) supporting both standalone executables and verified disc-container overlays, live Tier 3 native RyuJIT / Zero-RWX Shared-IR dispatch for unlifted code (`PsxInterpreter` is strictly quarantined to `--execution-tier interpreterOracle`), **four live swappable render backends** (`OpenGL 3.3`, hardware `Vulkan`, deterministic `Parallel CPU`, and `Software CPU`), `> 60 Hz` sub-tick enhancement hooks, clean-room `<dataDir>\hdpacks\` PNG texture overrides, Aspect Ratio modes & Overscan cropping, audio-safe **Fast-Forward (`F6`)** and opt-in **Cutscene Skip (`F7`)**, unified Slot 1+2 virtual memory cards (`data\memcards\`), `60 Hz` locked audio, customizable PC + controller keybinds, and instant `F5`/`F9` save states — while giving reverse-engineering teams an offline clean-room `.decomp` pipeline (`reSlate.Decomp.dll`).

---

## 2. Install, Upgrade & Uninstall

### Canonical Install Layout
`reSlate v0.3.1` installs per-user under `reEngine` (requiring **no administrator elevation**, including the required `.ready` discovery marker and `suiteId: "reEngine"` v2 manifest, and never auto-starting in the background):

- **Engine Binary Root:** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.1\` (`reSlate.exe`, `.ready`, `engine.manifest.json`, `settings.example.json`)
- **Application Binaries (`app\`):** `reSlate.Player.exe`, `reSlate.Compiler.exe`, `reSlate.Decomp.dll`, `reStation.Contracts.dll`, `reStation.Core.dll`, `reEngine.Core.dll`, `reEngine.Host.dll`, `SDL3.dll`, `7z.exe`, `7z.dll`, `LICENSE`, `LICENSE-7ZIP.txt`
- **User Data & Local AOT Cache (`data\`):** `rom\`, `cache\`, `hdpacks\` (optional clean-room PNG texture packs), `saves\`, `memcards\` (Slot 1+2 `.mcr` virtual cards), `config\settings.json`, `settings.controls.json`
- **Logs (`logs\`):** `runtime.log`, `crashes\`

### Installation Profiles (`--profile`)
- **Lightweight (Default for Players):** Installs the slim `reSlate` player, local AOT compilation runtime, and `reEngine` launcher integration. Does not require an offline decompiler toolchain.
- **Full (Developers & `.decomp` Creators):** Includes `Lightweight` capabilities plus integration for an offline Ghidra + OpenJDK toolchain placed alongside `reEngine` (`toolchain\`).

### Installer & Portable Options
- **Signed Setup EXE (`reSlate-Setup-v0.3.1-win-x64.exe`):** Interactive wizard or silent CLI installer (`--install-dir <path>`, `--reengine-dir <path>`, `--profile Lightweight|Full`, `--migrate-legacy`, `--confirm-legacy-cleanup`, `--uninstall`). Automatically detects existing `reSlate`/`reEngine`/`reStation` install folders and saved `installProfile` preferences on upgrade without double-nesting `\StreamItAll\` or mistaking a populated `reEngine` folder for a direct engine leaf.
- **Portable Bundle (`reSlate-v0.3.1-win-x64-Portable.zip`):** Self-contained portable archive (staged binaries, `.ready`, licenses, and sanitized documentation — zero setup EXE inside) for side-by-side manual extraction. Requires the .NET 8.0 Desktop Runtime on the host PC.

---

## 3. First-Run Wizard & Local AOT Compilation

1. **Choose `reEngine` Location & Profile:** Confirm your prefilled existing `reEngine` / `reSlate` install folders and saved profile (or pick `Lightweight` / `Full` and a custom target folder) in the setup wizard.
2. **Legacy Upgrade & Settings Migration (`v0.3.0` -> `v0.3.1`):** Existing `0.1.x`–`0.3.0` modules (`reStation`) remain compatible via bidirectional `reSlate` / `reStation` alias resolution and preserved `reStation.{Contracts,Core}` ABI. When loading an older configuration, `<dataDir>\settings.json` migrates non-destructively to `<dataDir>\config\settings.json` (retaining `.bak`), legacy `isWidescreen16By9: true` upgrades automatically to `aspectRatioMode: "horPlus16By9"`, and legacy `<dataDir>\saves\memcard_*.mcr` files copy non-destructively into `<dataDir>\memcards\`.
3. **First Game Launch (`data\cache\`):** Place your verified `SlateOne` disc image (`.bin`/`.cue`/`.chd`/`.iso`) or executable inside `<InstallDir>\data\rom\` (or select it in the first-run media picker). On cold launch of a clean-room `.recomp` package, `reSlate.Compiler.exe` compiles `<Game>.Generated.dll` locally on your PC into `<InstallDir>\data\cache\` and loads it straight from cache on every subsequent launch.

---

## 4. Screen-by-Screen & In-Game Drawer Tour

Press **`F1`** (or pass `--open-settings` / click **Settings (F1)** in the top-right corner) at any time during play to open the slide-over configuration drawer without losing your session:

### Video Tab
- **Render Backend (`OpenGL 3.3` / `Vulkan` / `Parallel CPU` / `Software CPU`):** Switch renderers live at frame boundaries with full `1x` VRAM preservation:
  - **`OpenGL 3.3`:** Default hardware GPU pipeline with pooled command queues and inter-VBlank presentation.
  - **`Vulkan`:** Hardware Vulkan `R16_UINT` `1024x512` VRAM pipeline (`VulkanPresenter` swapchain compositing) with `1x`–`8x` PGXP and automatic driver fallback banner if no Vulkan ICD is present.
  - **`Parallel CPU`:** Multi-core `16px`-band deterministic CPU rasterizer (`100%` SHA-256 bit-exact with `Software CPU` at `1x` while scaling across CPU cores).
  - **`Software CPU`:** Single-threaded reference software rasterizer and safe automatic fallback.
- **Render Scale (`1x` / `2x` / `4x` / `8x`):** Adjust internal rendering resolution across all four backends.
- **Framerate Mode & Target FPS (`Original (60/30)` / `Enhanced FPS` · `30`, `60`, `120`, `144`, `240`, `Max`):** Hardware VBlank and SPU audio remain locked at `60.0 Hz` (`44.1 kHz`) so music and voice audio never drift or stutter, while the **`F3`** HUD displays your actual presented swap rate. `Original (60/30)` + `Target FPS: 30` decimates display swaps cleanly to `~30.0 FPS`; choosing `Enhanced FPS` (`120`–`Max`) with **`VSync (Caps to Monitor Hz)`** off activates smooth inter-VBlank presentation and `ISubTickFrameHook` sub-tick interpolation when supported by the module.
- **HD Texture Packs (`On / Off`):** Loads clean-room PNG replacements from `<InstallDir>\data\hdpacks\` without modifying canonical `1x` VRAM (`GP0(C0h)` readbacks and QuickSaves stay portable). Place PNG files either directly as `<clutSha256Hex>_<rectSha256Hex>.png` or declared inside `<InstallDir>\data\hdpacks\hdpack.manifest.json` (`entries` or `overrides` with `clutSha256Hex`, `rectSha256Hex`, and `relativeFilePath`). Relative paths must stay strictly inside `hdpacks\` (`..` traversal, rooted paths, or junctions fail closed with `ExitCode = 3`).
- **Aspect Mode (`Native PAR` / `4:3` / `Square Int` / `Native Fit` / `16:9 Hor+`):** Left-click cycles through all five display framing modes; right-click resets to **`Native PAR`**. **`16:9 Hor+`** renders unstretched side wings in supported `3D` scenes while keeping `2D` HUD overlays and `24-bit` FMV cutscenes centered `4:3`; **`Native Fit`** scales/fits raw pixel dimensions cleanly to fill the viewport; **`Square Int`** locks crisp integer pixel multiples.
- **Overscan Crop (`0px` / `8px` / `16px`):** Left-click cycles `0px -> 8px -> 16px` symmetric border trim to hide analog edge garbage; right-click resets to **`0px`**. Per-edge crop values (`top`, `bottom`, `left`, `right` from `0` to `32` px) can also be tuned in `data\config\settings.json`.
- **Toggles (`VSync (Caps to Monitor Hz)`, `PGXP Precision`, `Fullscreen`, `FPS Overlay (F3)`):** Toggle monitor-rate swap lock, PGXP sub-pixel vertex precision, borderless fullscreen, and the compact top-edge `1060x34` performance strip (`F3`, showing `[FF 2x]` / `[ORIG]` / `[ENH]` / `[MAX]`).

### Controls Tab
- **Modern WASD + Mouse & Mouse Sens:** Toggle Modern PC WASD + relative Mouse Look and adjust sensitivity (`0.2`–`4.0`) without losing cursor capture across cutscenes or menus.
- **Per-Game Control Options:** Displays only game-specific control options declared by the loaded module (`metadataJson`) alongside live binding summaries for your active tier.

### Keybinds Tab (`F1 -> Keybinds`)
- Supports **full Keyboard/Mouse AND Controller button/trigger (`LT`/`RT`) rebinding** across all three tiers (`Global 1:1`, `Game 1:1`, and `Game Modern PC`):
  - **`Global 1:1` & `Game 1:1`:** Compact 3-column grid (**Action | KBM | Gamepad**) with custom controller button & `LT`/`RT` trigger capture (`2001`–`2032`) and full slot clearing (`0`).
  - **`Game Modern PC`:** Wide 5-column matrix (**Action | 1st | 2nd | 3rd | Gamepad**) supporting keyboard (including Numpad, `Ins`, `Home`, `End`, `RAlt`), mouse buttons/wheel (`WheelUp`/`WheelDown`), and SDL gamepad bindings (`Cross`..`RT`, `D-Pad`, `Touchpad`).
- **Bind, Cancel & Clear:** `Left-Click` any slot to capture, `Esc` to cancel, and `Right-Click` or `Delete` / `Backspace` when idle to clear optional/gamepad slots (`Mouse Wheel` or `PgUp`/`PgDn` scrolls). Held keys ignore OS auto-repeat so menus register one clean tap per press.

### System Tab
- **Use Global Format Rules & Fast Disc Load (`Form1`):** Configure shared formatting defaults and CD Form1 fast-load behavior.
- **Master Volume (`0–100%`) & Runtime Logging:** Adjust SPU XA + ADPCM mixer volume and toggle `runtime.log` diagnostics.
- **Fast-Forward (`F6`) & Multiplier (`FF 2x` / `FF 3x` / `FF 4x`):** Toggles `2x`, `3x`, or `4x` speed cleanly while SPU decimation prevents XA audio drops (`xaDrops == 0`). When **`Tab`** is unbound in **both** your Modern PC bindings and your active `1:1` bindings (`Global 1:1` or `Game 1:1`), holding **`Tab`** also engages temporary Fast-Forward; if `Tab` is bound to an action such as `Select` or `Inventory`, `Tab` never triggers Fast-Forward.
- **Skip Cutscene (`F7`) & Quick Saves (`F5` / `F9`):** Requests an opt-in cutscene skip at the next safe frame boundary (`ISkipCutsceneHook`), or triggers Quick Save (`F5`) / Quick Load (`F9`).

### Disc Tab
- **Active Module & Disc Info:** Shows the loaded `.recomp` title, serial number, and attached disc image status.
- **Change Disc Image... / Load `.recomp` Module...:** Swap disc media or queue another `.recomp` module without editing files by hand.

---

## 5. Everyday Hotkeys & Useful CLI Flags

| Shortcut | Action |
| :--- | :--- |
| **`F1`** | Open / close the Slide-Over Settings Drawer |
| **`F3`** | Toggle the compact `1060x34` FPS, Fast-Forward & renderer HUD strip |
| **`F5`** | Queue Quick-Save at the next VBlank edge (`data\saves\`, including `"MCD2"` memory card snapshot) |
| **`F6`** | Toggle audio-safe Fast-Forward (`2x`/`3x`/`4x`; `Tab` hold works only when `Tab` is unbound in all active profiles) |
| **`F7`** | Request opt-in Cutscene Skip (when supported by the loaded module) |
| **`F9`** | Queue Quick-Load at VBlank with native resume (`interpreterOracle` resume when that diagnostic tier is active) |
| **`Esc`** | Close active capture/drawer/game menu, or release captured mouse cursor |
| **`Alt + Enter`** | Toggle borderless fullscreen |

### Useful CLI Options (`reSlate.exe`)
- `--backend <opengl|vulkan|parallelCpu|software>` & `--require-backend <id>` — Choose active renderer or require initialization without fallback (`ExitCode = 6` if unavailable; **note:** combining `--tray-only` with `--require-backend opengl`, `vulkan`, or `parallelCpu` exits `6` because offscreen tray sessions initialize `software` only).
- `--aspect-mode <nativePar|fourByThree|squareInteger|nativeFit|horPlus16By9>` — Override display aspect mode for the session.
- `--hd-packs [on|off]` & `--fast-forward [on|off]` — Enable/disable `<dataDir>\hdpacks\` overrides and Fast-Forward at launch (overscan crop `0–16px` and `2x`/`3x`/`4x` multiplier are configured in `F1` or `data\config\settings.json`).
- `--doctor` — Print active paths, execution tier, runtime tools, and live probe status for all GPU/CPU backends.

### Memory Cards (`data\memcards\`)
- In-game saves made through hardware `SIO0` or BIOS memory-card calls persist automatically to per-module 128 KB `.mcr` files for **Slot 1** and **Slot 2** under `<InstallDir>\data\memcards\`. Handheld dock probes (`0x58..0x5F`) receive an explicit NACK so save screens complete immediately.

---

## 6. Troubleshooting & FAQ

- **Run Self-Diagnostics (`--doctor`):** Run `reSlate.exe --doctor` from a terminal to print active paths (`data\config\settings.json`, `logs\`), execution tier, `SDL3.dll` / `7z.exe` / compiler availability, and live `OpenGL`, `Vulkan`, `DirectX12`, `ParallelCpu`, and `Software` probe diagnostics.
- **"`--require-backend` could not initialize" (`ExitCode = 6`):** Either your GPU driver/ICD does not support the requested backend (for example, missing `vulkan-1.dll` ICD), `--backend` conflicts with `--require-backend`, or you passed `--tray-only` alongside a windowed backend (`opengl`, `vulkan`, or `parallelCpu`).
- **"Fail-Closed Path Containment Violation" (`ExitCode = 3`):** A path inside `--data-dir`, `--config`, `--log-dir`, media ingestion, or `<dataDir>\hdpacks\hdpack.manifest.json` attempted `..` traversal, pointed outside its root, or used a directory junction/reparse point. Keep all HD pack PNGs strictly inside `<dataDir>\hdpacks\` as real directories/files.
- **"Automation loopback (`--automation-port`) is disabled in Release builds" (`ExitCode = 2`):** Public `Release` binaries exclude automation hooks completely by design.
- **Vulkan or OpenGL reverted to Software automatically:** Your display adapter or ICD lacks the required Vulkan/OpenGL 3.3 Core driver (`Video` tab banner and `runtime.log` `[VK_FALLBACK]` / `GL_FALLBACK` show the exact reason). `Software CPU` or `Parallel CPU` rendering continues without crashing.
- **Cache rebuild after engine update:** Each `<Game>.Generated.dll` under `data\cache\` is keyed by module ID, media SHA-256, and engine version (`v0.3.1`), so stale caches from older versions are automatically rebuilt cleanly.

---

## 7. Releases, Community Channels & Glossary

- **Beta Release Distribution (`v0.3.1` Public Beta):**
  - **GitHub Releases (`--prerelease` Beta):** Official 7-file package containing signed `reSlate-Setup-v0.3.1-win-x64.exe`, standalone `reSlate-v0.3.1-win-x64-Portable.zip`, `.7z` archive, `SHA256SUMS.txt`, and sanitized docs ([GitHub Releases](https://github.com/StreamItAllGit/reSlate/releases)).
  - **Discord Dev Updates:** Automated release announcements and community build notes.
- **Clean-Room `.decomp` Package:** Shareable clean-room decompilation metadata, offline Ghidra recipe scripts, and C/asset normalization descriptors containing zero copyrighted retail game media.
- **Local AOT Cache (`data\cache\`):** Player-PC generated native assembly synthesized strictly locally from your own verified `data\rom\` media and clean-room recipe.

---

## 8. License & Legal

- **Project License:** `reSlate` source code and shipped assemblies are licensed under the **Mozilla Public License 2.0 (`MPL-2.0`)** (`app\LICENSE`). Embedded container extraction helpers (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` (`LICENSE-7ZIP.txt`).
- **Clean-Room & Non-Affiliation Disclaimer:** `reSlate` distributes zero copyrighted game assets, PlayStation (`PS1`) BIOS ROMs, or Sony proprietary code. End users provide their own legally obtained disc media for use with clean-room `.recomp` modules. *PlayStation*, *PS1*, *DualShock*, and *PocketStation* are registered trademarks of Sony Interactive Entertainment Inc.; `reSlate` is an independent clean-room project with zero affiliation or endorsement.
