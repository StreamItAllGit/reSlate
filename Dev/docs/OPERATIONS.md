# reStation — Operations & Verification Runbook

**Synced Version:** `0.1.0` (`2026-11-13`)  
**Owner:** `reStation` Engine (`@tessa` / Dev Bible Standard 16)

---

## 1. CLI Automation Switches

### 1.1 `reStation.exe` / `reStation.Player.exe` Flags (`CliOptions`)
`reStation.exe` (`reStation.Launcher`) forwards all arguments to `app\reStation.Player.exe`. Passing any verification or CLI flag (`--visible-test`, `--fixture`, `--verify`, `--test-mode`, `--headless`, `--version`, `--help`, `-h`, `/?`) causes the launcher to wait synchronously and propagate the exact exit code (`0` pass, `1` runtime/verify/fixture failure, `2` CLI usage error, `3` fail-closed sandbox violation, `4` disc ingestion failure).

| Flag | Values / Syntax | Operational Behavior |
| :--- | :--- | :--- |
| `--module` | `<module.recomp.json>` | Loads and validates the `.recomp` manifest, entry assembly, and `IGameFramePatch`. |
| `--disc` | `<image>` | Extracts and SHA-256-verifies `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, or `.zip` into `<dataDir>\gamedata\`. Overrides `<module>\data\rom\` auto-discovery. |
| `--backend` | `opengl \| software` | Selects `OpenGl33RenderBackend` (`opengl`) or deterministic `SoftwareCpuRenderBackend` oracle (`software`). In-memory override (not persisted). |
| `--scale` | `1 \| 2 \| 4 \| 8` | Overrides internal render resolution multiplier (`1x`–`8x`). |
| `--fps-mode` | `original \| enhanced` | Selects hardware `60.0 Hz` cap (`original`) or configurable `--target-fps` (`enhanced`). |
| `--target-fps` | `0 \| unlocked \| 30..500` | Sets target FPS (`0` / `unlocked` = uncapped). Values `1..29` or `> 500` throw `ArgumentException` (exit `2`). |
| `--vsync` / `--no-vsync` | `[on \| off \| true \| false \| 1 \| 0]` | Enables (`--vsync` / `--vsync on`) or disables (`--vsync off` / `--no-vsync`) VSync. |
| `--frames` | `<n>` (`>= 1`) | Runs exact host frame count before clean shutdown (default in `--headless` with no `gameModule`: `1`). |
| `--fixture` | `<spec.json>` | Runs deterministic `FixtureRunnerHost` (`SoftwareCpuRenderBackend` + `SaveStateFixtureRunner`), writing PNG/WAV artifacts under `<logDir>\fixtures\`. Exits `0` (all pass), `1` (mismatch), or `2` (no specs). |
| `--dump-screenshot` | `<pngPath>` | Saves `1280x720` composited RGBA PNG at final frame (or frame `2` if `--frames` is unset), or sets `--visible-test` output path. |
| `--data-dir` | `<dir>` | Overrides writable data root (also via `RESTATION_DATA_DIR` / `DATA_DIR`). |
| `--config` | `<path>` | Overrides `settings.json` path (also via `RESTATION_CONFIG` / `CONFIG_PATH`). |
| `--log-dir` | `<dir>` | Overrides log directory (also via `RESTATION_LOG_DIR` / `LOG_DIR`). |
| `--test-mode` | *(switch)* | Fail-closed sandbox mode (also via `RESTATION_TEST_MODE` / `TEST_MODE`). Forces `shouldEnableRuntimeLogs = true`, **requires explicit `--data-dir`**, and asserts `ConfigFilePath` + `LogDirectory` stay strictly inside `DataDirectory` (exit `3` otherwise). |
| `--headless` | *(switch)* | Runs without a foreground SDL window (`TargetFps = 0` uncapped, skips SDL audio device creation). |
| `--visible-test` | *(switch)* | Runs `VisibleTestHarness` (`IVisibleTestHarness`): opens foreground `#0E0E10` `1280x720` PS1 showcase for `3000 ms`, writes `<logDir>\screenshots\visible-test.png`, emits forced frame-1 heartbeat, auto-exits `0`. |
| `--verify` | *(switch)* | Verifies `<dataDir>\gamedata\disc_manifest.json` or `<module>\data\rom\` preload availability, emits forced heartbeat, exits `0` or `1`. |
| `--open-settings` | *(switch)* | Opens the `F1` Slide-Over Settings Drawer on frame 1. |
| `--enable-logs` | *(switch)* | Forces `runtime.log` writing for the session without mutating saved `settings.json`. |
| `--show-fps` | *(switch)* | Enables the `F3` telemetry HUD overlay for the session. |
| `--help` / `-h` / `/?` | *(switch)* | Prints `CliOptions.HelpText` and exits `0`. |

### 1.2 `reStation.Compiler.exe` Switches (`RecompCompilerPipeline`)
```powershell
reStation.Compiler.exe (--disc <image> | --gamedata-dir <dir> | --exe-path <exe>) `
  [--decomp-dir <dir>] [--functions-json <file>] `
  --out-dir <dir> --module-id <id> --serial <serialNumber> --title <title> `
  [--version <semver>] [--compile-dll]
```
- Exit codes: `0` success, `1` compile/IO failure, `2` missing/conflicting/unknown flags (`--disc` and `--gamedata-dir` are mutually exclusive).

