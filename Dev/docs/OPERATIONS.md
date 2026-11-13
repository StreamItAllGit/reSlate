# reStation — Operations & Verification Runbook

**Synced Version:** `0.1.0` (`2026-11-13`)

---

## 1. Build, Test & Sandbox Verification
- **Build & Unit/Parity Tests:** Run `dotnet build Dev\src\reStation.slnx -c Release -warnaserror` and `dotnet test Dev\src\reStation.slnx -c Release --no-build`.
- All runtime tests execute inside `Sandbox\` (`app\`, `data\`, `logs\`) with `--test-mode`.
- **Headless CI Runs (Default):** Run `reStation.exe --test-mode --headless --frames 240 --module ... --data-dir Sandbox\data --log-dir Sandbox\logs` (or `--fixture <spec.json>` for deterministic save-state scene checks) and inspect generated `Sandbox\logs\screenshots\*.png`, `*.wav`, and `runtime.log` (`<= 15s` heartbeats, `cpuMs`/`gpuMs`/`waitMs`, `xaDrops`, `activeSpuVoices`, `sectorsPerSec`).
- **Foreground Showcase (`--visible-test`):** When live visual testing is requested, launch with `--test-mode --visible-test` (opens 1x foreground showcase window and exits cleanly after ~3–4 seconds with 0 prompts).
- **Zero Auto-Start Installer Rule:** `reStation.Installer` inherits `AllowAutoStart = false` from `Foundation.Installer` so Startup shortcuts are never created.
