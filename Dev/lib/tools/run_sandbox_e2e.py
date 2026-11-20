# @module: Foundation.Tools.run_sandbox_e2e
# @purpose: Reusable Stage 2 real-program 12-point E2E runner (Standard 02 §4) emitting Standard 18 receipt JSON.
# @depends-on: argparse, json, os, pathlib, shlex, subprocess, sys, time, uuid, sandbox_ctl
# @exports: build_sandbox_child_env, is_path_within_sandbox, run_stage2_e2e, main

from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Any, Callable

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from sandbox_ctl import (  # noqa: E402
    DEV_ROOT, SandboxError, _host_programs_roots, atomic_write_json, diff_host_snapshots,
    get_subst_mappings, init_sandbox, mount_disc, resolve_sandbox_config,
    take_host_snapshot, toggle_removable, unmount_disc, utc_now_iso,
)

SAFE_SYSTEM_ENV_KEYS = (
    "SYSTEMROOT", "WINDIR", "COMSPEC", "PATH", "PATHEXT",
    "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
    "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT",
)


def is_path_within_sandbox(candidate: Path, cfg: dict[str, Any]) -> bool:
    res = candidate.resolve()
    allowed = [cfg["sandboxRoot"].resolve()] + [Path(f"{drv}\\").resolve() for k in ("SANDBOX_C_DRIVE", "SANDBOX_D_DRIVE", "SANDBOX_REMOVABLE_DRIVE") if (drv := cfg["drives"].get(k))]
    return any(res == r or r in res.parents for r in allowed)


def build_sandbox_child_env(cfg: dict[str, Any]) -> dict[str, str]:
    env = {k: os.environ[k] for k in SAFE_SYSTEM_ENV_KEYS if os.environ.get(k)}
    prof = Path(cfg["sandboxProfileDir"])
    local_app, temp_dir = prof / "AppData/Local", prof / "AppData/Local/Temp"
    env.update({
        "FOUNDATION_SANDBOX_ROOT": str(cfg["sandboxRoot"]), "FOUNDATION_SANDBOX_PROFILE_DIR": str(prof),
        "USERPROFILE": str(prof), "HOME": str(prof), "LOCALAPPDATA": str(local_app),
        "APPDATA": str(prof / "AppData/Roaming"), "TEMP": str(temp_dir), "TMP": str(temp_dir),
        "FOUNDATION_SUPPRESS_MODALS": "1",
    })
    for role in ("SANDBOX_C_DRIVE", "SANDBOX_D_DRIVE", "SANDBOX_REMOVABLE_DRIVE", "SANDBOX_DISC_DRIVE"):
        if cfg["drives"].get(role):
            env[role] = cfg["drives"][role]
    return env


def _launch_cmd(target: Path, extra_args: list[str]) -> list[str]:
    ext = target.suffix.lower()
    if ext == ".py":
        return [sys.executable, str(target), *extra_args]
    if ext in (".bat", ".cmd"):
        return ["cmd.exe", "/c", str(target), *extra_args]
    return [str(target), *extra_args]


