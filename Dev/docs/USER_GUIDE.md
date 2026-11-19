# reSlate — User Guide

> **Release Status:** **Beta (Pre-1.0 — `v0.3.0-hf.2` `reSlate` Public Beta)**. All `0.x` releases are Beta pre-releases (`--prerelease`) until `v1.0.0` Desktop General Availability.

> **Note:** Clean-room SlateOne (`MIPS32 R3000A`) runtime, local Ahead-of-Time (AOT) compiler, and static recompilation engine. Users provide their own legally owned disc media or executables. Zero copyrighted vendor BIOS files, retail ROMs, disc images, save states, or pre-generated game binaries are bundled or distributed.

---

## 1. What `reSlate` Is & Who It Is For

`reSlate` (formerly `reStation`) is the `SlateOne` (`R3000A` + `GTE`) static recompilation engine inside the `reEngine` desktop suite. It lets players run verified `.recomp` game packages on Windows using an automatic local AOT compiler (`data\cache\`) supporting both standalone executables and verified disc-container overlays, live Tier 3 native RyuJIT / Zero-RWX Shared-IR dispatch for unlifted code (`PsxInterpreter` is strictly quarantined to `--execution-tier interpreterOracle`), hardware-accelerated `OpenGL 3.3` or `Software CPU` rendering (`Vulkan`, `ParallelCpu`, and shared enhancement texture packs arrive in `v0.3.1`), unified Slot 1+2 virtual memory cards (`data\memcards\`), `60 Hz` locked audio, customizable PC + controller keybinds, and instant `F5`/`F9` save states — while giving reverse-engineering teams an offline clean-room `.decomp` pipeline (`reSlate.Decomp.dll`).

---

## 2. Install, Upgrade & Uninstall

### Canonical Install Layout
`reSlate v0.3.0-hf.2` installs into your local user application suite (requiring **no administrator elevation**, including the required `.ready` engine discovery marker, and never auto-starting in the background; `v0.2.8` and earlier installed under `engines\reStation\`):

- **Engine Binary Root:** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.0-hf.2\` (`reSlate.exe`, `.ready`, `engine.manifest.json`, `settings.example.json`)
- **Application Binaries (`app\`):** `reSlate.Player.exe`, `reSlate.Compiler.exe`, `reSlate.Decomp.dll`, `reStation.Contracts.dll`, `reStation.Core.dll`, `reEngine.Core.dll`, `reEngine.Host.dll`, `LICENSE`
- **User Data & Local AOT Cache (`data\`):** `rom\`, `cache\`, `saves\`, `memcards\` (Slot 1+2 `.mcr` virtual cards), `config\settings.json`, `settings.controls.json`
- **Logs (`logs\`):** `runtime.log`, `crashes\`

### Installation Profiles (`--profile`)
- **Lightweight (Default for Players):** Installs the slim `reSlate` player, local AOT compilation runtime, and `reEngine` launcher integration. Does not require an offline decompiler toolchain.
- **Full (Developers & `.decomp` Creators):** Includes `Lightweight` capabilities plus integration for an offline Ghidra + OpenJDK toolchain placed alongside `reEngine` (`toolchain\`).

### Installer & Portable Options
- **Signed Setup EXE (`reSlate-Setup-v0.3.0-hf.2-win-x64.exe`):** Interactive wizard or silent CLI installer (`--install-dir <path>`, `--reengine-dir <path>`, `--profile Lightweight|Full`, `--migrate-legacy`, `--confirm-legacy-cleanup`, `--uninstall`). Automatically detects existing `reSlate`/`reEngine`/`reStation` install folders, shared `LastInstallBaseDir`, and saved `installProfile` preferences on upgrade without double-nesting `\StreamItAll\` or mistaking a populated `reEngine` folder for a direct engine leaf; picking a generic parent or `reEngine` folder in Setup Wizard auto-sorts cleanly into `<reEngine>\engines\reSlate\v0.3.0-hf.2\`.
- **Portable Bundle (`reSlate-v0.3.0-hf.2-win-x64-Portable.zip`):** Self-contained portable archive (staged binaries, `.ready`, licenses, and sanitized documentation — zero setup EXE inside) for side-by-side manual extraction.

---

## 3. First-Run Wizard & Local AOT Compilation

1. **Choose `reEngine` Location & Profile:** Confirm your prefilled existing `reEngine` / `reSlate` install folders and saved profile (or pick `Lightweight` / `Full` and a custom target folder) in the setup wizard.
2. **Legacy Upgrade & Data Migration Safety:** Existing `0.1.x` / `0.2.x` modules (`reStation`) remain compatible via bidirectional `reSlate` / `reStation` alias resolution and preserved `reStation.{Contracts,Core}` ABI. When upgrading data folders, `<dataDir>\settings.json` migrates non-destructively to `<dataDir>\config\settings.json` (retaining `.bak`), legacy `<dataDir>\saves\memcard_*.mcr` files copy non-destructively into `<dataDir>\memcards\`, and `--migrate-legacy` upgrades always preserve `data\rom\` while prompting before removing old runtime files.
3. **First Game Launch (`data\cache\`):** Place your verified `SlateOne` disc image (`.bin`/`.cue`/`.chd`/`.iso`) or executable inside `<InstallDir>\data\rom\` (or select it in the first-run media picker). On cold launch of a clean-room `.recomp` package, `reSlate.Compiler.exe` compiles `<Game>.Generated.dll` locally on your PC into `<InstallDir>\data\cache\` and loads it straight from cache on every subsequent launch.

---

## 4. Screen-by-Screen & In-Game Drawer Tour

Press **`F1`** (or pass `--open-settings` / click **Settings (F1)** in the top-right corner) at any time during play to open the slide-over configuration drawer without losing your session:

### Video Tab
- **Render Backend (`OpenGL 3.3` / `Software CPU`):** Switch renderers live at frame boundaries with immediate VRAM preservation. If your GPU driver fails to initialize `OpenGL 3.3 Core`, `reSlate` automatically reverts to `Software CPU` and displays an inline warning banner. *Note: Hardware `Vulkan`, deterministic `ParallelCpu`, and `hdpacks\` texture overrides are deferred to `v0.3.1` pending shared `reEngine.Host` rendering (passing `--require-backend vulkan` or `--require-backend parallelCpu` exits cleanly with code `6`).*
- **Render Scale (`1x` / `2x` / `4x` / `8x`):** Adjust internal rendering resolution on `Software CPU` or `OpenGL 3.3`.
- **Framerate Mode & Target FPS (`Original (60/30)` / `Enhanced FPS` · `30`, `60`, `120`, `144`, `240`, `Max`):** Hardware VBlank and SPU audio remain locked at `60.0 Hz` (`44.1 kHz`) so music and voice audio never drift or stutter, while the **`F3`** HUD displays your actual presented swap rate (`PresentationFpsTracker`). `Original (60/30)` + `Target FPS: 30` decimates OpenGL/Software display swaps cleanly to `~30.0 FPS` without starving audio; choosing `Enhanced FPS` (`120`–`Max`) with **`VSync (Caps to Monitor Hz)`** turned off unlocks smooth inter-VBlank OpenGL display swaps (`60–240+ / Max`) ahead of full sub-tick 3D motion interpolation in `v0.3.1`.
- **Toggles (`VSync (Caps to Monitor Hz)`, `16:9 Widescreen`, `PGXP Precision`, `Fullscreen`, `FPS Overlay (F3)`):** Defaults to authentic `4:3` framing (`IsWidescreen16By9 = false`). When enabled and supported by the active game module, expands `3D` scenes across unstretched Hor+ `16:9` side wings while keeping `2D` HUD elements and `24-bit` FMV cutscenes centered `4:3`. Disable `VSync (Caps to Monitor Hz)` when targeting `120`–`Max` FPS on high-refresh monitors; toggle PGXP sub-pixel vertex precision and the compact top-edge `1060x34` performance strip (`F3`).

### Controls Tab
- **Modern WASD + Mouse & Mouse Sens:** Toggle Modern PC WASD + relative Mouse Look and adjust sensitivity (`0.2`–`4.0`) without losing cursor capture across cutscenes or menus.
- **Per-Game Control Options:** Displays only game-specific control options declared by the loaded module (`metadataJson`) alongside live binding summaries for your active tier.

### Keybinds Tab (`F1 -> Keybinds`)
- Supports **full Keyboard/Mouse AND Controller button/trigger (`LT`/`RT`) rebinding** across all three tiers (`Global 1:1`, `Game 1:1`, and `Game Modern PC`):
  - **`Global 1:1` & `Game 1:1`:** Compact 3-column grid (**Action | KBM | Gamepad**) with custom controller button & `LT`/`RT` trigger capture (`2001`–`2032`) and full slot clearing (`0`).
  - **`Game Modern PC`:** Wide 5-column matrix (**Action | 1st | 2nd | 3rd | Gamepad**) supporting keyboard (including Numpad `Num0`–`Num9`, `NumEnt`, `Num/`–`Num.`, `Ins`, `Home`, `End`, `RAlt`), mouse buttons/wheel (`WheelUp`/`WheelDown`), and SDL gamepad bindings (`Cross`..`RT`, `D-Pad`, `Touchpad`). Setting a Modern controller binding cleanly replaces that slot with zero invisible fallback overrides.
- **Single-Edge Menu Navigation:** Held keyboard keys ignore OS auto-repeat and mouse look stays isolated from button latches so each key press produces one clean menu tap.
- **Bind Any Slot (`Left-Click`):** Click any KBM or Gamepad pill to enter capture mode, then press your desired key, mouse button, scroll wheel direction, or gamepad button/`LT`/`RT` trigger.
- **Cancel Capture (`Esc`):** Cancels waiting without clearing the existing binding.
- **Clear Slot (`Right-Click` or `Delete` / `Backspace`):** Clears `1:1` Gamepad slots and `Modern PC` optional/controller slots cleanly to unbound (`Right-Click` binds when capture mode is armed, and clears when idle).
- **Scroll:** Use Mouse Wheel, `PgUp`/`PgDn`, or the header buttons to navigate large action lists.

### System Tab
- **Use Global Format Rules & Fast Disc Load (`Form1`):** Configure shared formatting defaults and CD Form1 fast-load behavior.
- **Master Volume:** Adjust SPU XA + ADPCM mixer output level (`0–100%`).
- **Runtime Logging & Quick Saves:** Toggle `runtime.log` diagnostics and trigger `Quick Save (F5)` or `Quick Load (F9)`.

### Disc Tab
- **Active Module & Disc Info:** Shows the loaded `.recomp` title, serial number, and attached disc image status.
- **Change Disc Image... / Load `.recomp` Module...:** Swap disc media or queue another `.recomp` module without editing files by hand.

---

## 5. Everyday Hotkeys & Workflows

| Shortcut | Action |
| :--- | :--- |
| **`F1`** | Open / close the Slide-Over Settings Drawer |
| **`F3`** | Toggle the compact `1060x34` FPS & renderer HUD strip |
| **`F5`** | Queue Quick-Save at the next VBlank edge (`data\saves\`, including `"MCD2"` memory card snapshot) |
| **`F9`** | Queue Quick-Load at VBlank with native resume (`interpreterOracle` resume when that diagnostic tier is active) |
| **`Esc`** | Close active capture/drawer/game menu, or release captured mouse cursor |
| **`Alt + Enter`** | Toggle borderless fullscreen |

### Memory Cards (`data\memcards\`)
- In-game saves made through hardware `SIO0` or BIOS memory-card calls persist automatically to per-module 128 KB `.mcr` files for **Slot 1** and **Slot 2** under `<InstallDir>\data\memcards\`.
- Handheld dock probes (`0x58..0x5F`) receive an explicit NACK so save screens complete immediately without hanging.

---

## 6. Troubleshooting & FAQ

- **Run Self-Diagnostics (`--doctor`):** Run `reSlate.exe --doctor` from a terminal to print active paths (`data\config\settings.json`, `logs\`), execution tier, `SDL3.dll` / `7z.exe` / compiler availability, and active render backends.
- **"Required render backend 'vulkan' / 'parallelCpu' is deferred to v0.3.1" (`ExitCode = 6`):** Hardware `Vulkan` and deterministic `ParallelCpu` are scheduled for `v0.3.1` alongside shared `reEngine.Host` rendering. Use `--backend opengl` or `--backend software` in `v0.3.0`.
- **"Automation loopback (--automation-port) is disabled in Release builds" (`ExitCode = 2`):** Public `Release` binaries exclude `Foundation.Automation` completely by design.
- **"Missing Offline Toolchain" on `.decomp` export:** You selected `Lightweight` profile or did not place an offline Ghidra + OpenJDK bundle inside `reEngine`'s `toolchain\` directory. Reinstall or copy the offline toolchain bundle for `.decomp` authoring; normal `.recomp` gameplay only needs local `.NET` AOT and works in `Lightweight`.
- **OpenGL reverted to Software automatically:** Your display adapter or driver does not expose an `OpenGL 3.3 Core` context (`Video` tab banner shows the exact reason). `Software CPU` rendering continues without crashing.
- **Game prompts for disc / media on startup:** Ensure your verified disc image or `PS-X EXE` binary resides in `<InstallDir>\data\rom\` and matches the module's expected media SHA-256 or catalog serial.
- **Cache rebuild after engine update:** Each `<Game>.Generated.dll` under `data\cache\` is keyed by module ID, media SHA-256, and engine version (`v0.3.0-hf.2`), so stale caches from older versions are automatically evicted and rebuilt cleanly.

---

## 7. Releases, Community Channels & Glossary

- **Beta Release Distribution (`v0.3.0+` Public Beta Pipeline):**
  - **GitHub Releases (`--prerelease` Beta):** Official 7-file package containing signed `reSlate-Setup-v0.3.0-hf.2-win-x64.exe`, standalone `reSlate-v0.3.0-hf.2-win-x64-Portable.zip`, `.7z` archive, `SHA256SUMS.txt`, and sanitized docs ([GitHub Releases](https://github.com/StreamItAllGit/reSlate/releases)).
  - **Discord Dev Updates:** Automated release announcements and community build notes.
  - **Notion Project Hub *(Optional)*:** Public project dossier and roadmap page *(linked once Notion public web publish is enabled)*.
- **Clean-Room `.decomp` Package:** Shareable clean-room decompilation metadata, offline Ghidra recipe scripts, and C/asset normalization descriptors containing zero copyrighted retail game media.
- **Local AOT Cache (`data\cache\`):** Player-PC generated native assembly synthesized strictly locally from your own verified `data\rom\` media and clean-room recipe.

---

## 8. License & Legal

- **Project License:** `reSlate` source code and shipped assemblies are licensed under the **Mozilla Public License 2.0 (`MPL-2.0`)** (`app\LICENSE`). Embedded container extraction helpers (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` (`LICENSE-7ZIP.txt`).
- **Clean-Room & Non-Affiliation Disclaimer:** `reSlate` distributes zero copyrighted game assets, PlayStation (`PS1`) BIOS ROMs, or Sony proprietary code. End users provide their own legally obtained disc media for use with clean-room `.recomp` modules. *PlayStation*, *PS1*, *DualShock*, and *PocketStation* are registered trademarks of Sony Interactive Entertainment Inc.; `reSlate` is an independent clean-room project with zero affiliation or endorsement.