---

## 2. Fail-Closed `Sandbox\` Execution & Verification Matrix

All automated development, CI, and acceptance tests run inside `Sandbox\` (`app\`, `data\`, `logs\`) with `--test-mode` so production data under `<PROGRAMS_ROOT>\` is never touched.

### 2.1 Containment Guards (`SandboxEnvironment`)
- `CliOptions.CreateSandbox()` throws `InvalidOperationException` (exit `3`) if `--test-mode` is passed without `--data-dir` or if `--config` / `--log-dir` escape the resolved sandbox boundary (`...\Sandbox\` when `--data-dir` resides inside `Sandbox\`, the parent of `...\data`, or `DataDirectory` otherwise).
- `SandboxEnvironment.AssertPathContained` blocks path-traversal (`..`) across ISO-9660 extraction (`DiscVerifierAndExtractor`), `data\rom\` preload discovery (`PreloadRomDetector`), module `entryAssembly` loading (`ModuleAssemblyLoader`), quick-saves (`QuickSaveManager`), `.mcr` cards (`MemCardMcrStore`), and fixture save-states (`FixtureRunnerHost`).

### 2.2 Standard Build, Unit Test & Sandbox Commands
```powershell
# 1. Full Solution Build (/warnaserror) & Unit/Parity Suite (0 warnings, 100% pass)
dotnet build Dev\src\reStation.slnx -c Release /warnaserror
dotnet test  Dev\src\reStation.slnx -c Release --no-build --logger "console;verbosity=minimal"

# 2. Headless Sandbox Run + Forced Heartbeat Verification
.\Sandbox\app\reStation.exe --test-mode --headless --frames 240 --backend software `
  --data-dir .\Sandbox\data --log-dir .\Sandbox\logs

# 3. Deterministic Save-State Fixture Parity Check (PNG + WAV outputs in Sandbox\logs\fixtures\)
.\Sandbox\app\reStation.exe --test-mode --headless --module "<path>\module.recomp.json" `
  --fixture "<path>\fixtures.json" --data-dir .\Sandbox\data --log-dir .\Sandbox\logs

# 4. Foreground Visual Showcase (3s window, writes Sandbox\logs\screenshots\visible-test.png, exits 0)
.\Sandbox\app\reStation.exe --test-mode --visible-test `
  --data-dir .\Sandbox\data --log-dir .\Sandbox\logs
```

---

## 3. `--test-mode` Installer Verification & `audit_installer.ps1`

