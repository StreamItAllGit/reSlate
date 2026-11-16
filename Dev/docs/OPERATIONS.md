# reStation v0.2.6-hf.1 — Operations & Verification Runbook

- **Version:** `v0.2.6-hf.1` (`Target Track: 0.2.x`, Backward-Compatible Track: `0.1.x`)
- **Maintained By:** `@tessa` (Clarity & Ops) & `@devv` (Packaging & CLI)
- **Canonical Production Boundary:** Zero automated edits or unconfirmed writes to `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\` or any live installation during tests, builds, or audits.

---

## 1. Install Topology (`AllowAutoStart = false`)

`reStation v0.2.6-hf.1` follows `Foundation v0.4.1-hf.1` non-admin user-scope suite deployment:

```text
%LOCALAPPDATA%\Programs\StreamItAll\reEngine\
├── toolchain\                          (Optional Full profile offline Ghidra + OpenJDK)
└── engines\reStation\v0.2.6-hf.1\
    ├── reStation.exe                   (Single-file launcher -> app\reStation.Player.exe)
    ├── settings.example.json           (Clean-room overlay template staged at track root)
    ├── AGENTS.md                       (Runtime context anchor)
    ├── app\                            (reStation.Player.exe, reStation.Decomp.dll, reStation.Compiler.exe, Foundation.*.dll, reEngine.Core.dll, LICENSE, engine.manifest.json, app.manifest.json, AGENTS.md)
    ├── data\
    │   ├── rom\                        (User-owned BYO-ROM media; preserved across upgrades/uninstalls)
    │   ├── cache\                      (Player-PC local <Game>.Generated.dll AOT cache; auto-evicts stale keys)
    │   ├── saves\                      (Quick-save .rst1 + "RSTX" snapshots)
    │   ├── memcards\                   (Virtual .mcr memory cards)
    │   ├── settings.json               (User settings + Standard 18/19 keys + Schema A/B controls)
    │   └── settings.controls.json      (Global 1:1 baseline profile)
    └── logs\                           (runtime.log, installer.log, crashes\crash_*.json)
