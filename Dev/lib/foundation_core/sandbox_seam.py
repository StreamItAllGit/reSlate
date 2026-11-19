# @module: foundation_core.sandbox_seam
# @purpose: Process-env-only 4-drive Stage 2 sandbox resolution, known-folder mapping, and fail-closed ExitCode=3 containment guards (Standards 02 & 25).
# @depends-on: contextlib, ctypes, dataclasses, os, pathlib, string, subprocess, tempfile, typing, uuid
# @exports: CONTAINMENT_EXIT_CODE, SandboxContainmentError, is_containment_violation, SandboxMountSpec, SandboxResolution, get_subst_mappings, resolve_sandbox_environment, resolve_known_folders, get_known_folder, is_path_within_sandbox, assert_sandbox_path_if_active, map_host_path_to_sandbox

from __future__ import annotations

from contextlib import suppress
import ctypes
from dataclasses import dataclass
import os
from pathlib import Path
import string
import subprocess
import tempfile
from typing import Any, Mapping
import uuid

CONTAINMENT_EXIT_CODE: int = 3
_DRIVE_FIXED: int = 3
_TRUTHY: frozenset[str] = frozenset({"1", "true", "yes", "on"})
_DRIVE_ROLES: tuple[tuple[str, str], ...] = (
    ("SANDBOX_C_DRIVE", "system_drive_root"), ("SANDBOX_D_DRIVE", "storage_drive_root"),
    ("SANDBOX_REMOVABLE_DRIVE", "removable_drive_root"), ("SANDBOX_DISC_DRIVE", "disc_drive_root"),
)
_WIN_KF_GUIDS: tuple[tuple[str, str, int], ...] = (
    ("Profile", "5E6C858F-0E22-4760-9AFE-EA3317B67173", 0x4400),
    ("LocalAppData", "F1B32785-6FBA-4FCF-9D55-7B8E7F157091", 0x4400),
    ("RoamingAppData", "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D", 0x4400),
    ("Desktop", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641", 0x4000),
    ("Documents", "FDD39AD0-238F-46AF-ADB4-6C85480369C7", 0x4000),
    ("ProgramFiles", "905E63B6-C1BF-494E-B29C-65B732D3D21A", 0x4400),
    ("ProgramFilesX86", "7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E", 0x4400),
    ("ProgramData", "62AB5D82-FDC1-4DC3-A9DD-070D1D495D97", 0x4400),
    ("Windows", "F38BF404-1D43-42F2-9305-67DE0B28FC23", 0x4400),
)


class SandboxContainmentError(ValueError):
    """Raised on Stage 2 sandbox containment or host-collision violations (ExitCode=3)."""

    def __init__(self, message: str, exit_code: int = CONTAINMENT_EXIT_CODE) -> None:
        super().__init__(message)
        self.exit_code = exit_code


def is_containment_violation(exc: BaseException) -> bool:
    """Returns True when exc represents a sandbox containment violation (ExitCode=3)."""
    return isinstance(exc, SandboxContainmentError) or getattr(exc, "exit_code", None) == CONTAINMENT_EXIT_CODE


@dataclass(slots=True, frozen=True)
class SandboxMountSpec:
    drive_role: str
    drive_mount: str
    directory_path: Path | None
    is_enabled: bool

    def to_dict(self) -> dict[str, object]:
        return {"driveRole": self.drive_role, "driveMount": self.drive_mount, "directoryPath": str(self.directory_path) if self.directory_path is not None else None, "isEnabled": self.is_enabled}


@dataclass(slots=True, frozen=True)
class SandboxResolution:
    is_sandbox_active: bool
    sandbox_root: Path | None = None
    system_drive_root: Path | None = None
    storage_drive_root: Path | None = None
    removable_drive_root: Path | None = None
    disc_drive_root: Path | None = None
    sandbox_profile_dir: Path | None = None
    registry_hive_path: Path | None = None
    registry_baseline_path: Path | None = None
    registry_lock_path: Path | None = None
    mounts: tuple[SandboxMountSpec, ...] = ()
    backing_roots: tuple[Path, ...] = ()


def _lookup_env(env: Mapping[str, str | None], key: str) -> str | None:
    upper = key.upper()
    return next((v.strip() for k, v in env.items() if k.upper() == upper and isinstance(v, str) and v.strip()), None)


def _normalize_path(raw: str | Path) -> Path:
    text = str(raw).strip().strip('"')
    text = ("\\\\" + text[8:]) if text.upper().startswith("\\\\?\\UNC\\") else (text[4:] if text.startswith("\\\\?\\") else text)
    if not text:
        raise SandboxContainmentError("Fail-Closed Containment Violation: empty path is not permitted.")
    if os.name == "nt" and len(text) == 2 and text[1] == ":" and text[0].isalpha():
        text = f"{text[0].upper()}:{os.sep}"
    return Path(os.path.abspath(text))


def _norm_key(path: Path) -> Path:
    return Path(os.path.normcase(os.path.abspath(str(path))))


def _is_same_or_child(candidate: Path, root: Path) -> bool:
    cand_k, root_k = _norm_key(candidate), _norm_key(root)
    return cand_k == root_k or root_k in cand_k.parents


def get_subst_mappings() -> dict[str, Path]:
    """Queries active Windows `subst` drive mappings (`X:` -> target Path) without shell=True."""
    if os.name != "nt":
        return {}
    mappings: dict[str, Path] = {}
    with suppress(OSError, subprocess.SubprocessError):
        proc = subprocess.run(["subst"], capture_output=True, text=True, timeout=3, check=False)
        for line in proc.stdout.splitlines() if proc.returncode == 0 else ():
            left, sep, right = line.partition("=>")
            token = left.strip().rstrip(":\\/").upper()
            if sep and len(token) == 1 and token.isalpha() and right.strip():
                mappings[f"{token}:"] = _normalize_path(right.strip())
    return mappings


def _resolve_backing_path(path: Path, subst_map: Mapping[str, Path]) -> Path:
    norm = _normalize_path(path)
    if os.name == "nt" and (drive := os.path.splitdrive(str(norm))[0].upper()) in subst_map:
        rel = str(norm)[len(drive) :].lstrip("\\/")
        return _normalize_path(subst_map[drive] / rel) if rel else subst_map[drive]
    with suppress(OSError):
        return _normalize_path(norm.resolve())
    return norm


def _win_known_folders() -> dict[str, Path]:
    s32, o32 = ctypes.WinDLL("shell32", use_last_error=True), ctypes.WinDLL("ole32", use_last_error=True)
    s32.SHGetKnownFolderPath.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.POINTER(ctypes.c_wchar_p)]
    o32.CoTaskMemFree.argtypes = [ctypes.c_void_p]
    out: dict[str, Path] = {}
    for name, guid_str, fl in _WIN_KF_GUIDS:
        ptr = ctypes.c_wchar_p()
        try:
            buf = (ctypes.c_ubyte * 16).from_buffer_copy(uuid.UUID(guid_str).bytes_le)
            if s32.SHGetKnownFolderPath(ctypes.byref(buf), fl, None, ctypes.byref(ptr)) == 0 and ptr.value:
                out[name] = _normalize_path(ptr.value)
        finally:
            if ptr:
                o32.CoTaskMemFree(ptr)
    return out


