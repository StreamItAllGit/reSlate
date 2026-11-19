# reSlate — Operations & Verification Runbook

- **Version:** `v0.2.8` *(shipped baseline under former name `reStation`)* | **Next Target:** `v0.3.0` *(`reSlate` Public-Ready Beta · Track `0.3.x`, Alias-Compatible with `0.2.x` / `0.1.x`)*
- **Maintained By:** `@tessa` (Clarity & Ops) & `@devv` (Packaging & CLI)
- **Canonical Production Boundary:** Zero automated edits or unconfirmed writes to `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\` or any live installation during tests, builds, or audits.

---

## 1. Install Topology (`AllowAutoStart = false`)

`v0.2.8` installed under `engines\reStation\v0.2.8\`; starting in **`v0.3.0`**, `reSlate` installs per-user under `engines\reSlate\v0.3.0\` alongside a required `.ready` marker (`reEngine v0.3.0+`):

```text
%LOCALAPPDATA%\Programs\StreamItAll\reEngine\
├── toolchain\                          (Optional Full profile offline Ghidra + OpenJDK)
└── engines\reSlate\v0.3.0\             (v0.2.8 and earlier: engines\reStation\v<Ver>\)
    ├── reSlate.exe                     (Single-file launcher -> app\reSlate.Player.exe)
    ├── .ready                          (Required for reEngine v0.3.0+ engine discovery)
    ├── settings.example.json           (Clean-room overlay template staged at track root)
    ├── app\                            (reSlate.Player.exe, reSlate.Decomp.dll, reSlate.Compiler.exe, reStation.{Contracts,Core}.dll, Foundation.*.dll, reEngine.{Core,Host}.dll, LICENSE, engine.manifest.json, app.manifest.json — strictly ZERO AGENTS.md & ZERO Release Foundation.Automation.dll)
    ├── data\
    │   ├── config\settings.json        (v0.3.0 canonical settings path; auto-migrates <dataDir>\settings.json)
    │   ├── rom\                        (User-owned BYO-ROM media; preserved across upgrades/uninstalls)
    │   ├── cache\                      (Player-PC local <Game>.Generated.dll AOT cache; auto-evicts stale keys)
    │   ├── hdpacks\                    (v0.3.0 hash-keyed clean-room texture overrides)
    │   ├── saves\                      (Quick-save .rst1 + "RSTX" snapshots)
    │   ├── memcards\                   (Virtual .mcr memory cards; v0.3.0 Slot 1+2 via SIO0 0x81 + BIOS)
    │   └── settings.controls.json      (Global 1:1 baseline profile)
    └── logs\                           (runtime.log, installer.log, crashes\crash_*.json)