### 3.1 Zero-Daemon & Embedded Bootstrap Rules (`reStation.Installer`)
- **`AllowAutoStart = false`:** `ReStationInstallerTheme.AllowAutoStart` and `Program.AllowAutoStart` are strictly `false` (Standard 12 / 19). `Program.SanitizeArgs` strips every `--autostart*` and `--auto-start*` CLI token before `ReStationInstallerBootstrap.Prepare` and `StandardInstallerHost.Run`.
- **NoInlining AssemblyResolve:** `reStation.Installer.Program` resolves `Foundation.Core`, `Foundation.Installer`, and `reEngine.Core` from embedded `.dll.gz` streams before `RunInstallerCore` (`MethodImplOptions.NoInlining`) JITs.
- **Side-by-Side `reEngine` Bootstrap (`ReStationInstallerBootstrap`):**
  - Skipped for informational/cleanup invocations (`--help`, `-h`, `/?`, `help`, `--version`, `-v`, `--verify`, `--dry-run`, `--uninstall`, `--visible-test`, or when running as `uninstall.exe`).
  - **Live Mode:** Extracts embedded `reEngine-Setup.exe`, ensures `reEngine` is installed, computes `<reEngineDir>\engines\reStation\v0.1.0`, and seeds `RESTATION_INSTALL_DIR` when `--install-dir` / `--target-dir` is omitted.
  - **Contained `--test-mode`:** Resolves sandbox root (`--data-dir`, else `--install-dir`/`--target-dir`, else `%TEMP%\restation-setup-sandbox`), extracts/bootstraps `reEngine` inside `<sandboxRoot>\`, rewrites effective args to `--data-dir <sandboxRoot> --install-dir <sandboxRoot>\reEngine\engines\reStation\v0.1.0`, and deletes temporary `_bootstrap\` files on exit.

### 3.2 Packaging & Audit Pipeline (`Dev\scripts\build_release.ps1`)
```powershell
# Single-pass Release Packager (LOC gate, build/test, Builds\v0.1.0\, Authenticode sign,
# Installers\, <INSTALLERS_ROOT>\, audit_installer.ps1, 6-file Releases\, SharedLibs\, Archive\)
powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\build_release.ps1
```

To verify installer compliance standalone via `audit_installer.ps1`:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File "$env:USERPROFILE\.gemini\config\skills\release-packaging\scripts\audit_installer.ps1" `
  -InstallerPath "Installers" `
  -StagingDir "Builds\v0.1.0" `
  -ExpectedVersion "0.1.0"
