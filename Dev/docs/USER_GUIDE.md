# reSlate — User Guide

> **Release Status:** **Beta (Pre-1.0 — `v0.2.8` shipped under legacy name `reStation`; Next Public Beta Milestone: `v0.3.0` `reSlate`)**. All `0.x` releases are Beta pre-releases (`--prerelease`) until `v1.0.0` Desktop General Availability.

> **Note:** Clean-room SlateOne (`MIPS32 R3000A`) runtime, local Ahead-of-Time (AOT) compiler, and static recompilation engine. Users provide their own legally owned disc media or executables. Zero copyrighted vendor BIOS files, retail ROMs, disc images, save states, or pre-generated game binaries are bundled or distributed.

---

## 1. What `reSlate` Is & Who It Is For

`reSlate` (formerly `reStation`) is the `SlateOne` (`R3000A + GTE`) static recompilation engine inside the `reEngine` desktop suite. It lets players run verified `.recomp` game packages cleanly on Windows using an automatic local AOT compiler (`data\cache\`) supporting both standalone executables and verified disc-container overlays, hardware-accelerated `OpenGL 3.3` or `Software CPU` rendering (`v0.3.0` adds `Vulkan` and multithreaded `ParallelCpu`), customizable PC + controller keybinds, and instant `F5`/`F9` save states — while giving reverse-engineering teams an offline clean-room `.decomp` pipeline (`R3000A` + `GTE COP2`).

---

## 2. Install, Upgrade & Uninstall

### Canonical Install Layout
By default (`v0.3.0+`), `reSlate` installs into your local user application suite (requiring **no administrator elevation**, including the required `.ready` marker and never auto-starting in the background; `v0.2.8` and earlier installed under `engines\reStation\`):

- **Engine Binary Root:** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.0\` (`reSlate.exe`, `.ready`)
- **User Data & Local AOT Cache:** `<InstallDir>\data\` (`rom\`, `cache\`, `saves\`, `memcards\`, `config\settings.json`, `settings.controls.json`)
- **Logs:** `<InstallDir>\logs\` (`runtime.log`, `crashes\`)

### Installation Profiles (`--profile`)
- **Lightweight (Default for Players):** Installs the slim `reSlate` player, local AOT compilation runtime, and `reEngine` launcher integration. Does not require an offline decompiler toolchain.
- **Full (Developers & `.decomp` Creators):** Includes `Lightweight` capabilities plus integration for an offline Ghidra + OpenJDK toolchain placed alongside `reEngine` (`toolchain\`).

### Installer & Portable Options
- **Signed Setup EXE (`reSlate-Setup-v<Version>-win-x64.exe`):** Interactive wizard or silent CLI installer (`--install-dir <path>`, `--reengine-dir <path>`, `--profile Lightweight|Full`, `--migrate-legacy`, `--confirm-legacy-cleanup`, `--uninstall`); picking a generic parent folder in Setup Wizard auto-sorts into `<parent>\reEngine\engines\reSlate\v<Version>\`.
- **Portable Bundle (`reSlate-v<Version>-win-x64-Portable.zip`):** Self-contained portable archive (binaries, `.ready`, licenses, and sanitized documentation — zero setup EXE inside) for side-by-side manual extraction.

---

## 3. First-Run Wizard & Local AOT Compilation

1. **Choose `reEngine` Location & Profile:** Pick `Lightweight` or `Full` and confirm your target folder in the slim setup wizard.
2. **Legacy Upgrade Safety:** If `v0.1.x` or `v0.2.x` user data is detected, migration runs only if requested (`--migrate-legacy`) and prompts before removing stranded old runtime files. Your `data\rom\` images are always preserved.
3. **First Game Launch (`data\cache\`):** Place your verified `SlateOne` disc image (`.bin`/`.cue`/`.chd`/`.iso`) or executable inside `<InstallDir>\data\rom\` (or select it in the first-run media picker). On cold launch of a clean-room `.recomp` package, `reSlate` compiles `<Game>.Generated.dll` locally on your PC into `<InstallDir>\data\cache\` and loads it straight from cache on every subsequent launch.

---

## 4. Screen-by-Screen & In-Game Drawer Tour

Press **`F1`** (or pass `--open-settings` / click **Open Settings** inside the `F1` drawer) at any time during play to open the slide-over configuration drawer without losing your session:

### Video Tab
- **Renderer Backend (`Software` / `OpenGL 3.3` / `Vulkan` / `ParallelCpu`):** Switch renderers live at frame boundaries with immediate VRAM preservation (`Vulkan` and `ParallelCpu` land in `v0.3.0`). If your GPU driver fails to initialize hardware rendering, `reSlate` automatically reverts to `Software` and displays an inline warning banner.
- **Resolution Scale (`1x` – `8x`):** Adjust internal rendering resolution cleanly (`Software`, `OpenGL 3.3`, or `Vulkan`).
- **Aspect Ratio (`4:3` Default / `16:9` Widescreen):** Defaults to authentic `4:3` letterboxed/pillarboxed framing (`IsWidescreen16By9 = false`). When enabled and supported by the active game module, expands `3D` scenes seamlessly across Hor+ `16:9` without stretching `2D` HUD/fonts or `24-bit` FMV cutscenes (which remain centered `4:3` unstretched).
- **Frame Pacing (`Original 30 FPS` / `Enhanced FPS`):** Decoupled `60 Hz` hardware VBlank speed keeps menus, cutscenes, timers, and SPU audio at exact hardware pace while smooth interpolated `60–240+ Hz` Enhanced FPS runs on supported 3D scenes, synced live to `Target FPS` and `VSync`.
- **PGXP Precision & HUD Overlay (`F3`):** Toggle PGXP sub-pixel vertex precision and toggle the compact top-edge `1060x34` performance strip (`F3`).

### Controls Tab
- **Modern WASD + Mouse & Mouse Sensitivity:** Toggle Modern PC WASD + relative Mouse Look and adjust sensitivity smoothly without losing cursor capture across cutscenes or menus.
- **Per-Game Control Options:** Displays only game-specific control options declared by the loaded game module (stored under `metadataJson`) alongside live binding summaries for your active tier.

### Keybinds Tab (`F1 -> Keybinds`)
- Displays a widened, scrollable 5-column matrix (**Action | 1st | 2nd | 3rd | Gamepad**) supporting simultaneous keyboard (including Numpad `Num0`–`Num9`, `NumEnt`, `Num/`–`Num.`, `Ins`, `Home`, `End`, `RAlt`), mouse button, mouse wheel (`WheelUp` / `WheelDown`), and SDL gamepad bindings (`Cross`..`RT`, `D-Pad`, `Touchpad`), plus `Global 1:1`, `Game 1:1`, and `Game Modern PC` tier pills.
- **Bind Any Slot (`Left-Click`):** Click any slot to enter capture mode, then press your desired key, mouse button, scroll wheel direction, or gamepad button.
- **Cancel Capture (`Esc`):** Cancels waiting without clearing the existing binding.
- **Clear Optional Slot (`Right-Click` or `Delete` / `Backspace`):** Clears secondary/gamepad slots cleanly (`Right-Click` binds when capture mode is armed, and clears when idle).
- **Scroll:** Use Mouse Wheel, `PgUp`/`PgDn`, or the header `▲`/`▼` buttons to navigate large action lists.

### System Tab
- **Use Global Format Rules & Fast Disc Load (`Form1`):** Configure shared formatting defaults and CD Form1 fast-load behavior.
- **Master Volume:** Adjust SPU XA + ADPCM mixer output level (`0–100%`) cleanly.
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
| **`F5`** | Queue Quick-Save cleanly at the next VBlank edge (`data\saves\`) |
| **`F9`** | Queue Quick-Load cleanly at VBlank with exact mid-stack interpreter resume |
| **`Esc`** | Close active capture/drawer/game menu, or release captured mouse cursor |
| **`Alt + Enter`** | Toggle borderless fullscreen |

---

## 6. Troubleshooting & FAQ

- **"Missing Offline Toolchain" on `.decomp` export:** You selected `Lightweight` profile or did not place an offline Ghidra + OpenJDK bundle inside `reEngine`'s `toolchain\` directory. Reinstall or copy the offline toolchain bundle for `.decomp` authoring; normal `.recomp` gameplay only needs local `.NET` AOT and works in `Lightweight`.
- **OpenGL reverted to Software automatically:** Your display adapter or driver does not expose an `OpenGL 3.3 Core` context (`Video` tab banner shows the exact reason). `Software` rendering continues cleanly without crashing.
- **Game prompts for disc / media on startup:** Ensure your verified disc image or `PS-X EXE` binary resides in `<InstallDir>\data\rom\` and matches the module's expected media SHA-256 or catalog serial.
- **Cache rebuild after engine update:** Each `<Game>.Generated.dll` under `data\cache\` is keyed by module ID, media SHA-256, and engine version (`v0.2.8` / `v0.3.0`), so stale caches from older versions are automatically replaced and evicted cleanly.

---

## 7. Releases, Community Channels & Glossary

- **4-Channel Release Distribution (`v0.3.0+` Public Beta Pipeline):**
  - **GitHub Releases (`--prerelease` Beta):** Official 7-file package containing signed `reSlate-Setup-v<Ver>-win-x64.exe`, standalone `reSlate-v<Ver>-win-x64-Portable.zip`, `.7z` archive, `SHA256SUMS.txt`, and sanitized docs (`https://github.com/StreamItAllGit/reSlate/releases` upon `v0.3.0` public launch).
  - **Google Drive Release Mirror:** Mirrored 7-file release bundle and archive for direct download.
  - **Discord Dev Updates:** Automated release announcements and community build summaries.
  - **Notion Project Hub:** Public project dossier and roadmap page published and linked once the repository flips public at `v0.3.0`.
- **Clean-Room `.decomp` Package:** Shareable clean-room decompilation metadata, offline Ghidra recipe scripts, and C/asset normalization descriptors containing zero copyrighted retail game media.
- **Local AOT Cache (`data\cache\`):** Player-PC generated native assembly synthesized strictly locally from your own verified `data\rom\` media and clean-room recipe.

---

## 8. License & Legal

- **Project License:** `reSlate` source code and shipped assemblies are licensed under the **Mozilla Public License 2.0 (`MPL-2.0`)** (`app\LICENSE`). Embedded container extraction helpers (`7z.exe` / `7z.dll`) are licensed under `LGPL-2.1+` (`LICENSE-7ZIP.txt`).
- **Clean-Room & Non-Affiliation Disclaimer:** `reSlate` distributes zero copyrighted game assets, PlayStation (`PS1`) BIOS ROMs, or Sony proprietary code. End users provide their own legally obtained disc media for use with clean-room `.recomp` modules. *PlayStation*, *PS1*, *DualShock*, and *PocketStation* are registered trademarks of Sony Interactive Entertainment Inc.; `reSlate` is an independent clean-room project with zero affiliation or endorsement.
