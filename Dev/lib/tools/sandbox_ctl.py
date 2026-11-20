# @module: Foundation.Tools.sandbox_ctl
# @purpose: Non-elevated 4-drive Stage 2 sandbox orchestrator (subst mounts, ISO disc mount, Standard 40 manifest, registry baseline/hive, host snapshot/diff).
# @depends-on: argparse, ctypes, datetime, itertools, json, os, pathlib, re, shutil, subprocess, sys, tempfile, winreg
# @exports: SandboxError, utc_now_iso, atomic_write_json, load_env_file, get_subst_mappings, resolve_sandbox_config, init_sandbox, mount_sandbox, toggle_removable, mount_disc, unmount_disc, get_status, take_host_snapshot, diff_host_snapshots, reset_sandbox, main

from __future__ import annotations

import argparse
import ctypes
import itertools
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

winreg = __import__("winreg") if os.name == "nt" else None
DEV_ROOT = Path(__file__).resolve().parent.parent
REPO_ROOT = DEV_ROOT.parent
SDK_VERSION = "0.5.2"
ROLE_DIRS = {"SANDBOX_C_DRIVE": "C", "SANDBOX_D_DRIVE": "D", "SANDBOX_REMOVABLE_DRIVE": "E"}


class SandboxError(RuntimeError):
    def __init__(self, message: str, exit_code: int = 1) -> None:
        super().__init__(message)
        self.exit_code = exit_code


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def atomic_write_json(path: Path, payload: dict[str, Any]) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp, bak = path.with_name(f"{path.name}.tmp"), path.with_name(f"{path.name}.bak")
    with tmp.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(payload, indent=2) + "\n")
        fh.flush()
        os.fsync(fh.fileno())
    if path.exists():
        shutil.copy2(path, bak)
    os.replace(tmp, path)
    return path


