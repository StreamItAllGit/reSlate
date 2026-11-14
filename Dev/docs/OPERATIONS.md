# reStation — Operations & Verification Runbook

**Synced Version:** `0.1.3` (`2026-11-14`)  
**Owner:** `reStation` Engine (`@tessa` / Dev Bible Standard 16)

---

## 1. CLI Automation Switches

### 1.1 `reStation.exe` / `reStation.Player.exe` Flags (`CliOptions`)
`reStation.exe` (`reStation.Launcher`) forwards all arguments to `app\reStation.Player.exe`. Passing `--wait` (using `[MethodImpl(MethodImplOptions.NoInlining)]` Windows `JobObjectGuard` child-tree supervision via `LauncherArgsHelper`) or any verification/CLI switch (`--visible-test`, `--fixture`, `--verify`, `--test-mode`, `--headless`, `--version`, `--help`, `-h`, `/?`) causes the launcher to wait synchronously and propagate the exact exit code (`0` pass, `1` runtime/verify/fixture failure, `2` CLI usage error, `3` fail-closed `STD-03` sandbox violation, `4` disc ingestion failure, reserved `5` `--jit-bringup` non-VBlank watchdog timeout [`v0.2.0`], reserved `6` `--require-backend` hardware/ICD unavailable [`v0.3.0`], and `130` clean `SIGINT` cancellation).

| Flag | Values / Syntax | Operational Behavior |
| :--- | :--- | :--- |
| `--module` | `<module.recomp.json>` | Loads and validates the `.recomp` manifest, entry assembly, and `IGameFramePatch` / `ISubTickFrameHook` / `IOverlayRecompModule`. |
| `--disc` | `<image>` | Extracts and SHA-256-verifies `.iso`, `.bin`+`.cue`, `.chd`, `.7z`, or `.zip` into `<dataDir>\gamedata\`. Highest priority (`--disc` > `--preload-media` > `<module>\data\rom\`). |
| `--preload-media` | `<path>` | Explicit preloaded disc/archive override forwarded by `reEngine` (`--open --wait`); verified fail-closed inside sandbox boundaries (`Exit 3` escape, `Exit 4` missing media). |
| `--wait` | *(switch)* | Holds parent `reStation.exe` launcher cleanly via Windows `JobObjectGuard` until `reStation.Player.exe` exits and propagates child exit code (`v0.1.1`). |
| `--version` | *(switch)* | Prints `reStation v0.1.3` cleanly across both `reStation.exe` and `app\reStation.Player.exe` and exits `0`. |
| `--backend` | `opengl \| software` | Selects `OpenGl33RenderBackend` (`opengl`, off-thread FIFO `Queue.cs`) or deterministic `SoftwareCpuRenderBackend` oracle (`software`). In `--headless`, `opengl` logs `[WARN] [GL_FALLBACK] reason=headless forced=software`. |
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
  - Skipped for informational/verification runs (`--help`, `-h`, `/?`, `help`, `--version`, `-v`, `--verify`, `--dry-run`, `--visible-test`). During `--uninstall` (or `reStation-Uninstall.exe`), `NormalizeUninstallArgs` skips `reEngine` bootstrapping and normalizes base-directory `--install-dir` / `--target-dir` arguments (`<root>` or `<root>\reEngine`) to `<reEngineRoot>\engines\reStation\v0.1.3` (or sets `RESTATION_INSTALL_DIR` in live GUI mode when omitted), preserves direct leaf paths, and enforces `--test-mode` fail-closed containment (`Exit 3` on escape).
  - **Live Mode:** Extracts embedded `reEngine-Setup.exe` (`v0.1.3`), ensures `reEngine` is installed, computes `<reEngineDir>\engines\reStation\v0.1.3`, and seeds `RESTATION_INSTALL_DIR` when `--install-dir` / `--target-dir` is omitted.
  - **Contained `--test-mode`:** Resolves sandbox root (`--data-dir`, else `--install-dir`/`--target-dir`, else `%TEMP%\restation-setup-sandbox`), extracts/bootstraps `reEngine` inside `<sandboxRoot>\`, rewrites effective args to `--data-dir <sandboxRoot> --install-dir <sandboxRoot>\reEngine\engines\reStation\v0.1.3`, and deletes temporary `_bootstrap\` files on exit.

### 3.2 Packaging & Audit Pipeline (`Dev\scripts\build_release.ps1` + `sanitize_release_docs.ps1`)
```powershell
# Single-pass Release Packager (LOC gate, build/test, --help/--version parity, sanitize_release_docs.ps1,
# Builds\v0.1.3\ (with app\LICENSE, zero data\rom\), Authenticode sign, Installers\, <INSTALLERS_ROOT>\, audit_installer.ps1, max-2 Releases\, SharedLibs\, Archive\)
powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\build_release.ps1 -Version 0.1.3
```

To verify installer compliance standalone via `audit_installer.ps1`:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File "$env:USERPROFILE\.gemini\config\skills\release-packaging\scripts\audit_installer.ps1" `
  -InstallerPath "Installers" `
  -StagingDir "Builds\v0.1.3" `
  -ExpectedVersion "0.1.3"