```

- **No Background Daemons:** `AllowAutoStart = false` is enforced in [`ReStationInstallerTheme.cs`](../src/reStation.Installer/ReStationInstallerTheme.cs) and [`LauncherArgsHelper.cs`](../src/reStation.Launcher/LauncherArgsHelper.cs); `--autostart` / `--auto-start` flags fail fast cleanly.

---

## 2. CLI Flags & Environment Contract

### Player (`reStation.exe`) & Launcher (`reStation.Launcher.exe`)
- `--module <path>` — Load unpacked module directory, `module.recomp.json`, or single-file ZIP `.recomp` archive.
- `--disc <path>` / `--preload-media <path>` — Point to verified PS1 disc/EXE media inside allowed root.
- `--data-dir <dir>` / `--config <file>` / `--log-dir <dir>` / `--port <n>` — Explicit runtime data, settings JSON, log directory, and diagnostic port overrides.
- `--reengine-dir <dir>` / `--suite-dir <dir>` / `--profile Lightweight|Full` — Override `reEngine` router location or active install profile.
- `--backend software|opengl` · `--scale <1..8>` · `--fps-mode original|enhanced` · `--target-fps <hz>` · `--vsync` / `--no-vsync`.
- `--execution-tier staticAotOnly|aotWithJitFallback|aotWithThreadedIr|jitBringup|interpreterOracle` — Select local AOT primary vs. optional JIT/IR diagnostic mode.
- `--jit-bringup` / `--force-zero-rwx` — Enable `JitBringupRecorder` + zero-RWX Threaded-IR diagnostic mode (`ExitCode = 5` on VBlank stall guard).
- `--verify` / `--tray-only` / `--open-settings` / `--enable-logs` / `--show-fps` / `--frames <n>` / `--dump-screenshot <png>` / `--wait` — Non-interactive verification, tray/background startup (`UiPresentationMode.TrayOrBackground`), UI toggles, and JobObject wait flags.

### Compiler (`reStation.Compiler.exe`)
- `--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--aot-recipe`, `--execution-tier`, `--emit-clean-room` (emits `aot-recipe.json` + clean `module.recomp.json` with zero `*.Generated.dll`), `--out-package`, `--compile-dll`, `--compile-generated-dll`, `--data-dir`, `--thin-shell`, `--help`.

### Installer (`reStation-Setup-v0.2.6-hf.1-win-x64.exe`)
- `--install-dir <dir>` / `--target-dir <dir>` / `--reengine-dir <dir>` / `--suite-dir <dir>` / `--profile Lightweight|Full` / `--data-dir <dir>` / `--config <file>` / `--log-dir <dir>` / `--migrate-legacy` / `--confirm-legacy-cleanup` / `--uninstall` / `--silent` (`--quiet`) / `--dry-run` / `--verify` / `--tray-only` (`APP_ISOLATED_TEST=1` suppresses host/registry mutations in automated tests).

---

## 3. Config & Settings File Reference

Bundled [`Dev/data/settings.example.json`](../data/settings.example.json) provides the clean-room `v0.2.6-hf.1` template persisted to `<dataDir>\settings.json`:
- **Suite & Decomp Metadata (`Standard 18`):** `installProfile`, `reEngineInstallDir`, `suiteInstallDir`, `umbrellaSubpath`, `decompSchemaVersion`, `goalDecompTier`, `executionTierMode`.
- **Audio/Video & Framing:** `backendId` (`"software"` / `"opengl"`), `resolutionScale`, `isWidescreen16By9` (`false` default for `4:3` framing), `frameRateMode`, `targetFps`, `isVsyncEnabled`, `isPgxpEnabled`, `isFpsOverlayEnabled`.
- **Input (`Schema A` & `Schema B`):** `inputProfileMode`, `controlMode`, `modernActionBindings` (`Dictionary<string, int[<=3]>` via `ModernBindingsJsonConverter` with legacy scalar read support), `modernControllerBindings` (`int[<=3]` codes `2001..2032`), `controllerDpadMode` (`"quickActions"` | `"dpadWalkStickRun"` | `"stickWalkDpadRun"`), `isometricMoveMode` (`"cropGrid"` | `"screenSpace"`), `jsonExtensionData` passthrough.

---

## 4. Ports, Locks & Sister-App Coexistence

- **Zero Network Ports:** `reStation` opens zero listening TCP/UDP sockets during gameplay, compilation, or `.decomp` execution.
- **Process Supervision (`--wait`):** [`LauncherArgsHelper.AttachJobGuard`](../src/reStation.Launcher/LauncherArgsHelper.cs) assigns a Windows `KILL_ON_JOB_CLOSE` Job Object so closing the caller/router terminates supervised `reStation.exe` sessions cleanly with exact exit code propagation (`0` clean, `5` bringup VBlank stall guard, `1` fatal/containment rejection).

---

## 5. Logs & Crash Bundles (Two-Tier Sweep)

1. **Production (`Read-Only` during Audits):** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reStation\v0.2.6-hf.1\logs\` (`runtime.log`, `crashes\`).
2. **Development (`RS-06`):** `Dev\logs\` (resolved automatically via [`ReStationLogger.TryResolveDevLogsDirectory`](../src/reStation.Core/Core/ReStationLogger.cs#L116-L132) inside source checkouts; excluded by root `.gitignore`).

Every long-running loop emits `<= 15s` structured telemetry heartbeats (`frame`, `fps`, `backend`, `subTicks`, `vramOps`, `spuVoices`, `status`). Crash bundles write `crashes\crash_*.json` plus preceding log tail context.

---

## 6. Backup, Restore & Legacy Migration (`RS-05`)

- **Source Archive (`Archive\`):** Before history rewrites or release tagging, create a compressed backup via `7z.exe a -mx=9 Archive\pre-scrub-v0.2.6-hf.1.7z ...` (excluding gitignored caches/logs/`bin`/`obj`).
- **Opt-In Legacy `v0.1.1` Migration:** [`ReStationLegacyMigrationHelper`](../src/reStation.Installer/ReStationLegacyMigrationHelper.cs) detects stranded `v0.1.1` installs (`app\data\` + `logs\crashes\crash_*.json`), copies `settings.json` and preserves `data\rom\` into the `v0.2.6-hf.1` `data\` directory **only** when `--migrate-legacy` is passed, and deletes non-ROM stranded artifacts **only** after explicit interactive confirmation or `--confirm-legacy-cleanup`.

---

## 7. Build, Sanitization & Release Packaging Procedure

1. **Zero-Warning Compile & Test Suite (Code-Only `<= 400` Lines Gate):**
   ```powershell
   $env:APP_ISOLATED_TEST = "1"
   dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true --no-restore
   ```
   Verify all `.cs` and `.ps1` files remain `<= 400` lines and **`100%` passing (`0` warnings, `0` failures, `0` skipped)**.
2. **Single-Pass Release Build (`Dev\scripts\build_release.ps1`):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\build_release.ps1 -Version 0.2.6-hf.1
   ```
   Emits `Builds\v0.2.6-hf.1\` (including `AGENTS.md`, `app\LICENSE`, `engine.manifest.json` with `supportedEngineTracks: ["0.2.x", "0.1.x"]`, zero `data\rom\`), compiles `Releases\v0.2.6-hf.1\README.md` from `USER_GUIDE.md` via [`sanitize_release_docs.ps1`](../scripts/sanitize_release_docs.ps1), validates zero-PII/4-pillar installer via `reStation-Setup-v0.2.6-hf.1-win-x64.exe`, publishes immutable `SharedLibs\reStation\v0.2.6-hf.1\`, and snapshots `Archive\reStation-v0.2.6-hf.1-src.7z`.
3. **Public History Hygiene Check:** Before pushing public tags, verify zero `ghidra_psx_ldr`, Sony SDK headers, `*.Generated.dll`, retail ROMs, workstation paths (`<WORKSTATION_ROOT>\`, `<PROGRAMS_ROOT>\`, `<USER_HOME>\`), hostnames, or private IDs exist in tracked source/docs/history (`git-filter-repo`).

---

## 8. Isolated Verification & Log Audit Recipe (`@inspecter`)

1. Run isolated xUnit + installer CLI tests under `APP_ISOLATED_TEST=1` (verifying `--dry-run`, `--tray-only`, `--verify`, and `--uninstall` containment via `InstallerRuntimeContext` and `AppEnvironment.AssertPathContained` without touching `%LOCALAPPDATA%` or `%LOCALAPPDATA%\Programs\`).
2. Verify offline AOT cold-to-warm cache generation in isolated temp dirs (including recipe-ordered `<container>#<slice>` overlays and `generatedCacheAssemblyName` PE assembly verification) and clean-room `.decomp` provider D0–D5 dry/offline check.
3. Sweep both log roots (`%LOCALAPPDATA%\Programs\...\logs\` read-only + `Dev\logs\`) with preceding 20–50 lines and write timestamped report to `Dev\docs\reports\`.

---

## 9. Incident Playbooks

| Symptom | Root Diagnosis | Resolution |
| :--- | :--- | :--- |
| **`PSX_GHIDRA_BATCH_FAILED` / `MISSING_OFFLINE_TOOLCHAIN`** | `Full` `.decomp` D2 run invoked without valid `toolchain\ghidra` + JDK under `reEngine` | Point `--toolchain-dir` / install `reEngine` Full toolchain; clean-room script generation still succeeds offline without `invokeGhidra=true` |
| **OpenGL immediately reverts to Software with banner** | GPU/VM lacks OpenGL 3.3 Core context | `HostVideoSurface.TrySwitchBackend` automatically preserves Software pipeline; update GPU driver or stay on `Software` |
| **Installer or player fails with path containment error** | Candidate path attempted to escape an allowed root (`AppEnvironment.AssertPathContained`) | Pass strictly contained subpaths inside the configured `--install-dir` / `--data-dir` / isolated root |
| **Exit code `5` during `--jit-bringup`** | `JitBringupRecorder` hit `MaxStalledCyclesWithoutVBlank` spin threshold | Inspect emitted bringup profile JSON under `<dataDir>\logs\` or run primary AOT cache |
| **Stranded `v0.1.1` files linger after upgrade** | `--migrate-legacy` ran without `--confirm-legacy-cleanup` (by safety design) | Re-run with `--confirm-legacy-cleanup` or approve the interactive confirmation prompt |