def _collect_host_collision_roots(subst_map: Mapping[str, Path], scopes: list[Path]) -> tuple[list[Path], list[Path], tuple[Path, ...]]:
    forbidden, user_homes, temp_roots = [], [], []
    k32 = ctypes.WinDLL("kernel32", use_last_error=True) if os.name == "nt" else None
    win_kf = _win_known_folders() if os.name == "nt" else {}
    def _is_host_env(p: Path) -> bool:
        if any(_is_same_or_child(p, sc) for sc in scopes):
            return False
        drv = os.path.splitdrive(str(p))[0].upper()
        return k32 is None or (bool(drv) and drv not in subst_map and k32.GetDriveTypeW(f"{drv}{os.sep}") == _DRIVE_FIXED)
    if k32 is not None:
        mask = int(k32.GetLogicalDrives())
        for idx, ch in enumerate(string.ascii_uppercase):
            tag, drv = f"{ch}:", Path(f"{ch}:{os.sep}")
            if (mask & (1 << idx)) and tag not in subst_map and k32.GetDriveTypeW(str(drv)) == _DRIVE_FIXED:
                forbidden.extend(_normalize_path(c) for f in ("Programs", "Projects") if (c := drv / f).exists())
    for k, evs in (("Windows", ("SystemRoot", "WINDIR")), ("ProgramFiles", ("ProgramFiles",)), ("ProgramFilesX86", ("ProgramFiles(x86)",)), ("ProgramData", ("ProgramData",))):
        if val := win_kf.get(k) or next((_normalize_path(e) for v in evs if (e := os.environ.get(v))), None):
            forbidden.append(val)
    la = win_kf.get("LocalAppData") or next((p for v in (os.environ.get("LOCALAPPDATA"),) if v and _is_host_env(p := _normalize_path(v))), None)
    ra = win_kf.get("RoamingAppData") or next((p for v in (os.environ.get("APPDATA"),) if v and _is_host_env(p := _normalize_path(v))), None)
    if la is not None:
        user_homes.append(la)
        forbidden.append(la / "Programs")
        temp_roots.append(la / "Temp")
    if ra is not None:
        forbidden.append(ra)
    homes = [win_kf["Profile"]] if "Profile" in win_kf else [p for raw in (Path.home(), os.environ.get("USERPROFILE"), os.environ.get("HOME")) if raw and _is_host_env(p := _normalize_path(raw))]
    for h in homes:
        user_homes.append(h)
        forbidden.extend((win_kf.get("Desktop") or (h / "Desktop"), win_kf.get("Documents") or (h / "Documents")))
    with suppress(OSError):
        if not any(_is_same_or_child(st := _normalize_path(tempfile.gettempdir()), sc) for sc in scopes):
            temp_roots.append(st)
    return forbidden, user_homes, tuple(t for t in dict.fromkeys(temp_roots) if not any(_is_same_or_child(t, f) for f in forbidden))


