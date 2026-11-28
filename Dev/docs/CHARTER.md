# Project Charter: reSlate

**Status:** Active  
**Last Updated:** 2026-11-28  
**Primary Audience:** Players running legally owned SlateOne (PS1 / MIPS32 R3000A) titles through clean-room native builds, plus clean-room `.decomp` and `.recomp` module authors

---

## 1. Core Identity & Mission

**reSlate** (formerly `reStation`, repository folder `Personal/reStation`) is a clean-room `.NET 8.0` static recompilation engine, `.decomp` lifter, and player-PC ahead-of-time (AOT) compiler (`platformId: "ps1"`) built on `Foundation` and `reEngine`.

It exists to decouple clean-room `MIPS32 R3000A + GTE` reverse engineering from end-user gameplay—turning shareable recompilation recipes and user-supplied disc media into locally compiled native assemblies rather than acting as a generic compatibility emulator.

Success means deterministic, hardware-accurate execution and aspect-faithful presentation from `v0.3.x` public beta releases through `v1.0.0` desktop gold and future multi-platform releases.

## 2. Who It Is For (and Not For)

- **Built For Players:** Desktop users who want native, hardware-timed execution of legally dumped SlateOne disc images or executables packaged as clean-room `.recomp` cartridges, with modern controls, optional clean-room HD texture packs, and reliable Slot 1+2 `.mcr` saves.
- **Built For Module & `.decomp` Authors:** Reverse-engineering teams who author clean-room `R3000A + GTE` decompilation recipes, frame patches, and sub-tick enhancement hooks verified by strict zero-warning compilation.
- **Built For Ecosystem Contributors:** Maintainers and contributors extending `OpenGL`, `Vulkan`, `ParallelCpu`, or future hardware rendering backends, packaging safety, and automated isolation tests without breaking existing cartridges.
- **Not Built For:** Users looking for bundled game ROMs, vendor console BIOS files, plug-and-play piracy packs, background system services, or generic interpreter-first emulation hacks.

## 3. Non-Negotiable Principles