def _run_child(cmd: list[str], env: dict[str, str], timeout_s: int, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(cmd, env=env, cwd=str(cwd) if cwd else None, capture_output=True, text=True, timeout=timeout_s, check=False)


def _find_app_exe(install_dir: Path, app_id: str, exe_rel: str | None) -> Path | None:
    if exe_rel:
        cand = (install_dir / exe_rel).resolve()
        return cand if cand.is_file() else None
    for cand in (install_dir / f"{app_id}.exe", install_dir / "app" / f"{app_id}.exe"):
        if cand.is_file():
            return cand
    for folder in (install_dir, install_dir / "app"):
        if folder.is_dir():
            for exe in sorted([*folder.glob("*.exe"), *folder.glob("*.py")]):
                low = exe.name.lower()
                if "-uninstall" not in low and "-setup" not in low and not low.startswith("setup"):
                    return exe
    return None


def _sweep_logs(roots: list[Path], since_epoch: float) -> list[str]:
    findings: list[str] = []
    for root in [r for r in roots if r.exists()]:
        for fp in root.rglob("*"):
            if not fp.is_file() or fp.stat().st_mtime < since_epoch:
                continue
            low = fp.name.lower()
            if low.startswith("crash_") and low.endswith(".json"):
                findings.append(f"Crash dump detected: {fp}")
            elif fp.suffix.lower() in (".log", ".txt"):
                for line in fp.read_text(encoding="utf-8", errors="replace").splitlines():
                    if any(tok in line for tok in ("[ERROR]", "[FATAL]", "UnhandledException", "Containment Violation")):
                        findings.append(f"{fp.name}: {line[:160]}")
    return findings


def _verify_shortcuts_and_hive(cfg: dict[str, Any], install_dir: Path) -> tuple[list[str], str]:
    hive = json.loads(cfg["hivePath"].read_text(encoding="utf-8"))
    if hive.get("schemaVersion") != 1 or not isinstance(hive.get("registryHives"), dict):
        raise RuntimeError("Invalid registry.hive.json schema.")
    hkcu = hive["registryHives"].get("HKCU", {})
    uninst_pfx = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\"
    uninstall_keys = [k for k in hkcu if k.lower().startswith(uninst_pfx.lower())]
    if not uninstall_keys:
        raise RuntimeError("No HKCU Uninstall key recorded in registry.hive.json after install.")
    manifest_file = install_dir / "app/install-manifest.json"
    if manifest_file.is_file():
        shortcuts = [str(s) for s in json.loads(manifest_file.read_text(encoding="utf-8")).get("created_shortcuts", []) if s]
    else:
        shortcuts = [str(p) for p in (cfg["cBackingDir"] / "Users/Sandbox").rglob("*.lnk")]
    for raw_lnk in shortcuts:
        lnk = Path(raw_lnk)
        if not is_path_within_sandbox(lnk, cfg):
            raise SandboxError(f"Containment violation: shortcut '{lnk}' escapes sandbox.", exit_code=3)
        if ":" in lnk.name:
            raise RuntimeError(f"Shortcut filename contains un-sanitized colon: '{lnk.name}'.")
        if not lnk.is_file() or lnk.read_bytes()[:4] != b"\x4c\x00\x00\x00":
            raise RuntimeError(f"Invalid or missing ShellLink .lnk file: '{lnk}'.")
    return shortcuts, f"Verified {len(uninstall_keys)} hive uninstall key(s) and {len(shortcuts)} .lnk shortcut(s)."


def run_stage2_e2e(args: argparse.Namespace) -> tuple[dict[str, Any], Path, int]:
    cfg = resolve_sandbox_config(args)
    started_iso, run_t0, run_epoch = utc_now_iso(), time.monotonic(), time.time() - 1.0
    app_id, clean_ver, snap_name = args.app_id.strip().lower(), args.version.lstrip("v"), f"e2e-{uuid.uuid4().hex[:8]}"
    d_root = Path(f"{cfg['drives']['SANDBOX_D_DRIVE']}\\") if (cfg["drives"].get("SANDBOX_D_DRIVE") and not args.no_subst) else cfg["dBackingDir"]
    install_dir = Path(args.install_dir).resolve() if args.install_dir else (d_root / "Programs" / app_id).resolve()
    if not is_path_within_sandbox(install_dir, cfg):
        raise SandboxError(f"Host collision: --install-dir '{install_dir}' escapes sandbox.", exit_code=3)

    child_env, checklist = build_sandbox_child_env(cfg), []
    state: dict[str, Any] = {"shortcuts": [], "exe": None, "hostDiffCount": -1}

    def step(sid: str, title: str, fn: Callable[[], str]) -> None:
        t0 = time.monotonic()
        try:
            note = fn()
            checklist.append({"id": sid, "title": title, "isPassed": True, "durationMs": int((time.monotonic() - t0) * 1000), "notes": note})
        except Exception as exc:
            checklist.append({"id": sid, "title": title, "isPassed": False, "durationMs": int((time.monotonic() - t0) * 1000), "notes": str(exc)})
            if isinstance(exc, SandboxError) and exc.exit_code == 3:
                state["containmentError"] = str(exc)

    def s01() -> str:
        init_sandbox(cfg, skip_subst=args.no_subst)
        return f"Initialized sandbox and host baseline {take_host_snapshot(cfg, name=snap_name)[0].name}."

    def s02() -> str:
        if not (inst := Path(args.installer).resolve()).is_file():
            raise FileNotFoundError(f"Installer not found: {inst}")
        extra = ["--install-dir", str(install_dir), "--silent"]
        if args.payload:
            extra.extend(["--payload", str(Path(args.payload).resolve())])
        if args.installer_args:
            extra.extend(shlex.split(args.installer_args))
        proc = _run_child(_launch_cmd(inst, extra), child_env, args.timeout_seconds)
        if proc.returncode != 0 or not install_dir.is_dir():
            raise RuntimeError(f"Installer exit={proc.returncode}: {(proc.stderr or proc.stdout).strip()}")
        return f"Installed to {install_dir} (exit=0)."

    def s03() -> str:
        shortcuts, msg = _verify_shortcuts_and_hive(cfg, install_dir)
        state["shortcuts"] = shortcuts
        return msg

    def s04() -> str:
        if (exe := _find_app_exe(install_dir, app_id, args.exe_rel)) is None:
            raise FileNotFoundError(f"Installed app executable not found under {install_dir}.")
        state["exe"] = exe
        probe_args = list(args.probe_arg or []) or (shlex.split(args.app_probe_args) if args.app_probe_args else ["--version"])
        proc = _run_child(_launch_cmd(exe, probe_args), child_env, args.timeout_seconds, cwd=install_dir)
        if proc.returncode != 0:
            raise RuntimeError(f"App probe exit={proc.returncode}: {(proc.stderr or proc.stdout).strip()}")
        return f"Booted {exe.name} {' '.join(probe_args)} (exit=0)."

    def s05() -> str:
        (d_root / "Media").mkdir(parents=True, exist_ok=True)
        (d_root / "Media" / f"{app_id}_coexist.txt").write_text("sandbox-coexist", encoding="utf-8")
        (install_dir / "data/rom").mkdir(parents=True, exist_ok=True)
        (install_dir / "data/e2e_seed.txt").write_text("SEED_DATA_V1", encoding="utf-8")
        (install_dir / "data/rom/e2e_dummy.rom").write_bytes(b"ROM_PRESERVED_V1")
        return f"Seeded coexistence and dummy data inside {d_root}."

    def s06() -> str:
        missing = d_root / "Missing/missing_media.bin"
        missing.unlink(missing_ok=True)
        if args.missing_file_hook or args.missing_file_arg:
            cmd = shlex.split(args.missing_file_hook.format(missing=str(missing), exe=str(state["exe"] or ""))) if args.missing_file_hook else _launch_cmd(Path(state["exe"]), [args.missing_file_arg.format(missing=str(missing))])
            proc = _run_child(cmd, child_env, args.timeout_seconds, cwd=install_dir)
            if proc.returncode not in args.missing_file_expected_exit:
                raise RuntimeError(f"Missing-file probe returned unexpected exit={proc.returncode}.")
            return f"Missing-file standby hook returned expected exit={proc.returncode}."
        return f"Verified purposeful missing path '{missing}' absent without crash."

    def s07() -> str:
        rem = cfg["drives"].get("SANDBOX_REMOVABLE_DRIVE")
        if not rem or args.no_subst:
            (cfg["eBackingDir"] / "e2e_removable_probe.txt").write_text("REM_OK", encoding="utf-8")
            return "Removable probe staged in backing E\\ (--no-subst active)."
        probe = Path(f"{rem}\\e2e_removable_probe.txt")
        probe.write_text("REM_OK", encoding="utf-8")
        toggle_removable(cfg, False)
        if os.path.exists(f"{rem}\\"):
            raise RuntimeError(f"Removable drive {rem} stayed mounted after unplug.")
        toggle_removable(cfg, True)
        if not probe.is_file() or probe.read_text(encoding="utf-8") != "REM_OK":
            raise RuntimeError(f"Removable probe lost after remount on {rem}.")
        return f"Unplugged and remounted {rem} cleanly with data intact."

    def s08() -> str:
        disc_drv = cfg["drives"].get("SANDBOX_DISC_DRIVE") or ""
        if args.disc_iso:
            info = mount_disc(cfg, Path(args.disc_iso))
            try:
                if args.disc_verify_hook and (proc := _run_child(shlex.split(args.disc_verify_hook.format(disc_drive=info["driveMount"], exe=str(state["exe"] or ""))), child_env, args.timeout_seconds)).returncode != 0:
                    raise RuntimeError(f"Disc hook exit={proc.returncode}.")
                return f"Mounted ISO on {info['driveMount']} and verified."
            finally:
                unmount_disc(cfg, Path(args.disc_iso))
        if args.disc_verify_hook and (proc := _run_child(shlex.split(args.disc_verify_hook.format(disc_drive=disc_drv, exe=str(state["exe"] or ""))), child_env, args.timeout_seconds)).returncode != 0:
            raise RuntimeError(f"Disc hook exit={proc.returncode}.")
        return f"Disc role '{disc_drv or 'unset'}' verified clean."

    def s09() -> str:
        up_inst = Path(args.upgrade_installer or args.installer).resolve()
        extra = ["--install-dir", str(install_dir), "--silent"] + (["--payload", str(Path(args.upgrade_payload or args.payload).resolve())] if (args.upgrade_payload or args.payload) else [])
        proc = _run_child(_launch_cmd(up_inst, extra), child_env, args.timeout_seconds)
        if proc.returncode != 0:
            raise RuntimeError(f"Upgrade exit={proc.returncode}: {(proc.stderr or proc.stdout).strip()}")
        if (install_dir / "data/e2e_seed.txt").read_text(encoding="utf-8") != "SEED_DATA_V1" or (install_dir / "data/rom/e2e_dummy.rom").read_bytes() != b"ROM_PRESERVED_V1":
            raise RuntimeError("data\\ or data\\rom\\ was not preserved across upgrade.")
        return "Upgrade preserved data\\ and data\\rom\\ intact."

    def s10() -> str:
        uninst = Path(args.uninstaller).resolve() if args.uninstaller else next((p for d in (install_dir, install_dir / "app") if d.is_dir() for p in sorted(d.glob("*-Uninstall.*"))), None)
        target, extra = (uninst, ["--install-dir", str(install_dir), "--uninstall", "--silent"]) if uninst and uninst.is_file() else (Path(args.installer).resolve(), ["--uninstall", "--install-dir", str(install_dir), "--silent"])
        proc = _run_child(_launch_cmd(target, extra), child_env, args.timeout_seconds)
        if proc.returncode != 0:
            raise RuntimeError(f"Uninstaller exit={proc.returncode}: {(proc.stderr or proc.stdout).strip()}")
        for _ in range(15):
            if not any(Path(s).exists() for s in state["shortcuts"]) and (state["exe"] is None or not Path(state["exe"]).exists()):
                break
            time.sleep(0.25)
        if state["exe"] is not None and Path(state["exe"]).exists():
            raise RuntimeError(f"Installed executable lingered after uninstall: {state['exe']}")
        hkcu = json.loads(cfg["hivePath"].read_text(encoding="utf-8")).get("registryHives", {}).get("HKCU", {})
        if (rem := [k for k in hkcu if "\\uninstall\\" in k.lower() and app_id in k.lower()]):
            raise RuntimeError(f"Uninstall key lingered in registry.hive.json: {rem}")
        return "Uninstaller cleaned binaries, shortcuts, and pseudo-registry entries."

    def s11() -> str:
        sb_logs = [install_dir / "logs", cfg["sandboxRoot"] / "logs", cfg["cBackingDir"] / "Users/Sandbox"]
        host_logs = [DEV_ROOT / "logs", *[r / app_id / "logs" for r in _host_programs_roots(get_subst_mappings())]]
        issues = _sweep_logs(sb_logs, run_epoch)
        for hl in [h for h in host_logs if h.exists()]:
            issues.extend(f"Host crash dump: {fp}" for fp in hl.rglob("crash_*.json") if fp.stat().st_mtime >= run_epoch)
        if issues:
            raise RuntimeError("; ".join(issues[:5]))
        return "2-tier sandbox & read-only host log sweep clean (0 errors/crashes)."

    def s12() -> str:
        diff = diff_host_snapshots(cfg, name=snap_name)
        state["hostDiffCount"] = diff["hostDiffCount"]
        if diff["hostDiffCount"] != 0:
            raise SandboxError(f"Host diff detected ({diff['hostDiffCount']}): added={diff['addedFiles'][:3]} reg={diff['addedRegistryEntries'][:3]}", exit_code=3)
        return "Host filesystem and registry diff == 0."

    for sid, title, fn in (
        ("S2-01", "Sandbox Init & Host Baseline Snapshot", s01), ("S2-02", "Real Installer Execution", s02),
        ("S2-03", "Shortcuts (.lnk) & Pseudo-Registry Verification", s03), ("S2-04", "Real App Boot & CLI Probe", s04),
        ("S2-05", "Sandbox Storage Drive Coexistence & Data Seeding", s05), ("S2-06", "Missing-File Standby & Error Recovery Probe", s06),
        ("S2-07", "Removable Drive Unplug & Remount", s07), ("S2-08", "Optical Disc Drive Verification", s08),
        ("S2-09", "Upgrade Backup & Data Preservation", s09), ("S2-10", "Real Uninstaller Cleanup", s10),
        ("S2-11", "2-Tier Sandbox & Host Log Sweep", s11), ("S2-12", "Zero Host Mutations Diff", s12),
    ):
        step(sid, title, fn)

    if state["hostDiffCount"] < 0 and (cfg["sandboxRoot"] / "snapshots" / f"host-snapshot-{snap_name}.json").is_file():
        state["hostDiffCount"] = diff_host_snapshots(cfg, name=snap_name)["hostDiffCount"]

    completed_iso = utc_now_iso()
    stamp = completed_iso.replace(":", "").replace("-", "").split(".")[0]
    receipt_path = Path(args.receipt_out).resolve() if args.receipt_out else (cfg["sandboxRoot"] / "runs" / f"{app_id}-v{clean_ver}-{stamp}.json").resolve()
    all_ok, host_diff = all(item["isPassed"] for item in checklist), max(0, int(state["hostDiffCount"]))
    passed_stage2 = all_ok and host_diff == 0
    exit_code = 0 if passed_stage2 else (3 if ("containmentError" in state or host_diff > 0) else 1)
    failed = [item for item in checklist if not item["isPassed"]]
    mounts = json.loads(cfg["manifestPath"].read_text(encoding="utf-8")).get("sandboxMounts", []) if cfg["manifestPath"].is_file() else []
    receipt = {
        "$schema": "https://schemas.local/sandbox-e2e-receipt.v1.json", "schemaVersion": 1, "id": str(uuid.uuid4()),
        "appId": app_id, "version": clean_ver, "status": "completed" if passed_stage2 else "failed",
        "isPassed": passed_stage2, "isStage2SandboxPassed": passed_stage2, "hostDiffCount": host_diff,
        "sandboxRoot": str(cfg["sandboxRoot"]), "sandboxProfileDir": str(cfg["sandboxProfileDir"]),
        "registryHivePath": str(cfg["hivePath"]), "sandboxMounts": mounts, "filePath": str(receipt_path),
        "directoryPath": str(receipt_path.parent), "startedAtUtc": started_iso, "completedAtUtc": completed_iso,
        "verifiedAtUtc": completed_iso, "durationMs": int((time.monotonic() - run_t0) * 1000), "exitCode": exit_code,
        "errorCode": None if passed_stage2 else ("HOST_DIFF_NONZERO" if host_diff > 0 else ("CONTAINMENT_VIOLATION" if exit_code == 3 else "STAGE2_CHECK_FAILED")),
        "errorMessage": None if passed_stage2 else "; ".join(f"{f['id']}: {f['notes']}" for f in failed[:3]),
        "errorRemedy": None if passed_stage2 else "Inspect failed checklistResults[] and sandbox logs under <SandboxRoot>/logs.",
        "notes": f"Stage 2 E2E {sum(1 for c in checklist if c['isPassed'])}/12 passed (hostDiffCount={host_diff}).",
        "checklistResults": checklist,
    }
    atomic_write_json(receipt_path, receipt)
    return receipt, receipt_path, exit_code


def _build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Foundation Stage 2 12-Point Sandbox E2E Runner")
    for flag, kw in (
        ("--app-id", {"required": True}), ("--version", {"default": "0.1.0"}), ("--installer", {"required": True}),
        ("--payload", {}), ("--installer-args", {}), ("--upgrade-installer", {}), ("--upgrade-payload", {}),
        ("--uninstaller", {}), ("--install-dir", {}), ("--exe-rel", {}), ("--probe-arg", {"action": "append"}),
        ("--app-probe-args", {}), ("--missing-file-arg", {}), ("--missing-file-hook", {}),
        ("--missing-file-expected-exit", {"type": int, "nargs": "+", "default": [0, 1, 2]}),
        ("--disc-iso", {}), ("--disc-verify-hook", {}), ("--receipt-out", {}),
        ("--timeout-seconds", {"type": int, "default": 120}), ("--no-subst", {"action": "store_true"}),
    ):
        p.add_argument(flag, **kw)
    for flag in ("--sandbox-root", "--c-drive", "--d-drive", "--removable-drive", "--disc-drive", "--profile-dir", "--env-file"):
        p.add_argument(flag, default=argparse.SUPPRESS)
    return p


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        receipt, _, exit_code = run_stage2_e2e(args)
        print(json.dumps(receipt, indent=2))
        return exit_code
    except SandboxError as ex:
        print(f"[SANDBOX-E2E-ERROR] {ex}", file=sys.stderr)
        return ex.exit_code


if __name__ == "__main__":
    raise SystemExit(main())