def load_env_file(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    pairs = (ln.strip().removeprefix("export ").strip().split("=", 1) for ln in path.read_text(encoding="utf-8", errors="replace").splitlines() if ln.strip() and not ln.strip().startswith("#") and "=" in ln)
    return {k.strip(): (v[1:-1] if len(v := rv.strip()) >= 2 and v[0] == v[-1] and v[0] in ('"', "'") else v) for k, rv in pairs if k.strip() and rv.strip()}


def _subst(args: list[str]) -> str:
    proc = subprocess.run(["subst", *args], capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise SandboxError(f"subst {' '.join(args)} failed: {(proc.stderr or proc.stdout).strip()}")
    return proc.stdout.strip()


def _normalize_drive(raw: str | None, label: str) -> str | None:
    if not raw or not raw.strip():
        return None
    c = raw.strip().rstrip("\\/").upper()
    c = f"{c}:" if len(c) == 1 and c.isalpha() else c
    if not re.fullmatch(r"[A-Z]:", c):
        raise SandboxError(f"Invalid drive for {label}: '{raw}'. Expected 'X:'.", exit_code=2)
    return c


def get_subst_mappings() -> dict[str, Path]:
    if os.name != "nt":
        return {}
    pat = re.compile(r"^\s*([A-Za-z]):\\?:\s*=>\s*(.+?)\s*$")
    return {f"{m.group(1).upper()}:": Path(m.group(2).strip()).resolve() for ln in _subst([]).splitlines() if (m := pat.match(ln))}


def _host_programs_roots(subst_map: dict[str, Path]) -> list[Path]:
    extra = [p.resolve() for ch in "DEFGHIJKLMNOPQRSTUVWXYZ" if f"{ch}:" not in subst_map and (p := Path(f"{ch}:{os.sep}Programs")).exists()] if os.name == "nt" else []
    return ([Path(os.environ["LOCALAPPDATA"]).resolve() / "Programs"] if os.environ.get("LOCALAPPDATA") else []) + extra


def _validate_no_host_collisions(root: Path, drives: dict[str, str | None], subst_map: dict[str, Path], check_subst: bool = True) -> None:
    res = root.resolve()
    if res.parent == res or (len(res.parts) >= 2 and res.parts[1].lower() in ("programs", "projects")):
        raise SandboxError(f"Host collision: sandbox root '{res}' targets drive root or host Programs/Projects.", exit_code=3)
    always = [Path(v).resolve() for k in ("WINDIR", "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramData") if (v := os.environ.get(k))] + _host_programs_roots(subst_map) + [REPO_ROOT.resolve()]
    user_roots = [Path(v).resolve() for k in ("USERPROFILE", "LOCALAPPDATA", "APPDATA") if (v := os.environ.get(k))]
    temp_root = Path(tempfile.gettempdir()).resolve()
    for prot in (always if (res == temp_root or temp_root in res.parents) else [*always, *user_roots]):
        if res == prot or prot in res.parents or res in prot.parents:
            raise SandboxError(f"Host collision: sandbox root '{res}' overlaps '{prot}'.", exit_code=3)
    blocked = {"A:", "B:", "C:", os.environ.get("SystemDrive", "C:")[:2].upper()} | {d for p in [*always, *user_roots] if (d := os.path.splitdrive(str(p))[0].upper()) and d not in subst_map}
    seen: set[str] = set()
    for role, drv in drives.items():
        if not drv:
            continue
        dtype = int(ctypes.windll.kernel32.GetDriveTypeW(f"{drv}\\")) if os.name == "nt" else 1
        bad_subst = role in ROLE_DIRS and ((drv not in subst_map and dtype != 1) or (check_subst and drv in subst_map and subst_map[drv] != (res / ROLE_DIRS[role]).resolve()))
        if drv in blocked or drv in seen or bad_subst or (role == "SANDBOX_DISC_DRIVE" and (drv in subst_map or dtype not in (1, 5))):
            raise SandboxError(f"Host collision: {role} '{drv}' conflicts with host or existing mount.", exit_code=3)
        seen.add(drv)


def resolve_sandbox_config(args: argparse.Namespace) -> dict[str, Any]:
    secrets = Path(os.environ.get("USERPROFILE") or Path.home()) / ".gemini" / ".secrets" / ".env"
    layers = [load_env_file(secrets), load_env_file(DEV_ROOT / ".env"), *([load_env_file(Path(args.env_file))] if getattr(args, "env_file", None) else []), {k: v for k, v in os.environ.items() if v}]
    def lookup(ek: str, ca: str) -> str | None:
        return str(getattr(args, ca)).strip() if getattr(args, ca, None) else next((l[ek].strip() for l in reversed(layers) if l.get(ek)), None)
    drives = {k: _normalize_drive(lookup(k, a), k) for k, a in (("SANDBOX_C_DRIVE", "c_drive"), ("SANDBOX_D_DRIVE", "d_drive"), ("SANDBOX_REMOVABLE_DRIVE", "removable_drive"), ("SANDBOX_DISC_DRIVE", "disc_drive"))}
    subst_map, raw_root = get_subst_mappings(), lookup("FOUNDATION_SANDBOX_ROOT", "sandbox_root")
    inferred = next((subst_map[drives[r]].parent.resolve() for r in ROLE_DIRS if drives[r] and drives[r] in subst_map), None)
    if not raw_root and inferred is None:
        raise SandboxError("FOUNDATION_SANDBOX_ROOT unset and no subst drive mounted. Pass --sandbox-root.", exit_code=2)
    root = Path(raw_root).resolve() if raw_root else inferred
    assert root is not None
    c_mnt, raw_prof = drives["SANDBOX_C_DRIVE"], lookup("FOUNDATION_SANDBOX_PROFILE_DIR", "profile_dir")
    prof = Path(raw_prof) if raw_prof else (Path(f"{c_mnt}\\Users\\Sandbox") if c_mnt else root / "C/Users/Sandbox")
    return {"sandboxRoot": root, "sandboxProfileDir": prof, "drives": drives, "substMap": subst_map, "cBackingDir": (root / "C").resolve(), "dBackingDir": (root / "D").resolve(), "eBackingDir": (root / "E").resolve(), "manifestPath": (root / "sandbox.manifest.json").resolve(), "baselineHivePath": (root / "registry/registry.baseline.json").resolve(), "hivePath": (root / "registry/registry.hive.json").resolve()}


def _build_mounts(cfg: dict[str, Any], subst_map: dict[str, Path]) -> list[dict[str, Any]]:
    specs = (("SANDBOX_C_DRIVE", "cBackingDir"), ("SANDBOX_D_DRIVE", "dBackingDir"), ("SANDBOX_REMOVABLE_DRIVE", "eBackingDir"), ("SANDBOX_DISC_DRIVE", ""))
    return [{"driveRole": r, "driveMount": drv, "directoryPath": f"{drv}\\" if not k else str(cfg[k]), "isEnabled": os.path.exists(f"{drv}\\") if not k else subst_map.get(drv) == cfg[k]} for r, k in specs if (drv := cfg["drives"][r])]


def _build_baseline_hive(cfg: dict[str, Any]) -> dict[str, Any]:
    def val(k: str, d: Any) -> dict[str, Any]:
        return {"registryValueKind": k, "registryValueData": d}
    c_pfx, prof = cfg["drives"]["SANDBOX_C_DRIVE"] or str(cfg["cBackingDir"]), str(cfg["sandboxProfileDir"])
    wv2 = {"pv": val("REG_SZ", "130.0.2849.68"), "name": val("REG_SZ", "Microsoft Edge WebView2 Runtime"), "location": val("REG_SZ", f"{c_pfx}\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application")}
    vc = {"Installed": val("REG_DWORD", 1), "Version": val("REG_SZ", "v14.40.33810.00"), "Major": val("REG_DWORD", 14), "Minor": val("REG_DWORD", 40), "Bld": val("REG_DWORD", 33810)}
    sf = {k: val("REG_EXPAND_SZ", f"{prof}\\{s}") for k, s in (("Desktop", "Desktop"), ("Personal", "Documents"), ("Programs", r"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"), ("Start Menu", r"AppData\Roaming\Microsoft\Windows\Start Menu"), ("AppData", r"AppData\Roaming"), ("Local AppData", r"AppData\Local"))}
    w1, w2 = r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    v1, v2 = r"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64", r"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
    return {"$schema": "https://schemas.local/registry-hive.v1.json", "schemaVersion": 1, "sandboxRoot": str(cfg["sandboxRoot"]), "updatedAtUtc": utc_now_iso(), "registryHives": {"HKCU": {r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders": sf}, "HKLM": {w1: wv2, w2: wv2, v1: vc, v2: vc}, "HKCR": {}}}


def mount_sandbox(cfg: dict[str, Any], skip_subst: bool = False) -> list[dict[str, Any]]:
    _validate_no_host_collisions(cfg["sandboxRoot"], cfg["drives"], cfg["substMap"], check_subst=not skip_subst)
    c, d, e, r = cfg["cBackingDir"], cfg["dBackingDir"], cfg["eBackingDir"], cfg["sandboxRoot"]
    for p in (*[c / s for s in ("Users/Sandbox/Desktop", "Users/Sandbox/Documents", "Users/Sandbox/AppData/Local/Programs", "Users/Sandbox/AppData/Local/Temp", "Users/Sandbox/AppData/Roaming/Microsoft/Windows/Start Menu/Programs", "Program Files", "Program Files (x86)", "ProgramData", "Windows/System32")], d / "Programs", d / "Projects", d / "Media", e, r / "registry", r / "locks", r / "snapshots", r / "runs", r / "logs"):
        p.mkdir(parents=True, exist_ok=True)
    if not skip_subst and os.name == "nt":
        for role, sub in ROLE_DIRS.items():
            if (drv := cfg["drives"][role]) and cfg["substMap"].get(drv) != (tgt := (r / sub).resolve()):
                _subst([drv, str(tgt)])
    return _build_mounts(cfg, get_subst_mappings())


def init_sandbox(cfg: dict[str, Any], skip_subst: bool = False, force_hive: bool = False) -> dict[str, Any]:
    mounts, baseline = mount_sandbox(cfg, skip_subst=skip_subst), _build_baseline_hive(cfg)
    atomic_write_json(cfg["baselineHivePath"], baseline)
    if force_hive or not cfg["hivePath"].exists():
        atomic_write_json(cfg["hivePath"], baseline)
    manifest = {"$schema": "https://schemas.local/sandbox-manifest.v2.json", "schemaVersion": 2, "appId": "foundation-sandbox", "version": SDK_VERSION, "updatedAtUtc": utc_now_iso(), "sandboxRoot": str(cfg["sandboxRoot"]), "sandboxProfileDir": str(cfg["sandboxProfileDir"]), "registryHivePath": str(cfg["hivePath"]), "sandboxMounts": mounts}
    atomic_write_json(cfg["manifestPath"], manifest)
    return manifest


def toggle_removable(cfg: dict[str, Any], mount: bool) -> dict[str, Any]:
    if not (drv := cfg["drives"]["SANDBOX_REMOVABLE_DRIVE"]):
        raise SandboxError("SANDBOX_REMOVABLE_DRIVE is not configured.", exit_code=2)
    subst_map = get_subst_mappings()
    _validate_no_host_collisions(cfg["sandboxRoot"], cfg["drives"], subst_map, check_subst=mount)
    if mount:
        cfg["eBackingDir"].mkdir(parents=True, exist_ok=True)
        if os.name == "nt" and subst_map.get(drv) != cfg["eBackingDir"]:
            _subst([drv, str(cfg["eBackingDir"])])
        return {"driveRole": "SANDBOX_REMOVABLE_DRIVE", "driveMount": drv, "directoryPath": str(cfg["eBackingDir"]), "isEnabled": True}
    if drv in subst_map and os.name == "nt":
        _subst([drv, "/D"])
    return {"driveRole": "SANDBOX_REMOVABLE_DRIVE", "driveMount": drv, "isEnabled": False}


def _run_ps(cmd: str, check: bool = True) -> str:
    proc = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", cmd], capture_output=True, text=True, check=False)
    if check and proc.returncode != 0:
        raise SandboxError(f"PowerShell failed: {(proc.stderr or proc.stdout).strip()}")
    return proc.stdout.strip()


def mount_disc(cfg: dict[str, Any], iso_path: Path | None = None) -> dict[str, Any]:
    drv = cfg["drives"]["SANDBOX_DISC_DRIVE"]
    _validate_no_host_collisions(cfg["sandboxRoot"], cfg["drives"], get_subst_mappings(), check_subst=False)
    if iso_path is not None:
        if not (full_iso := iso_path.resolve()).is_file():
            raise SandboxError(f"ISO image not found: {full_iso}")
        out = _run_ps(f"(Mount-DiskImage -ImagePath '{str(full_iso).replace(chr(39), chr(39)*2)}' -PassThru | Get-Volume).DriveLetter")
        mounted = f"{out.splitlines()[-1].strip().upper()}:" if out else (drv or "")
        return {"driveRole": "SANDBOX_DISC_DRIVE", "driveMount": mounted, "isoPath": str(full_iso), "isEnabled": bool(mounted)}
    dtype = int(ctypes.windll.kernel32.GetDriveTypeW(f"{drv}\\")) if drv and os.name == "nt" else 1
    if not drv or dtype != 5 or not os.path.exists(f"{drv}\\"):
        raise SandboxError(f"SANDBOX_DISC_DRIVE '{drv or ''}' is not mounted.", exit_code=2 if not drv else 1)
    return {"driveRole": "SANDBOX_DISC_DRIVE", "driveMount": drv, "isEnabled": True}


def unmount_disc(cfg: dict[str, Any], iso_path: Path | None = None) -> dict[str, Any]:
    drv = cfg["drives"]["SANDBOX_DISC_DRIVE"]
    if iso_path is not None:
        _run_ps(f"Dismount-DiskImage -ImagePath '{str(iso_path.resolve()).replace(chr(39), chr(39)*2)}'")
    elif drv and os.name == "nt" and int(ctypes.windll.kernel32.GetDriveTypeW(f"{drv}\\")) == 5:
        _run_ps(f"Get-Volume -DriveLetter '{drv[0]}' -EA SilentlyContinue | Get-DiskImage -EA SilentlyContinue | Dismount-DiskImage", check=False)
    return {"driveRole": "SANDBOX_DISC_DRIVE", "driveMount": drv, "isEnabled": False}


def _scan_host_state() -> tuple[dict[str, list[int]], dict[str, str]]:
    skip = {"data", "logs", "cache", "webview2_cache", "ebwebview", "gpu_cache", "code cache", "node_modules"}
    desk_menu = [p for p in (Path(os.environ["USERPROFILE"]) / "Desktop" if os.environ.get("USERPROFILE") else None, Path(os.environ["APPDATA"]) / "Microsoft/Windows/Start Menu/Programs" if os.environ.get("APPDATA") else None) if p and p.exists()]
    files: dict[str, list[int]] = {}
    stack = [(r, 0, 8) for r in desk_menu] + [(r, 0, 3) for r in _host_programs_roots(get_subst_mappings()) if r.exists()]
    while stack:
        cur, depth, max_d = stack.pop()
        try:
            with os.scandir(cur) as it:
                for e in it:
                    is_dir = e.is_dir(follow_symlinks=False)
                    if is_dir and max_d == 3 and e.name.lower() in skip:
                        continue
                    st = e.stat(follow_symlinks=False)
                    files[e.path] = [0 if is_dir else st.st_size, st.st_mtime_ns]
                    if is_dir and depth < max_d:
                        stack.append((Path(e.path), depth + 1, max_d))
        except OSError:
            continue
    reg: dict[str, str] = {}
    for hn, hv, sub, is_sub in (("HKCU", winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Uninstall", True), ("HKCU", winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", False), ("HKLM", winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", True)) if winreg else ():
        try:
            with winreg.OpenKey(hv, sub, 0, winreg.KEY_READ) as key:
                for idx in itertools.count():
                    reg[f"{hn}\\{sub}\\{winreg.EnumKey(key, idx)}" if is_sub else f"{hn}\\{sub}::{winreg.EnumValue(key, idx)[0]}"] = "subkey" if is_sub else str(winreg.EnumValue(key, idx)[1])
        except OSError:
            continue
    return files, reg


def take_host_snapshot(cfg: dict[str, Any], name: str = "baseline", output_path: Path | None = None) -> tuple[Path, dict[str, Any]]:
    snap = output_path.resolve() if output_path else (cfg["sandboxRoot"] / "snapshots" / f"host-snapshot-{name}.json").resolve()
    files, reg = _scan_host_state()
    return atomic_write_json(snap, {"capturedAtUtc": utc_now_iso(), "files": files, "registry": reg}), {"files": files, "registry": reg}


def diff_host_snapshots(cfg: dict[str, Any], baseline_path: Path | None = None, name: str = "baseline") -> dict[str, Any]:
    snap = baseline_path.resolve() if baseline_path else (cfg["sandboxRoot"] / "snapshots" / f"host-snapshot-{name}.json").resolve()
    if not snap.is_file():
        raise SandboxError(f"Host baseline snapshot not found: {snap}. Run 'snapshot' first.")
    base, (cf, cr) = json.loads(snap.read_text(encoding="utf-8")), _scan_host_state()
    diffs = {k: (sorted(x for x in cur if x not in old), sorted(x for x in old if x not in cur), sorted(x for x in cur if x in old and cur[x] != old[x])) for k, cur, old in (("Files", cf, base.get("files", {})), ("RegistryEntries", cr, base.get("registry", {})))}
    return {"hostDiffCount": sum(len(p) for t in diffs.values() for p in t), **{f"{pfx}{k}": diffs[k][i] for k in diffs for i, pfx in enumerate(("added", "removed", "modified"))}}


def reset_sandbox(cfg: dict[str, Any], keep_runs: bool = False, skip_subst: bool = False) -> dict[str, Any]:
    _validate_no_host_collisions(cfg["sandboxRoot"], cfg["drives"], get_subst_mappings(), check_subst=False)
    for folder in ["C", "D", "E", "locks", "snapshots", "logs"] + ([] if keep_runs else ["runs"]):
        if (target := cfg["sandboxRoot"] / folder).exists():
            for child in list(target.iterdir()):
                shutil.rmtree(child) if child.is_dir() else child.unlink(missing_ok=True)
    return init_sandbox(cfg, skip_subst=skip_subst, force_hive=True)


def get_status(cfg: dict[str, Any]) -> dict[str, Any]:
    subst_map = get_subst_mappings()
    _validate_no_host_collisions(cfg["sandboxRoot"], cfg["drives"], subst_map, check_subst=False)
    return {"sandboxRoot": str(cfg["sandboxRoot"]), "sandboxProfileDir": str(cfg["sandboxProfileDir"]), "registryHivePath": str(cfg["hivePath"]), "isManifestPresent": cfg["manifestPath"].is_file(), "isHivePresent": cfg["hivePath"].is_file(), "sandboxMounts": _build_mounts(cfg, subst_map)}


def _build_parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    for flag in ("--sandbox-root", "--c-drive", "--d-drive", "--removable-drive", "--disc-drive", "--profile-dir", "--env-file"):
        common.add_argument(flag, default=argparse.SUPPRESS)
    common.add_argument("--json", action="store_true", default=argparse.SUPPRESS)
    parser = argparse.ArgumentParser(description="Foundation 4-Drive Sandbox Controller", parents=[common])
    sub = parser.add_subparsers(dest="command", required=True)
    specs = {"init": [("--no-subst", {"action": "store_true"}), ("--force-hive", {"action": "store_true"})], "mount": [("--no-subst", {"action": "store_true"})], "unmount-removable": [], "remount-removable": [], "status": [], "mount-disc": [("--iso", {"type": Path})], "unmount-disc": [("--iso", {"type": Path})], "snapshot": [("--name", {"default": "baseline"}), ("--output", {"type": Path})], "diff-host": [("--name", {"default": "baseline"}), ("--baseline", {"type": Path}), ("--fail-on-diff", {"action": "store_true"})], "reset": [("--keep-runs", {"action": "store_true"}), ("--no-subst", {"action": "store_true"})]}
    for cmd, opts in specs.items():
        sp = sub.add_parser(cmd, parents=[common])
        for flag, kw in opts:
            sp.add_argument(flag, **kw)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        cfg = resolve_sandbox_config(args)
        handlers = {"init": lambda: init_sandbox(cfg, args.no_subst, args.force_hive), "mount": lambda: {"sandboxRoot": str(cfg["sandboxRoot"]), "sandboxMounts": mount_sandbox(cfg, args.no_subst)}, "unmount-removable": lambda: toggle_removable(cfg, False), "remount-removable": lambda: toggle_removable(cfg, True), "mount-disc": lambda: mount_disc(cfg, args.iso), "unmount-disc": lambda: unmount_disc(cfg, args.iso), "status": lambda: get_status(cfg), "snapshot": lambda: {"snapshotPath": str(take_host_snapshot(cfg, args.name, args.output)[0])}, "diff-host": lambda: diff_host_snapshots(cfg, args.baseline, args.name), "reset": lambda: reset_sandbox(cfg, args.keep_runs, args.no_subst)}
        res = handlers[args.command]()
        print(json.dumps(res, indent=2))
        return 3 if args.command == "diff-host" and args.fail_on_diff and res["hostDiffCount"] > 0 else 0
    except SandboxError as ex:
        print(f"[SANDBOX-ERROR] {ex}", file=sys.stderr)
        return ex.exit_code


if __name__ == "__main__":
    raise SystemExit(main())