def _validate_not_host_collision(label: str, candidate: Path, subst_map: Mapping[str, Path], host_roots: tuple[list[Path], list[Path], tuple[Path, ...]]) -> Path:
    forbidden, user_homes, temp_roots = host_roots
    backing = _resolve_backing_path(candidate, subst_map)
    for probe in (candidate, backing):
        for host_root in forbidden:
            if _is_same_or_child(probe, host_root) or _is_same_or_child(host_root, probe):
                raise SandboxContainmentError(f"Fail-Closed Sandbox Host Collision ({label}): '{probe}' collides with '{host_root}'.")
        for user_root in user_homes:
            if _is_same_or_child(user_root, probe) or (_is_same_or_child(probe, user_root) and not any(_is_same_or_child(probe, t) for t in temp_roots)):
                raise SandboxContainmentError(f"Fail-Closed Sandbox Host Collision ({label}): '{probe}' collides with user root '{user_root}'.")
    return backing


def resolve_sandbox_environment(env_override: Mapping[str, str | None] | None = None) -> SandboxResolution:
    """Resolves Stage 2 4-drive sandbox state strictly from process env (never auto-reading .secrets/.env)."""
    env: Mapping[str, str | None] = os.environ if env_override is None else env_override
    iso_val, raw_root = _lookup_env(env, "APP_ISOLATED_TEST"), _lookup_env(env, "FOUNDATION_SANDBOX_ROOT")
    if (iso_val is not None and iso_val.lower() in _TRUTHY) or not raw_root:
        return SandboxResolution(is_sandbox_active=False)
    subst_map, sandbox_root = get_subst_mappings(), _normalize_path(raw_root)
    raw_c = _lookup_env(env, "SANDBOX_C_DRIVE")
    c_norm = _normalize_path(raw_c) if raw_c else None
    raw_profile = _lookup_env(env, "FOUNDATION_SANDBOX_PROFILE_DIR")
    profile_dir = _normalize_path(raw_profile) if raw_profile else ((c_norm or (sandbox_root / "C")) / "Users" / "Sandbox")
    scopes: list[Path] = []
    for p in [profile_dir, *(_normalize_path(v) for r, _ in _DRIVE_ROLES if (v := _lookup_env(env, r)))]:
        scopes.extend((p, _resolve_backing_path(p, subst_map)))
    host_roots = _collect_host_collision_roots(subst_map, scopes)
    backings = [_validate_not_host_collision("FOUNDATION_SANDBOX_ROOT", sandbox_root, subst_map, host_roots)]
    drives: dict[str, Path | None] = {}
    mounts: list[SandboxMountSpec] = []
    for env_key, field_name in _DRIVE_ROLES:
        norm_drive = _normalize_path(raw_d) if (raw_d := _lookup_env(env, env_key)) else None
        backing = _validate_not_host_collision(env_key, norm_drive, subst_map, host_roots) if norm_drive else None
        if backing is not None:
            backings.append(backing)
        drives[field_name] = norm_drive
        mounts.append(SandboxMountSpec(env_key, (os.path.splitdrive(str(norm_drive))[0] or str(norm_drive)) if norm_drive else "", backing, norm_drive is not None))
    backings.append(_validate_not_host_collision("FOUNDATION_SANDBOX_PROFILE_DIR", profile_dir, subst_map, host_roots))
    return SandboxResolution(
        is_sandbox_active=True, sandbox_root=sandbox_root, system_drive_root=drives["system_drive_root"],
        storage_drive_root=drives["storage_drive_root"], removable_drive_root=drives["removable_drive_root"],
        disc_drive_root=drives["disc_drive_root"], sandbox_profile_dir=profile_dir,
        registry_hive_path=sandbox_root / "registry" / "registry.hive.json",
        registry_baseline_path=sandbox_root / "registry" / "registry.baseline.json",
        registry_lock_path=sandbox_root / "locks" / "registry.hive.lock",
        mounts=tuple(mounts), backing_roots=tuple(dict.fromkeys(backings)),
    )


