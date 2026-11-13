# reStation — User Guide

**Synced Version:** `0.1.0` (`2026-11-13`)

---

## 1. Overview
`reStation` runs clean-room PlayStation 1 `.recomp` game modules using your own legally dumped PS1 disc images (`.iso`, `.bin`+`.cue`, `.chd`, `.7z`, or `.zip`). Zero game binaries, artwork, or Sony BIOS files are distributed with `reStation`.

## 2. Getting Started
1. Install or unpack `reStation` (or run a standalone `<Game>Recomp` bundle built on `reStation`).
2. Select or drop your `.recomp` game module (`.recomp.json`).
3. Place your retail disc image (`.chd`, `.iso`, `.bin`+`.cue`, `.7z`, or `.zip`) inside the module's gitignored `data\rom\` preload directory (auto-detected on launch), drag and drop it onto the First-Run Disc Gate, or pass `--disc <path>`.
4. Press `F1` to open the slide-over Settings Drawer (render scale, OpenGL/Software backend, `Original` vs `Enhanced` framerate, audio volume, keybinds) or `F3` to toggle the live telemetry HUD.