```

- **No Background Daemons:** `AllowAutoStart = false` is enforced in [`ReStationInstallerTheme.cs`](../src/reStation.Installer/ReStationInstallerTheme.cs) and [`LauncherArgsHelper.cs`](../src/reStation.Launcher/LauncherArgsHelper.cs); `--autostart` / `--auto-start` flags fail fast cleanly.

---

## 2. CLI Flags & Environment Contract

### Player (`reSlate.exe` / `v0.2.8: reStation.exe`) & Launcher (`reStation.Launcher`)
- `--module <path>` — Load unpacked module directory, `module.recomp.json`, or single-file ZIP `.recomp` archive.
- `--disc <path>` / `--preload-media <path>` — Point to verified `SlateOne` (`R3000A`) disc/EXE media inside allowed root.
- `--data-dir <dir>` / `--config <file>` / `--log-dir <dir>` / `--port <n>` — Explicit runtime data, settings JSON, log directory, and diagnostic port overrides.
- `--reengine-dir <dir>` / `--suite-dir <dir>` / `--profile Lightweight|Full` — Override `reEngine` router location or active install profile.
- `--backend software|opengl|vulkan|parallelCpu` · `--require-backend` (`v0.3.0` `ExitCode = 6`) · `--scale <1..8>` · `--fps-mode original|enhanced` · `--target-fps <hz>` · `--vsync` / `--no-vsync`.
- `--execution-tier staticAotOnly|aotWithJitFallback|aotWithThreadedIr|jitBringup|interpreterOracle` — Select local AOT primary vs. optional JIT/IR diagnostic mode.
- `--jit-bringup` / `--force-zero-rwx` — Enable `JitBringupRecorder` + zero-RWX Threaded-IR diagnostic mode (`ExitCode = 5` on VBlank stall guard).
- `--verify` / `--doctor` (`v0.3.0`) / `--tray-only` / `--open-settings` / `--enable-logs` / `--show-fps` / `--frames <n>` / `--fixture <id>` / `--dump-screenshot <png>` / `--dump-audio <wav>` / `--wait` — Non-interactive verification, tray/background startup (`UiPresentationMode.TrayOrBackground`), UI toggles, and wait flags.

### Compiler (`reSlate.Compiler.exe`)
- `--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json`, `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--aot-recipe`, `--execution-tier`, `--emit-clean-room` (emits `aot-recipe.json` + clean `module.recomp.json` with zero `*.Generated.dll`), `--out-package`, `--compile-dll`, `--compile-generated-dll`, `--data-dir`, `--thin-shell`, `--help`.

### Installer (`reSlate-Setup-v<Ver>-win-x64.exe`)
- `--install-dir <dir>` / `--target-dir <dir>` / `--reengine-dir <dir>` / `--suite-dir <dir>` / `--profile Lightweight|Full` / `--data-dir <dir>` / `--config <file>` / `--log-dir <dir>` / `--migrate-legacy` / `--confirm-legacy-cleanup` / `--uninstall` / `--silent` (`--quiet`) / `--dry-run` / `--verify` / `--tray-only` (`APP_ISOLATED_TEST=1` suppresses host/registry mutations in automated tests).

---

## 3. Config & Settings File Reference

Bundled [`Dev/data/settings.example.json`](../data/settings.example.json) provides the clean-room `v0.2.8` template persisted to `<dataDir>\settings.json`:
- **Suite & Decomp Metadata (`Standard 18`):** `installProfile`, `reEngineInstallDir`, `suiteInstallDir`, `umbrellaSubpath`, `decompSchemaVersion`, `goalDecompTier`, `executionTierMode`.
- **Audio/Video & Framing:** `backendId` (`"software"` / `"opengl"`), `resolutionScale`, `isWidescreen16By9` (`false` default for `4:3` framing; unstretched Hor+ `16:9` when enabled), `frameRateMode`, `targetFps`, `isVsyncEnabled`, `isPgxpEnabled`, `isFpsOverlayEnabled`.
- **Input (`Schema A` & Per-Game Options):** `inputProfileMode`, `controlMode`, `modernActionBindings` (`Dictionary<string, int[<=3]>` via `ModernBindingsJsonConverter` with legacy scalar read support), `modernControllerBindings` (`int[<=3]` codes `2001..2032`), `metadataJson` (`Dictionary<string, string>` per-game `ControlOptions`), legacy `controllerDpadMode`/`isometricMoveMode` compatibility fields, and `jsonExtensionData` passthrough.

---

## 4. Ports, Locks & Sister-App Coexistence

- **Zero Network Ports:** `reStation` opens zero listening TCP/UDP sockets during gameplay, compilation, or `.decomp` execution.
- **Process Supervision (`--wait`):** [`LauncherArgsHelper.AttachJobGuard`](../src/reStation.Launcher/LauncherArgsHelper.cs) assigns a Windows `KILL_ON_JOB_CLOSE` Job Object so closing the caller/router terminates supervised `reStation.exe` sessions cleanly with exact exit code propagation (`0` clean, `5` bringup VBlank stall guard, `1` fatal/containment rejection).

---

## 5. Logs & Crash Bundles (Two-Tier Sweep)

1. **Production (`Read-Only` during Audits):** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reStation\v0.2.8\logs\` (`runtime.log`, `crashes\`).
2. **Development (`RS-06`):** `Dev\logs\` (resolved automatically via [`ReStationLogger.TryResolveDevLogsDirectory`](../src/reStation.Core/Core/ReStationLogger.cs#L116-L132) inside source checkouts; excluded by root `.gitignore`).

Every long-running loop emits `<= 15s` structured telemetry heartbeats (`frame`, `fps`, `backend`, `subTicks`, `vramOps`, `spuVoices`, `status`). Crash bundles write `crashes\crash_*.json` plus preceding log tail context.

---

## 6. Backup, Restore & Legacy Migration (`RS-05`)

- **Source Archive (`Archive\`):** Before history rewrites or release tagging, create a compressed backup via `7z.exe a -mx=9 Archive\pre-scrub-v0.2.8.7z ...` (excluding gitignored caches/logs/`bin`/`obj`).
- **Opt-In Legacy `v0.1.1` Migration:** [`ReStationLegacyMigrationHelper`](../src/reStation.Installer/ReStationLegacyMigrationHelper.cs) detects stranded `v0.1.1` installs (`app\data\` + `logs\crashes\crash_*.json`), copies `settings.json` and preserves `data\rom\` into the `v0.2.8` `data\` directory **only** when `--migrate-legacy` is passed, and deletes non-ROM stranded artifacts **only** after explicit interactive confirmation or `--confirm-legacy-cleanup`.

---

## 7. Build, Sanitization & 4-Channel Beta Release Procedure (`Standard 02 §4`, `11`, `28`, `32`, `36`)

1. **Zero-Warning Compile & Test Suite (`250 / 400 / 600` Code-Only SLOC Gate):**
   ```powershell
   $env:APP_ISOLATED_TEST = "1"
   dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true --no-restore
   ```
   Verify all non-exempt production files follow `Standard 32` (`<= 250` code-only SLOC preferred, `400` soft review, `600` hard cap excluding comments/whitespace; tests exempt; `@loc-allow` justified) and **`100%` passing (`0` warnings, `0` failures, `0` skipped)**.
2. **Two-Build Debug/Test `Foundation.Automation` 4-Drive Sandbox E2E (`Standard 02 §4` — Required Before Every `v0.3.0+` Release):**
   - Build and install a `-test.*` (`#if DEBUG || RESTATION_TEST_AUTOMATION`) package inside the isolated 4-drive sandbox (`SANDBOX_C_DRIVE`, `SANDBOX_D_DRIVE`, `SANDBOX_REMOVABLE_DRIVE`, `SANDBOX_DISC_DRIVE` via `%USERPROFILE%\.gemini\.secrets\.env`) and execute `Foundation.Automation` (`--automation-port 0` `127.0.0.1` Bearer/Origin-guarded Virtual Pointer, Virtual Keyboard & composited `IRenderSurfaceSnapshotProvider` PNG snapshots, `hostDiffCount == 0`).
   - Verify the final shipped `Release` binary contains **zero** `Foundation.Automation.dll` binaries or PE references and fails closed (`ExitCode = 2`) if `--automation-port` is passed to `Release`.