def _build_known_folder_map(profile: Path, local_app: Path, roaming_app: Path, prog_files: Path, prog_x86: Path, common_app: Path, windows: Path) -> dict[str, Path]:
    start_menu = roaming_app / "Microsoft" / "Windows" / "Start Menu"
    programs = start_menu / "Programs"
    return {
        "UserProfile": profile, "LocalApplicationData": local_app, "ApplicationData": roaming_app,
        "Desktop": profile / "Desktop", "DesktopDirectory": profile / "Desktop",
        "MyDocuments": profile / "Documents", "Personal": profile / "Documents",
        "StartMenu": start_menu, "Programs": programs, "Startup": programs / "Startup",
        "ProgramFiles": prog_files, "ProgramFilesX86": prog_x86, "CommonApplicationData": common_app, "Windows": windows,
    }


def _host_known_folders(env: Mapping[str, str | None]) -> dict[str, Path]:
    profile = _normalize_path(_lookup_env(env, "USERPROFILE") or _lookup_env(env, "HOME") or Path.home())
    sys_drive = _normalize_path(_lookup_env(env, "SystemDrive") or profile.anchor or os.sep)
    return _build_known_folder_map(
        profile, _normalize_path(_lookup_env(env, "LOCALAPPDATA") or (profile / "AppData" / "Local")),
        _normalize_path(_lookup_env(env, "APPDATA") or (profile / "AppData" / "Roaming")),
        _normalize_path(_lookup_env(env, "ProgramFiles") or (sys_drive / "Program Files")),
        _normalize_path(_lookup_env(env, "ProgramFiles(x86)") or (sys_drive / "Program Files (x86)")),
        _normalize_path(_lookup_env(env, "ProgramData") or (sys_drive / "ProgramData")),
        _normalize_path(_lookup_env(env, "SystemRoot") or _lookup_env(env, "WINDIR") or (sys_drive / "Windows")),
    )


