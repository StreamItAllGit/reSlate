# reStation — Roadmap & Milestones

**Current Active Version:** `0.1.0`  
**Lifecycle State:** `active.state`  
**Last Updated:** 2026-11-13

## 1. North Star Goal
Provide a modular, clean-room **PS1 Static Recompilation Engine & Player** (`reStation`) that separates reusable R3000A lifting, CD/MDEC/SPU hardware timing, and OpenGL 3.3 Core / Software rendering from slim `<Game>Recomp` modules—loading any `.recomp` module with the user's legal `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, or `.zip` copy at high, rock-solid framerates and faithful audio/visual parity.

## 2. Active Milestone (`0.1.0` — Modular Engine Extraction & PS1 Parity Foundation)
- [x] Tier A 6-folder scaffold, `active.state`, `AGENTS.md`, Root Quartet, Manual Shelf, and original `.svg`/`.png`/`.ico` logo bundle.
- [ ] Consume `Foundation` (`v0.3.1` / `Foundation.Retro` `v0.1.1`) SDK binaries (`MediaContainerExtractor`, `HighResFramePacer`, `AudioRingBuffer`, `WavCaptureWriter`, `RgbaPngWriter`, `IVisibleTestHarness`, `Foundation.Installer`).
- [ ] Extract reusable R3000A CPU, COP0, GTE, MDEC, 2x CD-ROM (`150 sectors/s`), ADSR SPU/XA mixer, and `.recomp` module ABI from `CrownDepthsRecomp` into `reStation`.
- [ ] Implement `IRecompRenderBackend` with `OpenGL 3.3 Core` hardware renderer + `SoftwareCpu` oracle fallback (`1x`–`8x` PGXP).
- [ ] Wire `CrownDepthsRecomp` as the flagship `.recomp` module (`v0.1.3`) and verify `OPEN.EXE`, `GAME.EXE`, `END.EXE`, and multi-scene save-state PNG/WAV parity inside `Sandbox\` (`--headless` + `--visible-test`).

## 3. Upcoming Minor Horizons
- **`0.2.0` — Second PS1 Module Validation & Parallel CPU / SDL3_GPU Backends**: Validate multi-game compatibility across at least two `.recomp` modules (e.g., `Tekken 3` / `MeadowValley: BTN` / `CrownDepths II`) and add optional `SDL3_GPU` / multi-threaded CPU backends.
- **`0.3.0` — Modular Library UI & Widescreen/Texture Overrides**: In-app `.recomp` + BYO disc library browser, shader/texture override packs, and live profiler overlay.
- **`1.0.0` — Going Gold**: Full Golden-Master PS1 timing/SPU/GTE/CD parity across flagship modules on explicit Stream command.

## 4. Completed Milestones
- `0.1.0` (`2026-11-13`) — Scaffolded Tier A repository, original brand artwork (`reStation.{svg,png,ico}`), Manual Shelf, and initial architecture plan ([`0.1.0.2026-11-13.md`](Dev/docs/plans/0.1.0.2026-11-13.md)).

## 5. Icebox
- Companion sibling engines using the `re*` naming pattern (`re64` for N64, `reCube` for GameCube) built on the same `Foundation.Retro` contracts.
