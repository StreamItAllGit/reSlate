# reSlate — Stage 2 E2E Input & Storyboard Playbook

- **Synced Version:** `0.3.3` (`2026-11-28`)
- **Orchestrator:** [`Dev/scripts/run_stage2_sandbox_e2e.ps1`](../scripts/run_stage2_sandbox_e2e.ps1)
- **Helpers:** [`Dev/scripts/stage2_e2e_helpers.ps1`](../scripts/stage2_e2e_helpers.ps1)
- **Virtual Desktop Stream:** `exebox-stream` (`http://127.0.0.1:5005`) — attach once per launch on isolated `WinSta0\exebox-*` desktops; never launch `Test Viewer.lnk` inside `exebox`.

## Consolidated 3-Launch Grand Tour Storyboard

### Launch 1 — Setup Wizard GUI (`Invoke-WizardGuiSandboxStep`)
- **Entry:** `reSlate-Setup-v0.3.3-win-x64.exe` (`ReStationTestAutomation=true`) in 4-drive sandbox (`O:\Programs\reEngine\engines\reSlate\v0.3.3`, `--no-launch`, `--automation-port`, `--automation-token`).
- **Beat Timing & Controls:**
  1. Wait for `Wizard_PrimaryButton`, settle `650 ms`.
  2. Click `Wizard_ProfileFull` -> hold `650 ms` -> click `Wizard_ProfileLightweight` -> hold `650 ms`.
  3. Press `SPACE` on `Wizard_DetailsToggle` -> settle `300 ms`; fallback pointer click if `Wizard_LogTextBox` is not yet visible -> hold `650 ms`.
  4. Click `Wizard_PrimaryButton` -> poll up to `60 s` until `Wizard_PrimaryButton.text` reaches `Finish` or `Launch` -> hold `800 ms` -> capture `stage2_wizard_live.png`.
  5. Click `Wizard_PrimaryButton` to close cleanly (`ExitCode = 0`), probe `reSlate.exe --version`, run `--uninstall --purge-data --silent`, and assert `Test-SandboxHostClean` (`0` host diff).

### Launch 2 — First-Run Media Gate (`Invoke-PlayerVisibleGuiSteps` `[3A/4]`)
- **Entry:** `reSlate.Player.exe` (`ReStationTestAutomation=true`, `--enable-logs --data-dir ... --backend opengl --automation-port ...`) with empty media state.
- **Beat Timing & Controls:**
  1. Wait for automation `/v1/health`, raise window (`>= 800x500`) on top, attach `exebox-stream`.
  2. Wait for `Gate_BrowseDisc` and `Gate_BrowseModule` (`< 5.0s` interactive window target) -> hold `1000 ms` -> capture `stage2_mediagate_live.png`.
  3. Press `ESC` via `/v1/keyboard` and assert clean `ExitCode = 0`.

### Launch 3 — Live SDL3 Player + F1 Drawer + F3 HUD (`Invoke-PlayerVisibleGuiSteps` `[3B/4]`)
- **Entry:** `reSlate.Player.exe` with `smoke_module\module.recomp.json` + `disc_manifest.json` (`--backend opengl`).
- **Beat Timing & Controls (`650 ms` milestone holds):**
  1. Wait for `Drawer_TogglePill`, hold `650 ms`, click `Drawer_TogglePill` -> wait for `Tab_Video` (`650 ms`).
  2. Press `F3` -> wait for `F3Hud` (`650 ms`).
  3. Verify all 4 render backend buttons visible: `Video_BackendOpenGl`, `Video_BackendVulkan`, `Video_BackendParallelCpu`, `Video_BackendSoftware`.
  4. Execute state-transition sequence (`Click` -> `Wait` -> `650 ms`):
     - `Video_Scale2x` -> `Video_TogglePgxp` -> `Video_ToggleHdPacks` -> `Video_AspectRatio` -> `Video_BackendParallelCpu` -> `Video_BackendOpenGl`
     - `Tab_Controls` -> `Controls_SensPlus`
     - `Tab_Keybinds`
     - `Tab_System` -> `System_ToggleFastForward` -> `System_FastForward3x` -> `System_SkipCutscene`
     - `Tab_DiscMedia` -> `Disc_ChangeDisc` -> return `Tab_Video`
  5. Raise window on top, capture `stage2_player_live.png`, click `Drawer_CloseButton`, wait `Tab_Video` absent + `500 ms`, send `WM_CLOSE`, assert `ExitCode = 0` and verify `data\config\settings.json` persistence on disk.

## Coordinate & Calibration Notes
- Overlay canvas is fixed `1280x720`; `HostContractBridge` projects `DrawerHitBox` rectangles directly onto `/v1/tree`, and `UiOverlayPresenter.MapWindowToOverlayCoords` normalizes `NativeFit` window pointer coordinates cleanly.
- Collapsed pill `v<Version>` badge renders immediately beneath `Drawer_TogglePill` (`y = 52..74`); open-drawer `v<Version>` renders beside `"Settings"` (`x ~ 845..960, y ~ 24`); `F3` HUD prepends `v<Version> | ` at `(14, 12)`.
- When running inside `WinSta0\exebox-*`, never invoke `Test Viewer.lnk` inside the session; capture PNGs via `/v1/snapshot` and inspect via `read_file`.
