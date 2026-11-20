# reSlate — Operations & Verification Runbook

- **Synced Version:** `0.3.0-hf.3` (`2026-11-19` · Public Beta Hotfix · `appId: "reslate"` · `engineId: "reSlate"` · Track `0.3.x`, Alias-Compatible with `0.2.x` / `0.1.x`) | **Next Target:** `v0.3.1` *(Shared `reEngine.Host` `Vulkan`, `ParallelCpu`, Enhancement SDK & QoL)*
- **Scope:** Install Topology, CLI Flags, Settings, Log Audit & Release Verification
- **Canonical Production Boundary:** Zero automated edits or unconfirmed writes to `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\` or any live installation during tests, builds, or audits.

---

## 1. Install Topology (`AllowAutoStart = false`)

Starting in **`v0.3.0`**, `reSlate` installs per-user under `engines\reSlate\v0.3.0-hf.3\` alongside the required `.ready` discovery marker (`v0.2.8` and earlier installed under `engines\reStation\v<Ver>\`):

```text
%LOCALAPPDATA%\Programs\StreamItAll\reEngine\
├── toolchain\                          (Optional Full profile offline Ghidra + OpenJDK)
└── engines\reSlate\v0.3.0-hf.3\        (v0.2.8 and earlier: engines\reStation\v<Ver>\)
    ├── reSlate.exe                     (Single-file launcher -> app\reSlate.Player.exe)
    ├── .ready                          (Required for reEngine v0.3.0+ engine discovery)
    ├── engine.manifest.json            (engineId: "reSlate", appId: "reslate", decompProviderAssembly: "app/reSlate.Decomp.dll")
    ├── settings.example.json           (Clean-room v0.3.0 overlay template staged at track root)
    ├── app\                            (reSlate.Player.exe, reSlate.Decomp.dll, reSlate.Compiler.exe, reStation.{Contracts,Core}.dll, Foundation.{Core,Retro,Installer,Launcher}.dll v0.5.0, reEngine.{Core,Host}.dll v0.3.0, SDL3.dll, 7z.{exe,dll}, LICENSE, LICENSE-7ZIP.txt, engine.manifest.json — strictly ZERO AGENTS.md & ZERO Release Foundation.Automation.dll)
    ├── data\
    │   ├── config\settings.json        (v0.3.0 canonical settings path; auto-migrates <dataDir>\settings.json)
    │   ├── rom\                        (User-owned BYO-ROM media; preserved across upgrades/uninstalls)
    │   ├── cache\                      (Player-PC local <Game>.Generated.dll AOT cache; auto-evicts stale keys)
    │   ├── hdpacks\                    (Reserved for v0.3.1 clean-room texture overrides; unused in v0.3.0)
    │   ├── saves\                      (Quick-save .rst1 + "RSTX" snapshots incl. "MCD2")
    │   ├── memcards\                   (Canonical v0.3.0 Slot 1+2 .mcr files via SIO0 0x81 + BIOS B0:0x4E-0x50 _new_card)
    │   └── settings.controls.json      (Global 1:1 baseline profile)
    └── logs\                           (runtime.log, installer.log, crashes\crash_*.json)