3. **Single-Pass Release Build (`Dev\scripts\build_release.ps1`):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\build_release.ps1 -Version 0.2.8
   ```
   Emits `Builds\v<Ver>\` (including `.ready` marker at `v0.3.0+`, `app\LICENSE`, `engine.manifest.json`, zero `data\rom\`, **zero `AGENTS.md`** inside `Builds\`/`app\`/`payload.zip`, **zero forbidden `Installers\` root folder**), compiles `Releases\v<Ver>\README.md` from `USER_GUIDE.md` via [`sanitize_release_docs.ps1`](../scripts/sanitize_release_docs.ps1) (rewriting repo-relative `[label](Dev/...)` before leak filtering + verifying zero unexempt trademarks), packages **`reSlate-v<Ver>-win-x64-Portable.zip`** alongside signed **`reSlate-Setup-v<Ver>-win-x64.exe`** into NTFS-hardlinked **7-file `Releases\`** mirrored at `D:\Projects\Releases\Personal\reStation\` (+ `reSlate` junction), runs `audit_installer.ps1 -InstallerPath Releases\v<Ver>` (`0` findings) + `Assert-BinaryPurity` (`0` `Foundation.Automation` references in Release), publishes immutable `SharedLibs\reSlate\v<Ver>\` (keeping `SharedLibs\reStation\v0.1.0..v0.2.8` intact), and snapshots `Archive\reSlate-v<Ver>-src.7z`.
4. **Public History Hygiene & 4-Channel Beta Distribution (`requireHumanReviewBeforePush: true`):**
   - Before flipping `isPublicRepo` from `false` to `true` at `v0.3.0`, archive `Archive\pre-scrub-v0.3.0.7z`, scrub `git log --all` + `%an <%ae>` + `git grep` via `git-filter-repo` (verifying `0` `ghidra_psx_ldr`, vendor SDK headers, `*.Generated.dll`, retail ROMs, workstation paths, unexempt trademarks, usernames, or live Notion UUIDs), and obtain explicit human confirmation (`ask_question`) BEFORE `git push` or `gh release create` (`StreamItAllGit/reSlate`).
   - Ship across **4 Release Channels**: (1) **GitHub Releases** (`gh release create --prerelease` attaching `Setup.exe`, `-Portable.zip`, `Latest.7z`, `SHA256SUMS.txt` until `v1.0.0` Gold), (2) **Google Drive Release Mirror** (7-file `Releases\v<Ver>\` + `v<Ver>.7z` via `gdrive` CLI), (3) **Discord `/post` Devlog** (`active.state -> discordPost` webhook routing), and (4) **Notion Public Web Page Check** (verify if `reSlate` Notion page is published to `*.notion.site`; publish + link it in `README.md`, `USER_GUIDE.md`/`Releases\README.md`, and `RELEASE_NOTES.md` once `isPublicRepo: true`).

---

## 8. Isolated Verification & Log Audit Recipe (`@inspecter`)

1. Run isolated xUnit + installer CLI tests under `APP_ISOLATED_TEST=1` (verifying `--dry-run`, `--tray-only`, `--verify`, and `--uninstall` containment via `InstallerRuntimeContext` and `AppEnvironment.AssertPathContained` without touching `%LOCALAPPDATA%` or canonical host program paths).
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