```
Checks enforced by `audit_installer.ps1` & `sanitize_release_docs.ps1`:
1. `Installers\` holds strictly versioned `.exe` files + UTF-8 sanitized `RELEASE_NOTES.md` (historical installers retained per Standard 03; `<INSTALLERS_ROOT>\` holds latest `reStation-Setup-v0.1.3-win-x64.exe`).
2. Staging dir (`Builds\v0.1.3\`) contains `app\LICENSE` (`MPL-2.0`), zero engine `data\rom\` directory, and zero `.pdb`, `.db`, `.sqlite`, `.sqlite3`, `.log`, `.env`, `oauth_creds*.json`, or `*secret*.json` files; `Releases\` markdown docs contain zero `file:///`, agent-handle, internal-path, or UTF-8 mojibake leaks.
3. All `.exe` binaries carry a valid (or intact self-signed) Authenticode signature (`CertThumbprint` `A5414BDD36F7F113C7B2356F189B7A43F69C35E6`).
4. ASCII and UTF-16LE binary scans confirm zero leaked host PII (`<USER_HOME_PREFIX>\...`, `<PROJECTS_ROOT>\`, `<PROGRAMS_ROOT>\`) or secret/token literals.

---

## 4. 3-Tier Log Paths, `<= 15s` Heartbeats & `CrashBundleService` Diagnostics

### 4.1 3-Tier Log Sweep Roots (Standard 15)
Always inspect all three roots (with `20–50` lines of preceding context) before diagnosing runtime issues:
1. **Production (Read-Only during Audits):** `<PROGRAMS_ROOT>\reEngine\engines\reStation\v0.1.3\logs\` (plus `data\logs\` if deployed with sidecar data).
2. **Development:** `Dev\logs\`
3. **Sandbox:** `Sandbox\logs\` (`runtime.log`, `screenshots\`, `fixtures\`, `crashes\`).

### 4.2 `ReStationLogger` & `<= 15s` Telemetry Heartbeats
- **Log Sink & Standard 15 Format:** `ReStationLogger` wraps `Foundation.Core.CappedSharedLogSink` (`runtime.log`, `4 MiB` max cap) plus an always-active `200`-line in-memory circular buffer (`GetRecentLines()`), stamping every line as `yyyy-MM-ddTHH:mm:ss.fffZ [v0.1.3] [PID:<pid>/T:<tid:D2>]`.
- **Heartbeat Cadence:** `HeartbeatLogger` (`Foundation.Core`) emits a single-prefix `[HEARTBEAT]` line every `<= 15s` (plus forced frame `1` / `--verify` / `FixtureRunnerHost` ticks with positive denominators) formatted via `PsxTelemetrySnapshot.ToHeartbeatLine()`:
  ```text
  2026-11-14T03:59:00.282Z [v0.1.1] [PID:54352/T:01] [INFO] [HEARTBEAT] reStation: 1/180 (0.6%) | 2.4s | frame=1 fps=13.3 p99Ms=75.03 cpuMs=66.73 gpuMs=3.99 waitMs=0.88 resolutionScale=4 backend=opengl sectorsPerSec=0.0 mdecMbPerSec=0 activeSpuVoices=0 xaDrops=0 audioFillPct=0.0 audioUnderruns=0
  ```
- **Healthy Parity Baselines:** `xaDrops = 0`, `audioUnderruns = 0`, `sectorsPerSec` near `150.0` during `2x` CD-ROM streaming (`75.0` at `1x`), `p99Ms` within frame budget, and `5/5` Wave 2 Gates `0.1.1-A..E` verified `PASS` ([`0.1.1.20261114-040030.-.sandbox-verify.md`](Dev/docs/reports/archive/0.1.1.20261114-040030.-.sandbox-verify.md)).

### 4.3 `CrashBundleService` Diagnostics
`Program.cs` registers `CrashBundleService.RegisterGlobalHandlers(...)` (`AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`) and explicitly calls `TryWriteCrashBundle(...)` on fatal host exceptions and emulation-thread faults (`ReStationHost.RunFrame()`).
- **Output Path:** `<logDir>\crashes\crash_restation_<yyyyMMdd_HHmmss_fff>.json`
- **Schema (`CrashBundlePayload`, `schemaVersion: 1`):** Captures `appId`, `version`, `crashedAtUtc`, `pid`, `osDescription`, `frameworkDescription`, `workingSetBytes`, `exceptionType`, `exceptionMessage`, `stackTrace`, `innerException`, and up to the last `100` lines of `ReStationLogger.GetRecentLines()`.

---

## 5. Immutable `SharedLibs\reStation\v0.1.3\` SDK Consumption Rules

`Dev\scripts\build_release.ps1` preserves prior immutable releases (`v0.1.0`, `v0.1.1`, `v0.1.2`) and publishes clean engine SDK drops (recreating `Latest\` cleanly) to:
- **Immutable Version Drop:** `<SHARED_LIBS_ROOT>\reStation\v0.1.3\`
- **Rolling Convenience Pointer:** `<SHARED_LIBS_ROOT>\reStation\Latest\` *(refreshed atomically each release without overwriting `v0.1.0` / `v0.1.1` / `v0.1.2`)*

Published artifacts (indexed in `sdk-manifest.json` with `schemaVersion: 1`, `fileName`, `fileSizeBytes`, `sha256Hex`, plus `engine.manifest.json` declaring `"supportedEngineTracks": ["0.1.x"]`, root `LICENSE`, and root `README.md`):
- `reStation.Contracts.dll` / `.xml` / `.deps.json`
- `reStation.Core.dll` / `.xml` / `.deps.json`
- `reStation.Compiler.exe` / `.dll` / `.deps.json` / `.runtimeconfig.json`
- `Foundation.Core.dll` / `Foundation.Retro.dll`
- `reStation-Setup-v0.1.3-win-x64.exe`
- `LICENSE`, `README.md`, `engine.manifest.json`, `sdk-manifest.json`

### Downstream Rules (Standard 00 §4 & Standard 01 §3.7)
1. **Immutability:** Never modify or overwrite files inside an already-published `<SHARED_LIBS_ROOT>\reStation\v<Ver>\` folder. Existing `<Game>Recomp` modules remain pinned to their validated engine drop.
2. **Type-Identity Reference Pattern:** Compiled `.recomp` modules reference `reStation.Contracts`, `reStation.Core`, `Foundation.Core`, and `Foundation.Retro` with `<Private>false</Private>` (via `-p:ReStationLibDir=...`, `RESTATION_LIB_DIR`, `RESTATION_SHARED_DIR`, or `<module>\lib\`) so `ModuleAssemblyLoader` shares host interface type identity cleanly across `AssemblyLoadContext`.
3. **Child Module Fix Boundary:** Strictly additive, non-breaking fixes uncovered during downstream `<Game>Recomp` runs ship as an immutable `PATCH+1` (`v0.1.3` / `v0.1.(Z+1)`) drop (passing all `reStation` tests with `0` warnings and Gate 2 sign-off); breaking API/ABI changes or large features must be filed in `Dev\docs\reports\` for a dedicated parent `reStation` release.