```

- **No Background Daemons:** `AllowAutoStart = false` is enforced in [`ReStationInstallerTheme.cs`](../src/reStation.Installer/ReStationInstallerTheme.cs) and [`LauncherArgsHelper.cs`](../src/reStation.Launcher/LauncherArgsHelper.cs); `--autostart` / `--auto-start` flags fail fast cleanly.

---

## 2. CLI Flags & Environment Contract

### Player (`reSlate.exe` / `app\reSlate.Player.exe`) & Launcher (`reStation.Launcher`)
- `--module <path>` — Load unpacked module directory, `module.recomp.json`, or single-file ZIP `.recomp` archive (`EngineAliasCatalog.AreEquivalent` accepts `"reSlate"` and `"reStation"`).
- `--disc <path>` / `--preload-media <path>` — Point to verified `SlateOne` (`R3000A`) disc/EXE media inside allowed root.
- `--data-dir <dir>` / `--config <file>` / `--log-dir <dir>` / `--save-dir <dir>` / `--port <n>` — Explicit runtime data, settings JSON (defaults to `config/settings.json`), log directory, save directory override (`--save-dir` accepted; full router hook in `v0.4.0`), and port overrides.
- `--reengine-dir <dir>` / `--suite-dir <dir>` — Override companion `reEngine` / suite router directory (`reSlate.Player.exe --profile <json>` accepts a runtime profile JSON path, whereas `reSlate-Setup` `--profile Lightweight|Full` selects install profile).
- `--backend opengl|software` · `--require-backend opengl|software|vulkan|parallelCpu` (`vulkan` and `parallelCpu` are **deferred to `v0.3.1`**; `--require-backend vulkan|parallelCpu` or an unavailable backend immediately exits with `ExitCode = 6`) · `--scale 1|2|4|8` · `--fps-mode original|enhanced` · `--target-fps <30..500|unlocked>` (`30` decimates swap/presentation only while keeping SPU at `60 Hz`) · `--vsync [on|off]` / `--no-vsync`.
- `--execution-tier staticAotOnly|aotWithJitFallback|aotWithThreadedIr|jitBringup|interpreterOracle` — Live native RyuJIT / Zero-RWX IR (`Tier3ExecutionCoordinator`) executes unlifted blocks directly inside `PsxCpu`; `staticAotOnly` fails closed; `PsxInterpreter` is strictly quarantined to `interpreterOracle`.
- `--jit-bringup` / `--force-zero-rwx` — Enable `JitBringupRecorder` (emitting canonical `isPureStaticReady: false` during active bring-up + `[Obsolete]` `isR4PromotionReady` read compat) + Zero-RWX Threaded-IR diagnostic mode (`ExitCode = 5` on VBlank stall guard).
- `--verify` / `--doctor` / `--tray-only` / `--dry-run` / `--open-settings` / `--enable-logs` / `--show-fps` / `--frames <n>` / `--fixture <path>` / `--dump-screenshot <png>` / `--dump-audio <wav>` / `--wait` — Non-interactive health checks, host diagnostics (`--doctor`), tray/background startup (`UiPresentationMode.TrayOrBackground`), UI toggles, fixture/audio/frame dumps, and Job-Object synchronous wait mode.
- `--automation-port <n>` / `--automation-token <tok>` — **`Debug` / `RESTATION_TEST_AUTOMATION=true` only** loopback inspection server (`127.0.0.1`); pure `Release` binaries strip `Foundation.Automation` completely and fail closed with `ExitCode = 2`.

### Compiler (`reSlate.Compiler.exe`)
- `--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--aot-recipe`, `--execution-tier`, `--emit-clean-room` (emits `aot-recipe.json` + clean `module.recomp.json` with zero `*.Generated.dll`), `--out-package`, `--compile-dll`, `--compile-generated-dll`, `--data-dir`, `--thin-shell`, `--help`.

### Installer (`reSlate-Setup-v0.3.0-hf.3-win-x64.exe`)
- `--install-dir <dir>` / `--target-dir <dir>` / `--reengine-dir <dir>` / `--suite-dir <dir>` / `--payload <zip>` / `--profile Lightweight|Full` / `--data-dir <dir>` / `--config <file>` / `--log-dir <dir>` / `--automation-port <n>` / `--automation-token <tok>` (`Debug`/`RESTATION_TEST_AUTOMATION` only; `ExitCode = 2` in `Release`) / `--migrate-legacy` / `--confirm-legacy-cleanup` / `--uninstall` / `--silent` (`--quiet`) / `--dry-run` / `--verify` / `--tray-only` (`APP_ISOLATED_TEST=1` suppresses host/registry mutations in automated tests).

---

## 3. Config & Settings File Reference

Bundled [`Dev/data/settings.example.json`](../data/settings.example.json) provides the clean-room `v0.3.0-hf.3` template (`appId: "reslate"`, `umbrellaSubpath: "StreamItAll\\reEngine\\engines\\reSlate\\v0.3.0-hf.3"`) persisted to `<dataDir>\config\settings.json` (`SettingsMigrationHelper.MigrateForConfigPathIfNeeded` automatically migrates legacy `<dataDir>\settings.json` non-destructively while keeping `.bak`):
- **Suite & Decomp Metadata:** `installProfile`, `reEngineInstallDir`, `suiteInstallDir`, `umbrellaSubpath`, `decompSchemaVersion`, `goalDecompTier`, `executionTierMode`.
- **Audio/Video & Framing:** `backendId` (`"opengl"` / `"software"`), `resolutionScale`, `isWidescreen16By9` (`false` default for `4:3` framing; unstretched Hor+ `16:9` when enabled), `frameRateMode` (`"original"` with `targetFps=30` swap decimation vs `"enhanced"` `60–240+`/`0` Max), `targetFps`, `isVsyncEnabled` (`"VSync (Caps to Monitor Hz)"`), `isPgxpEnabled`, `isFpsOverlayEnabled`, `shouldFastLoadData`, `masterVolume`, `useGlobalFormatRules`.
- **Input (`Schema A` / `Schema B` & Per-Game Options):** `inputProfileMode`, `controlMode`, `keyBindings` (`bindings` KBM scancodes + nested `controllerBindings` codes `2001..2032` or `0` cleared across `Global 1:1` and `Game 1:1`), `modernActionBindings` (`Dictionary<string, int[<=3]>` via `ModernBindingsJsonConverter` with legacy scalar read support), `modernControllerBindings` (`int[<=3]` with `SetModernControllerSlot` clean single-slot replacement), `metadataJson` (`Dictionary<string, string>` per-game `ControlOptions`), legacy `controllerDpadMode`/`isometricMoveMode` compatibility fields, and `jsonExtensionData` passthrough.

---

## 4. Ports, Locks & Sister-App Coexistence

- **Zero Network Ports in Release:** Pure `Release` `reSlate.exe`, `reSlate.Player.exe`, and `reSlate-Setup-v0.3.0-hf.3-win-x64.exe` binaries strip `Foundation.Automation` completely, open zero listening TCP/UDP sockets during normal operation, and fail closed with `ExitCode = 2` when `--automation-port` is passed (only `Debug` / `RESTATION_TEST_AUTOMATION=true` builds expose `127.0.0.1` loopback automation).
- **Process Supervision (`--wait`) & Exit Codes:** [`LauncherArgsHelper.AttachJobGuard`](../src/reStation.Launcher/LauncherArgsHelper.cs) assigns a Windows `KILL_ON_JOB_CLOSE` Job Object so closing the caller/router terminates supervised `reSlate.exe` sessions cleanly with exact exit code propagation (`0` clean, `1` runtime/`--verify` fault, `2` CLI error / disallowed Release `--automation-port`, `3` path containment rejection, `4` disc/archive verification error, `5` `--jit-bringup` VBlank stall guard, `6` unavailable or `v0.3.1`-deferred `--require-backend`, `130` `SIGINT`).

---

## 5. Logs & Crash Bundles (Two-Tier Sweep)

1. **Production (`Read-Only` during Audits):** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.0-hf.3\logs\` (`runtime.log`, `crashes\`; `v0.2.8` and earlier wrote under `engines\reStation\v<Ver>\logs\`).
2. **Development Logs:** `Dev\logs\` (resolved automatically via [`ReStationLogger.TryResolveDevLogsDirectory`](../src/reStation.Core/Core/ReStationLogger.cs#L117-L134) inside source checkouts; excluded by root `.gitignore`).

Every long-running loop emits `<= 15s` structured telemetry heartbeats (`frame`, `fps`, `backend`, `subTicks`, `vramOps`, `spuVoices`, `status`). Crash bundles write `crashes\crash_*.json` plus preceding log tail context.

---

## 6. Backup, Restore & Legacy Migration

- **Source Archive (`Archive\`):** Before history rewrites or release tagging, create a compressed backup via `7z.exe a -mx=9 Archive\pre-scrub-v0.3.0.7z ...` (excluding gitignored caches/logs/`bin`/`obj`).
- **Opt-In Legacy Migration:** [`ReStationLegacyMigrationHelper`](../src/reStation.Installer/ReStationLegacyMigrationHelper.cs) detects stranded `v0.1.x` / `v0.2.x` installs (`app\data\` + `logs\crashes\crash_*.json`), copies `settings.json` to `data\config\settings.json` and preserves `data\rom\` into the `v0.3.0` `data\` directory **only** when `--migrate-legacy` is passed, and deletes non-ROM stranded artifacts **only** after explicit interactive confirmation or `--confirm-legacy-cleanup`. At runtime, [`MemCardMcrStore`](../src/reStation.Core/Bios/MemCardMcrStore.cs) non-destructively copies legacy `<dataDir>\saves\memcard_*.mcr` files to `<dataDir>\memcards\`.

---

## 7. Build, Sanitization & Beta Release Procedure

1. **Dual-Config Zero-Warning Compile & Test Suite (`250 / 400 / 600` Code-Only SLOC Gate):**
   ```powershell
   $env:APP_ISOLATED_TEST = "1"
   dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true --no-restore
   dotnet test Dev\src\reStation.slnx -c Debug -p:TreatWarningsAsErrors=true --no-restore
   ```
   Verify all non-exempt production files follow the code-only SLOC ceiling (`<= 250` preferred, `400` soft cap, `600` hard cap excluding comments/whitespace; tests exempt) and **`281/281` passing (`0` warnings, `0` failures, `0` skipped)** in both configurations.
2. **Visible Interactive 4-Step Debug/Test `Foundation.Automation` 4-Drive Sandbox E2E:**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\run_stage2_sandbox_e2e.ps1 -Version 0.3.0-hf.3 -EvidenceDir <temp_dir>
   ```
   - Runs `Foundation\Dev\tools\run_sandbox_e2e.py` against the isolated 4-drive sandbox (`SANDBOX_C_DRIVE`, `SANDBOX_D_DRIVE`, `SANDBOX_REMOVABLE_DRIVE`, `SANDBOX_DISC_DRIVE` via `%USERPROFILE%\.gemini\.secrets\.env`, asserting `hostDiffCount == 0`), drives a visible interactive `-p:ReStationTestAutomation=true` Setup Wizard (`InstallerAutomationHook` inside `O:\` with profile toggles, details log expand, install, `Finish`, `.ready`/`reSlate.exe --version`, uninstall & `0` host diff), drives visible SDL3 MediaGate & live `RunHostLoop` F1 Drawer / F3 HUD button interactions + `settings.json` persistence (`DebugAutomationHook` `30s` STA readiness wait + `15 Hz` telemetry push + `DebugAutomationDiagnostics` `pad:`/`key:`/`audio:wav:`/`mem:watch:` diagnostics + `cpuMs`/`ovHash` + `SDL3.dll`), optionally copies live PNGs to `-EvidenceDir`, and verifies pure `Release` rejects `--automation-port` with `ExitCode = 2` on both `reSlate.Player.exe` and `reSlate-Setup`.
3. **Single-Pass Release Build (`Dev\scripts\build_release.ps1` + `Dev\scripts\package_helpers.ps1`):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\build_release.ps1 -Version 0.3.0-hf.3
   ```
   Emits `Builds\v0.3.0-hf.3\` (`.ready` marker, `app\LICENSE`, `engine.manifest.json`, `reEngine.Host.dll`, zero `data\rom\`, **zero `AGENTS.md`** inside `Builds\`/`app\`/`payload.zip`, **zero forbidden `Installers\` root folder**), compiles `Releases\v0.3.0-hf.3\README.md` from `USER_GUIDE.md` (+ any `USER_GUIDE.*.md`) via [`sanitize_release_docs.ps1`](../scripts/sanitize_release_docs.ps1) (rewriting repo-relative `[label](Dev/...)` before leak filtering + verifying zero unexempt trademarks), packages **`reSlate-v0.3.0-hf.3-win-x64-Portable.zip`** alongside signed **`reSlate-Setup-v0.3.0-hf.3-win-x64.exe`** into NTFS-hardlinked **7-file `Releases\v0.3.0-hf.3\` + `Latest\`** mirrored at `<ProjectsRoot>\Releases\Personal\reStation\` (+ sibling `<ProjectsRoot>\Releases\Personal\reSlate\` junction), runs `audit_installer.ps1 -InstallerPath Releases\v0.3.0-hf.3` (`0` findings) + `Assert-BinaryPurity` (`0` `Foundation.Automation` / `System.Windows.Forms` references in Release Player) + `Assert-InstallerPurity` (`0` `Foundation.Automation` bytes + `ExitCode = 2` on Release Setup), publishes immutable `<ProjectsRoot>\SharedLibs\reSlate\v0.3.0-hf.3\` (keeping `SharedLibs\reStation\v0.1.0..v0.2.8` intact), and snapshots `Archive\reSlate-v0.3.0-hf.3-src.7z`.
4. **Public History Hygiene & Beta Distribution (`StreamItAllGit/reSlate`):**
   - Keep `.gitignore` strict on `Archive/`, `Builds/`, `Releases/`, `Dev/docs/*/`, `Dev/data/`, `Dev/logs/`, and runtime databases, keep `git log --all` + `%an <%ae>` free of secrets, workstation paths, and unexempt trademarks (`git-filter-repo` pre-public scrub), and sanitize release docs before pushing or publishing.
   - Distribute via **GitHub Releases** (`gh release create --prerelease` attaching `Setup.exe`, `-Portable.zip`, `.7z`, `SHA256SUMS.txt` on [StreamItAllGit/reSlate](https://github.com/StreamItAllGit/reSlate/releases) — never Google Drive), **Discord Dev Updates** (`active.state -> discordPost` webhook delivery), and optional manual **Notion Public Hub** (`*.notion.site` when published).

---

## 8. Isolated Verification & Log Audit Recipe

1. Run isolated xUnit + installer CLI tests under `APP_ISOLATED_TEST=1` in both `-c Release` and `-c Debug` (verifying `--dry-run`, `--tray-only`, `--verify`, `--doctor`, `--require-backend`, and `--uninstall` containment via `InstallerRuntimeContext` and `AppEnvironment.AssertPathContained` without touching `%LOCALAPPDATA%` or canonical host program paths).
2. Verify offline AOT cold-to-warm cache generation in isolated temp dirs (including recipe-ordered `<container>#<slice>` overlays and `generatedCacheAssemblyName` PE assembly verification) and clean-room `.decomp` provider `D4_CleanSource` checks (`PsxD4CraftsmanshipValidator`: 6 decompiler-tell families, `_01.._NN.c` rejection, `-Wno-everything` ban, real `-Wall -Wextra -Wno-unused-parameter` `0`-warning compile checks, `camelCase` `d4b_lockstep_report.json`).
3. Sweep both log roots (`%LOCALAPPDATA%\Programs\...\logs\` read-only + `Dev\logs\`) with preceding 20–50 lines and write timestamped report to `Dev\docs\reports\`.

---

## 9. Incident Playbooks

| Symptom | Root Diagnosis | Resolution |
| :--- | :--- | :--- |
| **`ExitCode = 6` on `--require-backend vulkan` or `parallelCpu`** | Requested backend is deferred to `v0.3.1` pending shared `reEngine.Host` GPU rendering | Pass `--backend opengl` or `--backend software` in `v0.3.0`, or upgrade once `v0.3.1` ships |
| **`ExitCode = 2` on `--automation-port`** | Pure `Release` binaries strip `Foundation.Automation` and fail closed by design | Use a `-c Debug` or `-p:ReStationTestAutomation=true` build for sandbox UI automation |
| **`PSX_D4_CRAFTSMANSHIP_FAILED` on `.decomp` export** | Exported C files tripped one of the 6 decompiler-tell checks, `_01.._NN.c` fragment naming, `-Wno-everything`, or `-Wall -Wextra -Wno-unused-parameter` warnings | Coalesce logical modules cleanly, replace decompiler patterns, and clear all compiler warnings |
| **`PSX_GHIDRA_BATCH_FAILED` / `MISSING_OFFLINE_TOOLCHAIN`** | `Full` `.decomp` run invoked without valid `toolchain\ghidra` + JDK under `reEngine` | Point `--toolchain-dir` / install `reEngine` Full toolchain; clean-room script generation still succeeds offline without `invokeGhidra=true` |
| **OpenGL immediately reverts to Software with banner** | GPU/VM lacks OpenGL 3.3 Core context | `HostVideoSurface.TrySwitchBackend` automatically preserves Software pipeline; update GPU driver or stay on `Software` |
| **Installer or player fails with path containment error (`ExitCode = 3`)** | Candidate path attempted to escape an allowed root (`AppEnvironment.AssertPathContained`) | Pass strictly contained subpaths inside the configured `--install-dir` / `--data-dir` / isolated root |
| **Exit code `5` during `--jit-bringup`** | `JitBringupRecorder` hit `MaxStalledCyclesWithoutVBlank` spin threshold | Inspect emitted bringup profile JSON (`isPureStaticReady: false`) under `<dataDir>\logs\` or run primary AOT cache |
| **Stranded legacy files linger after upgrade** | `--migrate-legacy` ran without `--confirm-legacy-cleanup` (by safety design) | Re-run with `--confirm-legacy-cleanup` or approve the interactive confirmation prompt |
