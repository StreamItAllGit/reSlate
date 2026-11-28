# reSlate — Stage 2 E2E Surface Coverage Ledger

- **Current Version:** `0.3.3` (`2026-11-28T08:46:25Z`)
- **Machine-Readable Ledger:** `Dev/docs/tests/e2e_coverage.json` *(gitignored)*
- **Performance History:** `Dev/docs/tests/e2e_perf_history.json` *(gitignored; `v0.3.3` baseline initialized)*
- **Execution Topology:** Consolidated `3`-launch Stage 2 4-Drive Sandbox Grand Tour via [`run_stage2_sandbox_e2e.ps1`](../scripts/run_stage2_sandbox_e2e.ps1) + `exebox-stream` (`http://127.0.0.1:5005`)

## Summary Counts

| Total Inventoried | Verified | Untested / Stale | Failed | Last Random Seed | Overdue Perf Checks | Host Load Band |
| :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| `18` | `18` | `0` | `0` | `20261128` (`5` sampled) | `0` (`v0.3.3` baseline) | `CLEAN` (`20% -> 40%` CPU, `34.6% -> 34.7%` RAM, `2%` GPU, `6350+ MB` free VRAM) |

## Surface Inventory (`v0.3.3`)

### Setup Wizard & 4-Drive Installer Sandbox
| Surface ID | Screen / Mode | Selector / Entrypoint | Channel | Status | Verified Version |
| :--- | :--- | :--- | :--- | :---: | :---: |
| `wizard.window_chrome` | Setup Wizard Main Window | `Wizard_PrimaryButton`, title `reSlate Setup (v0.3.3)` | `Foundation.Automation` + `read_file` PNG | `VERIFIED` | `0.3.3` |
| `wizard.profile_select` | Setup Wizard Profile Radios | `Wizard_ProfileFull`, `Wizard_ProfileLightweight` | `Foundation.Automation` pointer click | `VERIFIED` | `0.3.3` |
| `wizard.details_log` | Setup Wizard Details Log | `Wizard_DetailsToggle` (`SPACE`), `Wizard_LogTextBox` | `Foundation.Automation` keyboard + tree | `VERIFIED` | `0.3.3` |
| `wizard.install_uninstall_roundtrip` | Setup Wizard Install & Clean Uninstall | `Wizard_PrimaryButton` -> `Finish`, `--uninstall --purge-data --silent` | `Foundation.Automation` + sandbox diff | `VERIFIED` | `0.3.3` |
| `installer.sandbox_12pt` | 4-Drive Sandbox 12-Point Suite | `run_sandbox_e2e.py` (`S2-01..S2-12`) | Sandbox CLI + `Test-SandboxHostClean` | `VERIFIED` | `0.3.3` |

### First-Run Media Gate & Cold-Archive Pump
| Surface ID | Screen / Mode | Selector / Entrypoint | Channel | Status | Verified Version |
| :--- | :--- | :--- | :--- | :---: | :---: |
| `mediagate.window_and_badge` | First-Run Media Gate Window | `reSlate v0.3.3` header, `< 5s` interactive window before ingest | `Foundation.Automation` + `read_file` PNG | `VERIFIED` | `0.3.3` |
| `mediagate.browse_and_esc` | Media Gate Browse Controls & Cancel | `Gate_BrowseDisc`, `Gate_BrowseModule`, `Esc` exit `0` | `Foundation.Automation` tree + keyboard | `VERIFIED` | `0.3.3` |

### Live SDL3 Player, F1 Settings Drawer & F3 Telemetry HUD
| Surface ID | Screen / Mode | Selector / Entrypoint | Channel | Status | Verified Version |
| :--- | :--- | :--- | :--- | :---: | :---: |
| `player.live_window_and_close` | Live SDL3 Player RunHostLoop | `reSlate.Player.exe --module ...`, `WM_CLOSE`, `settings.json` | SDL3 window + disk settings assertion | `VERIFIED` | `0.3.3` |
| `player.f1_drawer_shell` | F1 Slide-Over Drawer Shell | `Drawer_TogglePill`, `Drawer_CloseButton`, `Settings v0.3.3` | `Foundation.Automation` + `read_file` PNG | `VERIFIED` | `0.3.3` |
| `player.f3_telemetry_hud` | F3 Telemetry HUD Overlay | `F3` key, `F3Hud` (`v0.3.3 \| [FF 3x] [ENH] FPS 60 \| opengl 2x`) | `Foundation.Automation` + `read_file` PNG | `VERIFIED` | `0.3.3` |
| `player.video_4backends` | F1 > Video > 4 Render Backends | `Video_BackendOpenGl`, `Video_BackendVulkan`, `Video_BackendParallelCpu`, `Video_BackendSoftware` | `Foundation.Automation` pointer click | `VERIFIED` | `0.3.3` |
| `player.video_enhancement_controls` | F1 > Video > Enhancements | `Video_Scale2x`, `Video_TogglePgxp`, `Video_ToggleHdPacks`, `Video_AspectRatio`, `Video_ToggleOverscan` | `Foundation.Automation` + `settings.json` | `VERIFIED` | `0.3.3` |
| `player.tab_controls` | F1 > Controls Tab | `Tab_Controls`, `Controls_SensPlus` | `Foundation.Automation` + `settings.json` | `VERIFIED` | `0.3.3` |
| `player.tab_keybinds` | F1 > Keybinds Tab | `Tab_Keybinds` | `Foundation.Automation` pointer click | `VERIFIED` | `0.3.3` |
| `player.tab_system_qol` | F1 > System Tab & QoL | `Tab_System`, `System_ToggleFastForward`, `System_FastForward3x`, `System_SkipCutscene` | `Foundation.Automation` + `settings.json` | `VERIFIED` | `0.3.3` |
| `player.tab_disc_media` | F1 > Disc Tab | `Tab_DiscMedia`, `Disc_ChangeDisc` | `Foundation.Automation` pointer click | `VERIFIED` | `0.3.3` |

### SharedLibs SDK & Pure Release Security Gates
| Surface ID | Screen / Mode | Selector / Entrypoint | Channel | Status | Verified Version |
| :--- | :--- | :--- | :--- | :---: | :---: |
| `sdk.sharedlibs_compiler_smoke` | SharedLibs Compiler CLI | `reSlate.Compiler.exe --help`, `dotnet reSlate.Compiler.dll --help` | CLI probe | `VERIFIED` | `0.3.3` |
| `release.fail_closed_automation_gate` | Pure Release Automation Guard | `reSlate.Player.exe --automation-port 5199`, `reSlate-Setup-v0.3.3-win-x64.exe --automation-port 5199` | CLI `ExitCode = 2` | `VERIFIED` | `0.3.3` |

## Cross-Run Performance vs. Base (`v0.3.3` Baseline Initialized)

| Metric | Direction | Base (`0.3.3`) | Current (`0.3.3`) | Delta | Host Band | Verdict |
| :--- | :--- | ---: | ---: | ---: | :--- | :--- |
| Full Stage 2 E2E Suite Duration (ms) | lower is better | `65209.2` | `65209.2` | `0.0` (`0.0%`) | `CLEAN` | `BASELINE` |
| 12-Point Sandbox Installer Suite (ms) | lower is better | `10528.0` | `10528.0` | `0.0` (`0.0%`) | `CLEAN` | `BASELINE` |
| `S2-04` CLI `--version` Boot (ms) | lower is better | `275.0` | `275.0` | `0.0` (`0.0%`) | `CLEAN` | `BASELINE` |
| Total Consolidated GUI Launches | target `1–3` | `3` | `3` | `0` | `CLEAN` | `PASS` |
