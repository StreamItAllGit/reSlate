# reStation v0.2.5 — User Guide

> **Note:** Clean-room PlayStation 1 (`PS1`) runtime, local Ahead-of-Time (AOT) compiler, and static recompilation engine. Users provide their own legally owned PS1 disc media or executables. Zero copyrighted Sony BIOS files, retail ROMs, disc images, save states, or pre-generated game binaries are bundled or distributed.

---

## 1. What `reStation` Is & Who It Is For

`reStation` is the PlayStation 1 (`PS1`) engine inside the `reEngine` desktop suite. It lets players run verified `.recomp` game packages cleanly on Windows using an automatic local AOT compiler (`data\cache\`) supporting both standalone PS-X executables and verified disc-container overlays, hardware-accelerated `OpenGL 3.3` or `Software CPU` rendering, customizable PC + controller keybinds, and instant `F5`/`F9` save states — while giving reverse-engineering teams an offline clean-room `.decomp` pipeline (`R3000A` + `GTE COP2`).

---

## 2. Install, Upgrade & Uninstall

### Canonical Install Layout
By default, `reStation v0.2.5` installs into your local user application suite (requiring **no administrator elevation** and never auto-starting in the background):

- **Engine Binary Root:** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reStation\v0.2.5\`
- **User Data & Local AOT Cache:** `<InstallDir>\data\` (`rom\`, `cache\`, `saves\`, `memcards\`, `settings.json`, `settings.controls.json`)
- **Logs:** `<InstallDir>\logs\` (`runtime.log`, `crashes\`)

### Installation Profiles (`--profile`)
- **Lightweight (Default for Players):** Installs the slim `reStation` player, local AOT compilation runtime, and `reEngine` launcher integration. Does not require an offline decompiler toolchain.
- **Full (Developers & `.decomp` Creators):** Includes `Lightweight` capabilities plus integration for an offline Ghidra + OpenJDK toolchain placed alongside `reEngine` (`toolchain\`).

### Installer CLI Options
- `--install-dir <path>` or `--reengine-dir <path>` — Pick a custom `reEngine` suite root (`reStation-Setup-v0.2.5-win-x64.exe`); choosing a generic parent folder in Setup Wizard auto-sorts into `<parent>\reEngine\engines\reStation\v0.2.5\`.
- `--profile Lightweight|Full` — Select installation profile non-interactively.
- `--migrate-legacy` — Opt-in import of `settings.json` and `data\rom\` media from older `v0.1.x` installations into `v0.2.5`.
- `--confirm-legacy-cleanup` — Explicit confirmation flag to remove stranded non-ROM legacy artifacts only after preserving `data\rom\` and `settings.json`.
- `--uninstall` — Removes the `v0.2.5` engine track cleanly without touching your `data\rom\` backups or other side-by-side `0.1.x` tracks.

---

## 3. First-Run Wizard & Local AOT Compilation

1. **Choose `reEngine` Location & Profile:** Pick `Lightweight` or `Full` and confirm your target folder in the slim setup wizard.
2. **Legacy Upgrade Safety:** If `v0.1.1` user data is detected, migration runs only if requested (`--migrate-legacy`) and prompts before removing stranded old runtime files. Your `data\rom\` images are always preserved.
3. **First Game Launch (`data\cache\`):** Place your verified PS1 disc image (`.bin`/`.cue`/`.chd`/`.iso`) or executable inside `<InstallDir>\data\rom\` (or select it in the first-run media picker). On cold launch of a clean-room `.recomp` package, `reStation` compiles `<Game>.Generated.dll` locally on your PC into `<InstallDir>\data\cache\` and loads it straight from cache on every subsequent launch.

---

## 4. Screen-by-Screen & In-Game Drawer Tour

Press **`F1`** (or pass `--open-settings` / click **Open Settings** inside the `F1` drawer) at any time during play to open the slide-over configuration drawer without losing your session:

### Video Tab
- **Renderer Backend (`Software` / `OpenGL 3.3`):** Switch renderers live at frame boundaries with immediate VRAM preservation. If your GPU driver fails to initialize `OpenGL 3.3`, `reStation` automatically reverts to `Software` and displays an inline warning banner.
- **Resolution Scale (`1x` – `8x`):** Adjust internal rendering resolution cleanly (`Software` or `OpenGL 3.3`).
- **Aspect Ratio (`4:3` Default / `16:9` Widescreen):** Defaults to authentic `4:3` letterboxed/pillarboxed framing (`IsWidescreen16By9 = false`). Only switches to `16:9` when both the toggle is enabled **and** the active game module reports true widescreen support.
- **Frame Pacing (`Original 30 FPS` / `Enhanced 60 FPS`):** Smooth interpolated sub-tick frame pacing where supported by the loaded module, synced live to the frame limiter (`Target FPS`) and `VSync`.
- **PGXP Precision & HUD Overlay (`F3`):** Toggle PGXP sub-pixel vertex precision and toggle the compact top-edge `1060x34` performance strip (`F3`).

### Audio Tab
- **Master Volume / Mute:** Adjust SPU XA + ADPCM mixer output level (`0–100%`) or mute cleanly.

### Controls Tab
- **Input Tier (`Global 1:1` / `Game 1:1` / `Modern PC`):** Choose authentic PS1 1:1 pad mapping or game-specific Modern WASD + Mouse Look + Native Actions.
- **Controller D-Pad Mode (`Schema B`):** Choose `Quick Actions` (default, D-Pad triggers quick actions while analog stick moves), `D-Pad Walk / Stick Run`, or `Stick Walk / D-Pad Run`.
- **Isometric Movement Mode (`Schema B`):** Choose `Crop Grid` (default, aligned to game camera/tile axes) or `Screen Space` (aligned to monitor axes).
- **Invert Mouse Y & Sensitivity:** Tune relative mouse look speed and vertical inversion.

### Keybinds Tab (`F1 -> Keybinds`)
- Displays a widened, scrollable 5-column matrix (**Action | 1st | 2nd | 3rd | Gamepad**) supporting simultaneous keyboard (including Numpad `Num0`–`Num9`, `NumEnt`, `Num/`–`Num.`, `Ins`, `Home`, `End`, `RAlt`), mouse button, mouse wheel (`WheelUp` / `WheelDown`), and SDL gamepad bindings (`Cross`..`RT`, `D-Pad`, `Touchpad`).
- **Bind Any Slot (`Left-Click`):** Click any slot to enter capture mode, then press your desired key, mouse button, scroll wheel direction, or gamepad button.
- **Cancel Capture (`Esc`):** Cancels waiting without clearing the existing binding.
- **Clear Optional Slot (`Right-Click` or `Delete` / `Backspace`):** Clears secondary/gamepad slots cleanly (`Right-Click` binds when capture mode is armed, and clears when idle).
- **Scroll:** Use Mouse Wheel, `PgUp`/`PgDn`, or the header `▲`/`▼` buttons to navigate large action lists.

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
- **Game prompts for disc / media on startup:** Ensure your verified disc image or PS-X binary resides in `<InstallDir>\data\rom\` and matches the module's expected media SHA-256 or disc serial.
- **Cache rebuild after engine update:** Each `<Game>.Generated.dll` under `data\cache\` is keyed by module ID, ROM SHA-256, and engine version (`v0.2.5`), so stale caches from older versions are automatically replaced and evicted cleanly.

---

## 7. Glossary & Licensing

- **Clean-Room `.decomp` Package:** Shareable clean-room decompilation metadata, offline Ghidra recipe scripts, and C/asset normalization descriptors containing zero copyrighted retail game media.
- **Local AOT Cache (`data\cache\`):** Player-PC generated native assembly synthesized strictly locally from your own verified `data\rom\` media and clean-room recipe.
- **License:** `reStation` source code and shipped assemblies are licensed under the **Mozilla Public License 2.0 (`MPL-2.0`)** (`LICENSE` bundled inside `app\`).