def resolve_known_folders(resolution: SandboxResolution | None = None, env_override: Mapping[str, str | None] | None = None) -> dict[str, Path]:
    """Resolves Windows SpecialFolder-equivalent paths into the sandbox when active, or host OS paths otherwise."""
    res = resolution if resolution is not None else resolve_sandbox_environment(env_override)
    if not res.is_sandbox_active or res.sandbox_root is None:
        return _host_known_folders(os.environ if env_override is None else env_override)
    c_root = res.system_drive_root or (res.sandbox_root / "C")
    profile = res.sandbox_profile_dir or (c_root / "Users" / "Sandbox")
    return _build_known_folder_map(profile, profile / "AppData" / "Local", profile / "AppData" / "Roaming", c_root / "Program Files", c_root / "Program Files (x86)", c_root / "ProgramData", c_root / "Windows")


def get_known_folder(folder_name: str, resolution: SandboxResolution | None = None, env_override: Mapping[str, str | None] | None = None) -> Path:
    for key, path in resolve_known_folders(resolution, env_override).items():
        if key.lower() == folder_name.strip().lower():
            return path
    raise KeyError(f"Unknown folder name '{folder_name}'.")


def is_path_within_sandbox(candidate_path: str | Path, resolution: SandboxResolution | None = None, env_override: Mapping[str, str | None] | None = None) -> bool:
    """Returns True iff sandbox is active and both logical and resolved paths stay within sandbox/drive roots."""
    res = resolution if resolution is not None else resolve_sandbox_environment(env_override)
    if not res.is_sandbox_active:
        return False
    allowed = tuple(dict.fromkeys(r for r in (res.sandbox_root, res.system_drive_root, res.storage_drive_root, res.removable_drive_root, res.disc_drive_root, res.sandbox_profile_dir, *res.backing_roots) if r is not None))
    logical = _normalize_path(candidate_path)
    return any(_is_same_or_child(logical, r) for r in allowed) and any(_is_same_or_child(_resolve_backing_path(logical, get_subst_mappings()), r) for r in allowed)


def assert_sandbox_path_if_active(candidate_path: str | Path, resolution: SandboxResolution | None = None, env_override: Mapping[str, str | None] | None = None) -> Path:
    """When sandbox is active, fails closed (exit_code=3) if candidate_path escapes sandbox roots."""
    res = resolution if resolution is not None else resolve_sandbox_environment(env_override)
    normalized = _normalize_path(candidate_path)
    if res.is_sandbox_active and not is_path_within_sandbox(normalized, res):
        raise SandboxContainmentError(f"Fail-Closed Sandbox Containment Violation: '{normalized}' escapes '{res.sandbox_root}'.")
    return normalized


def map_host_path_to_sandbox(host_path: str | Path, resolution: SandboxResolution | None = None, env_override: Mapping[str, str | None] | None = None) -> Path:
    """Maps a host known-folder path into the active sandbox, or returns it normalized when inactive."""
    res = resolution if resolution is not None else resolve_sandbox_environment(env_override)
    normalized = _normalize_path(host_path)
    if not res.is_sandbox_active or is_path_within_sandbox(normalized, res):
        return normalized
    host_folders, sandbox_folders = _host_known_folders(os.environ if env_override is None else env_override), resolve_known_folders(res, env_override)
    for key in ("Startup", "Programs", "StartMenu", "LocalApplicationData", "ApplicationData", "DesktopDirectory", "MyDocuments", "ProgramFilesX86", "ProgramFiles", "CommonApplicationData", "Windows", "UserProfile"):
        if _is_same_or_child(normalized, host_folders[key]):
            rel_parts = normalized.parts[len(host_folders[key].parts) :]
            return sandbox_folders[key].joinpath(*rel_parts) if rel_parts else sandbox_folders[key]
    raise SandboxContainmentError(f"Fail-Closed Sandbox Containment Violation: cannot map '{normalized}' into active sandbox.")
