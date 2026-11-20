# reSlate — Operations & Verification Runbook

- **Synced Version:** `0.3.1` (`2026-11-20` · Public Beta · `appId: "reslate"` · `engineId: "reSlate"` · Track `0.3.x`, Alias-Compatible with `0.2.x` / `0.1.x`) | **Next Target:** `v0.4.0` *(Pure-Static Overlay Promoter, Analog Rumble, CD-DA, `.m3u` Lid-Swap & `--save-dir`)*
- **Scope:** Install Topology, CLI Flags, `--doctor` Diagnostics, `v0.3.0 -> v0.3.1` Settings Migration, Framework-Dependent `SharedLibs`, Log Audit & Stage 2 Sandbox Verification
- **Canonical Production Boundary:** Zero automated edits or unconfirmed writes to `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\` or any live installation during tests, builds, or audits.

---

## 1. Install Topology (`AllowAutoStart = false`)

Starting in **`v0.3.0`**, `reSlate` installs per-user under `engines\reSlate\v0.3.1\` alongside `.ready` and `schemaVersion: 2` manifests (`suiteId: "reEngine"`, `hostProtocolVersion: "0.5.0"`, `minSuiteVersion: "0.5.0"`):

```text
%LOCALAPPDATA%\Programs\StreamItAll\reEngine\
├── toolchain\                          (Optional Full profile offline Ghidra + OpenJDK)
└── engines\reSlate\v0.3.1\             (v0.2.8 and earlier: engines\reStation\v<Ver>\)
    ├── reSlate.exe                     (Single-file launcher -> app\reSlate.Player.exe)
    ├── .ready                          (Required for reEngine engine discovery)
    ├── engine.manifest.json            (schemaVersion: 2, manifestType: "standalone-app", suiteId: "reEngine", hostProtocolVersion: "0.5.0")
    ├── settings.example.json           (Clean-room v0.3.1 33-key config template)
    ├── app\                            (reSlate.Player.exe, reSlate.Decomp.dll, reSlate.Compiler.exe, reStation.{Contracts,Core}.dll, Foundation.*.dll v0.5.2, reEngine.{Core,Host}.dll v0.5.0, SDL3.dll, 7z.{exe,dll}, LICENSE, LICENSE-7ZIP.txt, engine.manifest.json — strictly ZERO AGENTS.md & ZERO Release Foundation.Automation.dll)
    ├── data\
    │   ├── config\settings.json        (Canonical settings path; auto-migrates <dataDir>\settings.json and isWidescreen16By9)
    │   ├── rom\                        (User-owned BYO-ROM media; preserved across upgrades/uninstalls)
    │   ├── cache\                      (Player-PC local <Game>.Generated.dll AOT cache; auto-evicts stale keys)
    │   ├── hdpacks\                    (Active v0.3.1 path-contained PNG texture override folder; ExitCode=3 on escape/junction)
    │   ├── saves\                      (Quick-save .rst1 + "RSTX" snapshots incl. "MCD2")
    │   ├── memcards\                   (Canonical Slot 1+2 .mcr files via SIO0 0x81 + BIOS B0:0x4E-0x50 _new_card)
    │   └── settings.controls.json      (Global 1:1 baseline profile)
    └── logs\                           (runtime.log, installer.log, crashes\crash_*.json)