```
Checks enforced by `audit_installer.ps1`:
1. `Installers\` holds strictly the versioned `.exe` + `RELEASE_NOTES.md` (zero sidecar dirs/files) and mirrors byte-for-byte to `<INSTALLERS_ROOT>\reStation-Setup-v0.1.0-win-x64.exe`.
2. Staging dir (`Builds\v0.1.0\`) contains zero `.pdb`, `.db`, `.sqlite`, `.sqlite3`, `.log`, `.env`, `oauth_creds*.json`, or `*secret*.json` files.
3. All `.exe` binaries carry a valid (or intact self-signed) Authenticode signature (`CertThumbprint` `A5414BDD36F7F113C7B2356F189B7A43F69C35E6`).
4. ASCII and UTF-16LE binary scans confirm zero leaked host PII (`<USER_HOME_PREFIX>\...`, `<PROJECTS_ROOT>\`, `<PROGRAMS_ROOT>\`) or secret/token literals.

---

## 4. 3-Tier Log Paths, `<= 15s` Heartbeats & `CrashBundleService` Diagnostics

### 4.1 3-Tier Log Sweep Roots (Standard 15)
Always inspect all three roots (with `20–50` lines of preceding context) before diagnosing runtime issues:
1. **Production (Read-Only during Audits):** `<PROGRAMS_ROOT>\reEngine\engines\reStation\v0.1.0\logs\` (plus `data\logs\` if deployed with sidecar data).
2. **Development:** `Dev\logs\`
3. **Sandbox:** `Sandbox\logs\` (`runtime.log`, `screenshots\`, `fixtures\`, `crashes\`).

### 4.2 `ReStationLogger` & `<= 15s` Telemetry Heartbeats
- **Log Sink:** `ReStationLogger` wraps `Foundation.Core.CappedSharedLogSink` (`runtime.log`, `4 MiB` max cap) plus an always-active `200`-line in-memory circular buffer (`GetRecentLines()`) even when disk logging is toggled off.
- **Heartbeat Cadence:** `HeartbeatLogger` (`Foundation.Core`) emits a `[HEARTBEAT]` line every `<= 15s` (and forces frame `1` / `--verify` emission) formatted via `PsxTelemetrySnapshot.ToHeartbeatLine()`:
  ```text
  [HEARTBEAT] frame=240 fps=60.0 p99Ms=16.72 cpuMs=1.18 gpuMs=2.04 waitMs=13.40 resolutionScale=4 backend=opengl sectorsPerSec=150.0 mdecMbPerSec=24 activeSpuVoices=8 xaDrops=0 audioFillPct=51.4 audioUnderruns=0
  ```
- **Healthy Parity Baselines:** `xaDrops = 0`, `audioUnderruns = 0`, `sectorsPerSec` near `150.0` during `2x` CD-ROM streaming (`75.0` at `1x`), and `p99Ms` within frame budget.

### 4.3 `CrashBundleService` Diagnostics
`Program.cs` registers `CrashBundleService.RegisterGlobalHandlers(...)` (`AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`) and explicitly calls `TryWriteCrashBundle(...)` on fatal host exceptions and emulation-thread faults (`ReStationHost.RunFrame()`).
- **Output Path:** `<logDir>\crashes\crash_restation_<yyyyMMdd_HHmmss_fff>.json`
- **Schema (`CrashBundlePayload`, `schemaVersion: 1`):** Captures `appId`, `version`, `crashedAtUtc`, `pid`, `osDescription`, `frameworkDescription`, `workingSetBytes`, `exceptionType`, `exceptionMessage`, `stackTrace`, `innerException`, and up to the last `100` lines of `ReStationLogger.GetRecentLines()`.

---

## 5. Immutable `SharedLibs\reStation\v0.1.0\` SDK Consumption Rules

`Dev\scripts\build_release.ps1` (`[6/7]`) publishes the engine SDK drop to:
- **Immutable Version Drop:** `<SHARED_LIBS_ROOT>\reStation\v0.1.0\`
- **Rolling Convenience Pointer:** `<SHARED_LIBS_ROOT>\reStation\Latest\`

Published artifacts (indexed in `sdk-manifest.json` with `schemaVersion: 1`, `fileName`, `fileSizeBytes`, `sha256Hex`):
- `reStation.Contracts.dll` / `.xml` / `.deps.json`
- `reStation.Core.dll` / `.xml` / `.deps.json`
- `reStation.Compiler.exe` / `.dll` / `.deps.json` / `.runtimeconfig.json`
- `Foundation.Core.dll` / `Foundation.Retro.dll`
- `reStation-Setup-v0.1.0-win-x64.exe`

### Downstream Rules (Standard 00 §4 & Standard 01 §3.7)
1. **Immutability:** Never modify or overwrite files inside an already-published `<SHARED_LIBS_ROOT>\reStation\v<Ver>\` folder. Existing `<Game>Recomp` modules remain pinned to their validated engine drop.
2. **Type-Identity Reference Pattern:** Compiled `.recomp` modules reference `reStation.Contracts`, `reStation.Core`, `Foundation.Core`, and `Foundation.Retro` with `<Private>false</Private>` (via `-p:ReStationLibDir=...`, `RESTATION_LIB_DIR`, `RESTATION_SHARED_DIR`, or `<module>\lib\`) so `ModuleAssemblyLoader` shares host interface type identity cleanly across `AssemblyLoadContext`.
3. **Child Module Fix Boundary:** Strictly additive, non-breaking fixes uncovered during downstream `<Game>Recomp` runs ship as an immutable `PATCH+1` (`v0.1.1`) drop (passing all `reStation` tests with `0` warnings and Gate 2 sign-off); breaking API/ABI changes or large features must be filed in `Dev\docs\reports\` for a dedicated parent `reStation` release.