1. **AOT-Primary Static Recompilation:** Certified `.recomp` packages compile locally into native C# assemblies (`staticAotOnly` and AOT-first tiers), with live Shared-IR / RyuJIT / Zero-RWX translation handling unlifted blocks and bring-up profiling. Generic interpreter execution stays strictly quarantined behind an explicit diagnostic flag and never serves as a silent runtime fallback.
2. **Zero-Copyright Clean-Room Discipline:** Tracked repositories, `.decomp` specifications, and `.recomp` cartridges distribute zero copyrighted disc images, retail game assets, vendor BIOS dumps, or pre-built generated game assemblies. Players supply verified local media under `data\rom\`, and clean-room C output must compile with zero warnings under strict warning flags.
3. **Hardware Cadence & Aspect Fidelity:** Guest VBlank timing and SPU audio remain locked at `60.0 Hz` (`44.1 kHz`) regardless of presentation frame rate or Fast-Forward multiplier, and display modes preserve authentic pixel proportions by default (`Native PAR` / `4:3`) without stretching `4:3` HUDs or `24-bit` FMVs into `16:9`.
4. **Zero-Daemon Local Containment:** Both launcher and player enforce `AllowAutoStart = false` with zero background daemons or release-mode automation listeners, and validate path containment before reading or writing user directories.
5. **Backward-Compatible Cartridge ABI:** `reStation.Contracts` and `reStation.Core` preserve their assembly names and `reStation.*` namespaces alongside bidirectional `reSlate` / `reStation` alias matching so `0.1.x`–`0.3.x` cartridges keep running across updates.

## 4. Hard Boundaries & Non-Goals

### Explicit Non-Goals
- **No Silent Emulation Fallback:** Running retail titles through an unlifted compatibility interpreter without explicit diagnostic opt-in is permanently out of scope.
- **No Copyrighted Assets or Pre-Built Game DLLs:** Vendor BIOS ROMs, retail disc images, extracted game media, and pre-compiled game cache DLLs are never distributed with the engine or cartridges.
- **No Niche Peripherals Before `v1.0.0`:** Multitaps, lightguns, mouse peripherals, external LCD memory docks, and serial link cables stay outside the `v1.0.0` target, while Port 1+2 dual analog and rumble remain supported.
- **No Standalone Native Game Ports or Library Browsers:** Independent native C/C++ ports and multi-game library browsing live outside `reSlate` (`reEngine` owns library browsing).
- **No Unapproved Cloud Binary Mirrors or Automated External Syncs:** Release binaries ship via official versioned packages and GitHub Releases rather than cloud drive uploads or automated workspace syncs.

### Ecosystem & Ownership Seams
- **`reSlate` owns:** `R3000A` CPU, `COP0`, `GTE`, `MDEC`, `2x` CD-ROM, `SPU`, `SIO0` / `.mcr` persistence, clean-room BIOS HLE, `OpenGL` / `Vulkan` / `ParallelCpu` / `DirectX 12` / `Software` GPU backends, local AOT compilation, and clean-room `PS1` `.decomp` lifting.
- **`reEngine` owns:** Library browsing, multi-engine routing, shared SDL3 host windowing/presenters (`reEngine.Host`), shared overlay rails, and opt-in runtime API hosting.
- **`Foundation` owns:** Single-file launcher plumbing, installer bootstrap/migration, path containment, diagnostics, and archive extraction helpers.
- **Downstream `.recomp` titles own:** Title-specific frame patches, native input profiles, sub-tick hooks, clean-room recipes, and human playtest sign-off toward `v1.0.0` symmetric confirmation.

### Read-Only & Operational Boundaries
- Keep `Foundation\`, `reEngine\`, upstream research repositories, shared design documentation, and live user installations strictly read-only during development and automated tests (`APP_ISOLATED_TEST=1`).
- Strip debug/test automation hooks and diagnostic crash-dump helpers cleanly out of shipping Release builds.

## 5. Agent Steering & Trade-Off Heuristics

When planning, building, or reviewing changes, resolve ambiguity using these priority rules:

1. **Prefer Determinism, Containment, and ABI Compatibility over Shortcuts:** Never break `0.1.x`–`0.3.x` cartridge contracts, weaken path containment, or accept unguarded `.mcr` / VRAM mutations for convenience.
2. **Prefer Upstream `reEngine` / `Foundation` Rails over Local Duplication:** Reuse shared host presenters, input contracts, containment guards, and installer primitives rather than building bespoke copies inside the player shell.
3. **Prefer Small Cohesive Units and Measurable Parity:** Keep source files concise and single-purpose, keep hot paths allocation-free where verified bit-exact, and prove changes with isolated Release + Debug unit tests and sandbox end-to-end checks.
4. **Prefer Responsive Failures and Safe Backups:** Open interactive feedback promptly during long archive operations with clean cancellation, and write atomic backups before mutating user memory cards or settings.
5. **When in Doubt:** Keep changes reversible, protect user data, and escalate charter or scope tensions for explicit human sign-off rather than loosening boundaries.

## 6. Conceptual Mental Model & Tone

Think of **reSlate** as a clean-room hardware bench paired with a local ahead-of-time compiler rather than a traditional console emulator:

- **Offline Authoring vs. Player Execution:** Reverse engineers distill hardware behavior and executable layouts into shareable, asset-free recipes, while players compile those recipes natively on their own machine against their own verified disc media.
- **Layered Hardware Fidelity:** Every rendering backend (`OpenGL 3.3`, `Vulkan`, `ParallelCpu`, `DirectX 12`, and `Software`) and input tier (`Global 1:1`, `Game 1:1`, `Modern PC`) answers to the same `60.0 Hz` VBlank/SPU clock and `1x` canonical VRAM truth.
- **Plain-English Clarity:** Public documentation, diagnostics, and release notes stay clear, concrete, and free of internal process jargon so players and contributors can understand behavior at a glance.

## 7. Milestone & Lifecycle Discipline

- **Pre-1.0 Transparency:** Until `v1.0.0`, all `0.x` releases ship as public betas with visible version indicators in the main interface and settings drawer so bug reports map cleanly to exact builds.
- **Symmetric Gold Confirmation:** Major releases—starting with `v1.0.0` Desktop Gold—require verified parity and human playtest sign-off across at least two real downstream recompilation titles rather than synthetic unit tests alone.
- **Scoped Maintenance:** Targeted `0.3.x` maintenance releases resolve user-facing friction, performance hot spots, and public documentation drift without breaking `0.1.x`–`0.3.x` cartridge compatibility.

## 8. Amendment Policy

This charter guides scoping, planning, implementation, and audits across the repository.

Contributors and automated workflows must flag conflicts between a requested change and these boundaries rather than silently bypassing them; altering core principles, ABI guarantees, or non-goals requires explicit human approval.

When an existing rule becomes obsolete due to an approved milestone transition (such as retiring the single-file settings compatibility exception at `v0.5.0`), update this charter alongside the architecture and operations manuals in the same release cycle.