```

- **No Background Daemons:** `AllowAutoStart = false` is enforced in [`ReStationInstallerTheme.cs`](../src/reStation.Installer/ReStationInstallerTheme.cs), [`installer.manifest.json`](../src/reStation.Installer/installer.manifest.json), and [`LauncherArgsHelper.cs`](../src/reStation.Launcher/LauncherArgsHelper.cs); `--autostart` / `--auto-start` flags are stripped/rejected.

---

## 2. CLI Flags, `--doctor` Diagnostics & Exit Codes

### Player (`reSlate.exe` / `app\reSlate.Player.exe`) & Launcher ([`CliOptions.cs`](../src/reStation.Core/Core/CliOptions.cs), [`LauncherArgsHelper.cs`](../src/reStation.Launcher/LauncherArgsHelper.cs))
- **Media & Execution:** `--module <path>`, `--disc <path>`, `--preload-media <path>`, `--execution-tier staticAotOnly|aotWithJitFallback|aotWithThreadedIr|jitBringup|interpreterOracle` (`PsxInterpreter` quarantined strictly to `interpreterOracle`), `--jit-bringup`, `--force-zero-rwx`, `--frames <n>`.
- **Video & Enhancement Flags:** `--backend opengl|vulkan|parallelCpu|software` (aliases `gl`, `parallelcpu`, `parallel-cpu`, `parallel_cpu`), `--require-backend <id>`, `--scale 1|2|4|8`, `--aspect-mode <nativePar|fourByThree|squareInteger|nativeFit|horPlus16By9>`, `--hd-packs [on|off]`, `--fast-forward [on|off]`, `--fps-mode original|enhanced`, `--target-fps <30..500|unlocked>`, `--vsync [on|off]`, `--no-vsync`.
  - *Note on `--tray-only` + `--require-backend`:* `--tray-only` (or `UiPresentationMode.TrayOrBackground`) always initializes an offscreen `software` surface (`HostVideoSurface.CreateOffscreen`); pairing `--tray-only` with `--require-backend opengl`, `vulkan`, or `parallelCpu` fails closed with **`ExitCode = 6`** (only `--require-backend software` succeeds under `--tray-only`). Conflicting `--backend` vs `--require-backend` values also fail closed with **`ExitCode = 6`**.
  - *Note on Overscan & FF Multiplier:* `overscanCropPx` (`0px/8px/16px` F1 cycle; `0..32px` per-edge clamp) and `fastForwardMultiplier` (`2x/3x/4x` F1 pills; `2.0..4.0` clamp) are configured in the `F1` drawer or `data\config\settings.json` (there are no `--overscan` or `--ff-mult` CLI flags).
- **Overrides & Diagnostics:** `--data-dir <dir>`, `--config <file>` (default `config/settings.json`), `--log-dir <dir>`, `--save-dir <dir>` (accepted; router hook completes in `v0.4.0`), `--fixture <path>`, `--port <1..65535>`, `--reengine-dir <dir>`, `--suite-dir <dir>`, `--profile <json>`, `--dump-screenshot <png>`, `--dump-audio <wav>`, `--tray-only`, `--dry-run`, `--verify`, `--open-settings`, `--enable-logs`, `--show-fps`, `--doctor`, `--wait`, `--version`, `--help`.
- **Debug/Test-Only Automation:** `--automation-port <0..65535>` / `--automation-token <tok>` (`#if DEBUG || RESTATION_TEST_AUTOMATION` only; pure `Release` builds omit `Foundation.Automation` and exit **`2`** immediately).
- **`--doctor` Diagnostics Output ([`Program.MediaGate.cs`](../src/reStation.Player/Program.MediaGate.cs#L199-L242)):** Emits structured line-by-line plain-text diagnostic records (NOT JSON) to both stdout and `DOCTOR` log entries (`reSlate Doctor v0.3.1`, `Containment`, `Paths`, `Execution`, `Host`, and `BackendProbe [<Kind> (<id>)]: available=<bool> diag="<msg>"` for `OpenGL`, `Vulkan`, `DirectX12`, `ParallelCpu`, and `Software` via `HostBackendProbe.ProbeAll()`).

### Compiler (`reSlate.Compiler.exe` / `dotnet reSlate.Compiler.dll`)
- `--disc`, `--gamedata-dir`, `--exe-path`, `--decomp-dir`, `--functions-json` (filters `"program"` entries when tagged), `--out-dir`, `--module-id`, `--serial`, `--title`, `--version`, `--aot-recipe` (consumes optional $O(1)$ `containerOffsetBytes` and per-EXE `entryAddresses`), `--execution-tier`, `--emit-clean-room`, `--out-package`, `--compile-dll`, `--compile-generated-dll`, `--data-dir`, `--thin-shell`, `--version`, `--help`.

### Installer (`reSlate-Setup-v0.3.1-win-x64.exe`)
- `--profile Lightweight|Full`, `--reengine-dir <dir>`, `--suite-dir <dir>`, `--install-dir <dir>`, `--target-dir <dir>`, `--payload <zip>`, `--data-dir <dir>`, `--config <file>`, `--log-dir <dir>`, `--automation-port <n>`, `--automation-token <tok>` (`ExitCode = 2` in `Release`), `--migrate-legacy`, `--confirm-legacy-cleanup`, `--uninstall`, `--purge-data`, `--silent`/`--quiet`, `--dry-run`, `--verify`, `--tray-only`, `--no-launch`, `--help`.

### Exit Codes ([`Program.cs`](../src/reStation.Player/Program.cs), [`CliOptions.cs`](../src/reStation.Core/Core/CliOptions.cs))
`0` Success · `1` Fatal runtime error / `--verify` failed · `2` CLI usage error or disallowed Release `--automation-port` · `3` Fail-closed path containment or reparse-point violation (`--data-dir`/`--config`/`--log-dir`, media extraction, or `<dataDir>\hdpacks\`) · `4` Missing disc/media or SHA-256 mismatch · `5` `--jit-bringup` spin watchdog · `6` Unavailable, conflicting, or `--tray-only`-incompatible `--require-backend` · `130` `SIGINT`.

---

## 3. Config, `v0.3.0 -> v0.3.1` Migration & Containment

Bundled [`Dev/data/settings.example.json`](../data/settings.example.json) provides the clean-room `v0.3.1` template (`33` camelCase keys under a scoped single-file backward-compatibility exception documented in [`ARCHITECTURE.md`](ARCHITECTURE.md#L135), `appId: "reslate"`, `umbrellaSubpath: "StreamItAll\\reEngine\\engines\\reSlate\\v0.3.1"`):
- **`v0.3.0 -> v0.3.1` Automatic Migration ([`RecompSettings.cs`](../src/reStation.Core/Core/RecompSettings.cs#L94-L104)):**
  - `SettingsMigrationHelper.MigrateForConfigPathIfNeeded` first migrates legacy `<dataDir>\settings.json` non-destructively to `<dataDir>\config\settings.json` (retaining `.bak`).
  - On `RecompSettings.Load()` / `Clamp()`, if `aspectRatioMode` is absent or empty, `isWidescreen16By9 == true` automatically migrates to `aspectRatioMode = "horPlus16By9"`, while `isWidescreen16By9 == false` maps to `"nativePar"`. Once set, `isWidescreen16By9` stays synchronized (`aspectRatioMode == "horPlus16By9"`).
- **New `v0.3.1` Keys & Clamps:** `backendId` (`opengl|vulkan|parallelCpu|software`), `aspectRatioMode` (`nativePar`), `overscanCropPx` (`{ top: 0, bottom: 0, left: 0, right: 0 }`, clamped `0..32` in JSON; `F1` cycles `0/8/16px`), `isHdTexturePackEnabled` (`false`), `isFastForwardEnabled` (`false`), `fastForwardMultiplier` (`2.0`, clamped `2.0..4.0`).
- **Clean-Room `<dataDir>\hdpacks\` Containment (`ExitCode = 3`):** [`HdTexturePackStore`](../src/reStation.Core/Enhancement/HdTexturePackStore.cs) + `HdPackResolver` reject `..` traversal (`../escape.png`), rooted paths (`C:\...`), and Windows junctions/reparse points with `InvalidOperationException` ("Fail-Closed Path Containment Violation"), exiting `3` cleanly at startup or disabling HD packs with an inline warning banner if toggled at runtime. Canonical `1x` VRAM stays untouched.

---

## 4. Ports, Locks & Two-Tier Logs

- **Zero Network Ports in Release:** Pure `Release` Player, Setup, and `SharedLibs` binaries strip `Foundation.Automation` completely (`ExitCode = 2` on `--automation-port`); `--wait` binds a `KILL_ON_JOB_CLOSE` Windows Job Object (`LauncherArgsHelper.AttachJobGuard`).
- **Two-Tier Log Audit:** Sweep (1) Production `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v0.3.1\logs\` (**read-only** during audits) and (2) Development `Dev\logs\` (`runtime.log` `<= 15s` heartbeats, `[VK_FALLBACK]` / `GL_FALLBACK` / `CONTAINMENT`, and `crashes\crash_*.json` with preceding 20–50 lines).

---

## 5. Build, Framework-Dependent `SharedLibs`, Stage 2 Sandbox E2E & CI

1. **Dual-Config Zero-Warning Unit Suite (`310/310` Passing):**
   ```powershell
   $env:APP_ISOLATED_TEST = "1"
   dotnet test Dev\src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true --disable-build-servers -p:UseSharedCompilation=false
   dotnet test Dev\src\reStation.slnx -c Debug -p:TreatWarningsAsErrors=true --disable-build-servers -p:UseSharedCompilation=false
   ```
2. **Single-Pass Release Packaging & Framework-Dependent `SharedLibs\reSlate\v0.3.1\` ([`build_release.ps1`](../scripts/build_release.ps1), [`package_helpers.ps1`](../scripts/package_helpers.ps1)):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\build_release.ps1 -Version 0.3.1
   ```
   - Enforces the `<= 400` code-only SLOC gate (`Dev\src\` non-test + `Dev\scripts\`), builds & tests `Release`, stages `Builds\v0.3.1\` (zero `AGENTS.md`, zero `Foundation.Automation`/`WinForms`), signs EXEs, creates 7-file NTFS-hardlinked `Releases\v0.3.1\` + `Latest\` + `-Portable.zip`, runs `audit_installer.ps1`, snapshots `Archive\reSlate-v0.3.1-src.7z`, and executes `Publish-SharedLibsSdk`:
   - **`Publish-SharedLibsSdk` Dual EXE + DLL Verification:** Publishes `reSlate.Compiler` (`net8.0`, `--no-self-contained`, inheriting `reEngine.Host.dll`) and `reSlate.Player` (`net8.0-windows`, `--no-self-contained`) into `<ProjectsRoot>\SharedLibs\reSlate\v0.3.1\` + `Latest\`, asserts `runtimeconfig.json` is framework-dependent (zero `includedFrameworks`, resolving `hostpolicy.dll` error `0x80008083`), verifies zero `Foundation.Automation`/`System.Windows.Forms`/`Foundation.UI` bytes/refs, and executes live `reSlate.Compiler.exe --help`, `dotnet reSlate.Compiler.dll --help`, and `reSlate.Player.exe --version` smoke tests.
3. **4-Drive Stage 2 Sandbox E2E ([`run_stage2_sandbox_e2e.ps1`](../scripts/run_stage2_sandbox_e2e.ps1)):**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Dev\scripts\run_stage2_sandbox_e2e.ps1 -Version 0.3.1 -EvidenceDir <temp_dir>
   ```
   - Runs `Foundation\Dev\tools\run_sandbox_e2e.py` (`hostDiffCount == 0`), passes `"-t:Rebuild,Publish"` when building `-p:ReStationTestAutomation=true` Setup/Player test binaries (preventing stale incremental automation builds) while preserving Windows OS known-folder env vars (`SystemDrive`, `ProgramData`, `ProgramFiles`, `ALLUSERSPROFILE`, `PUBLIC`) inside `stage2_e2e_helpers.ps1`, drives visible Setup Wizard (`O:\`), drives visible SDL3 MediaGate & `RunHostLoop` Step `[3B/4]` across all 4 backend pills (`ParallelCpu <-> OpenGL` swap), `HD Texture Packs`, `Aspect Ratio`, `Overscan Crop`, `Fast-Forward` (`3x`), `Cutscene Skip`, `F3` HUD, verifies `settings.json` persistence, runs `Test-Stage2SharedLibsCompilerSmoke` (`EXE --help` + `dotnet DLL --help`), and verifies pure `Release` `ExitCode = 2`.
4. **GitHub CI Release Workflow (`.github/workflows/release.yml`):** On `v*` tag push or `workflow_dispatch`, runs `build_release.ps1 -SkipSigning -SkipSourceArchive` under `APP_ISOLATED_TEST=1` and submits unsigned Setup artifacts for SignPath signing when configured.

---

## 6. Incident Playbooks

| Symptom | Root Diagnosis | Resolution |
| :--- | :--- | :--- |
| **`ExitCode = 6` on `--require-backend`** | Requested GPU/Vulkan ICD unavailable, `--backend` conflicts with `--require-backend`, or `--tray-only` was combined with `opengl`/`vulkan`/`parallelCpu` | Run `reSlate.exe --doctor` to inspect `BackendProbe` diagnostics; remove `--tray-only` for GPU backends or use `--require-backend software` offscreen |
| **`ExitCode = 3` (`Containment Violation`)** | Path in CLI flags, media archive, or `<dataDir>\hdpacks\hdpack.manifest.json` escaped allowed root or hit a junction/reparse point | Replace `..`/rooted paths in `hdpack.manifest.json` with clean relative paths inside `hdpacks\` and remove directory junctions |
| **`0x80008083` (`hostpolicy.dll` missing) in `SharedLibs`** | Stale single-file or self-contained `.runtimeconfig.json` staged beside `reSlate.Compiler.dll` | Re-run `build_release.ps1 -Version 0.3.1` (`Publish-SharedLibsSdk` enforces `--no-self-contained` + live EXE & `dotnet DLL --help` smoke check) |
| **`ExitCode = 2` on `--automation-port`** | Pure `Release` binaries strip `Foundation.Automation` and fail closed by design | Use a `-c Debug` or `-p:ReStationTestAutomation=true` build (`-t:Rebuild,Publish`) for UI automation |
| **`[VK_FALLBACK]` / `GL_FALLBACK` banner on launch** | Host GPU driver/ICD lacks Vulkan or OpenGL 3.3 Core support | `HostVideoSurface` safely falls back to `Software CPU`; switch to `Parallel CPU` or update GPU drivers |
| **`PSX_D4_CRAFTSMANSHIP_FAILED` on `.decomp` export** | Exported C files tripped one of the 6 decompiler-tell checks, `_01.._NN.c` fragment naming, `-Wno-everything`, or `-Wall -Wextra -Wno-unused-parameter` warnings | Coalesce logical modules cleanly, replace decompiler patterns, and clear all compiler warnings |
| **Exit code `5` during `--jit-bringup`** | `JitBringupRecorder` hit `MaxStalledCyclesWithoutVBlank` spin threshold | Inspect emitted bringup profile JSON (`isPureStaticReady: false`) under `<dataDir>\logs\` or run primary AOT cache |
