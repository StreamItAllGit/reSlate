# @module: foundation_core.pseudo_registry
# @purpose: Cross-process atomic JSON pseudo-registry (HKCU/HKLM/HKCR, Standard 18 schema) with PidSafeLockEnvelope + OS lock coordination.
# @depends-on: contextlib, ctypes, datetime, enum, json, os, pathlib, re, shutil, socket, threading, time, typing, .locks, .sandbox_seam
# @exports: REGISTRY_HIVE_SCHEMA_URI, REGISTRY_HIVE_SCHEMA_VERSION, PlatformRegistryHive, PlatformRegistryValueKind, JsonHivePlatformRegistry

from __future__ import annotations

from contextlib import contextmanager, suppress
import ctypes
from datetime import datetime, timedelta, timezone
from enum import StrEnum
import json
import os
from pathlib import Path
import re
import shutil
import socket
import threading
import time
from typing import Any, Iterator

from .locks import PidSentinelLock
from .sandbox_seam import SandboxResolution, resolve_sandbox_environment

REGISTRY_HIVE_SCHEMA_URI: str = "https://schemas.local/registry-hive.v1.json"
REGISTRY_HIVE_SCHEMA_VERSION: int = 1
_SPLIT_SEP_RE = re.compile(r"[\\/]+")
_INPROC_GUARD = threading.Lock()
_INPROC_LOCKS: dict[Path, threading.Lock] = {}

class PlatformRegistryHive(StrEnum):
    CURRENT_USER, LOCAL_MACHINE, CLASSES_ROOT = "HKCU", "HKLM", "HKCR"

class PlatformRegistryValueKind(StrEnum):
    STRING, EXPAND_STRING, MULTI_STRING, DWORD, QWORD, BINARY = "REG_SZ", "REG_EXPAND_SZ", "REG_MULTI_SZ", "REG_DWORD", "REG_QWORD", "REG_BINARY"

_HIVE_MAP = {"HKCU": "HKCU", "HKEYCURRENTUSER": "HKCU", "CURRENTUSER": "HKCU", "HKLM": "HKLM", "HKEYLOCALMACHINE": "HKLM", "LOCALMACHINE": "HKLM", "HKCR": "HKCR", "HKEYCLASSESROOT": "HKCR", "CLASSESROOT": "HKCR"}
_KIND_MAP = {k.name.replace("_", ""): k.value for k in PlatformRegistryValueKind} | {k.value.replace("_", ""): k.value for k in PlatformRegistryValueKind}

def _utc_iso_ms(dt: datetime | None = None) -> str:
    cur = (dt or datetime.now(timezone.utc)).astimezone(timezone.utc)
    return cur.strftime("%Y-%m-%dT%H:%M:%S.") + f"{cur.microsecond // 1000:03d}Z"

def _parse_iso_utc(raw: Any) -> datetime | None:
    with suppress(ValueError):
        if isinstance(raw, str) and (s := raw.strip()):
            dt = datetime.fromisoformat((s[:-1] + "+00:00") if s.endswith("Z") else s)
            return dt if dt.tzinfo is not None else dt.replace(tzinfo=timezone.utc)
    return None

def _get_process_state(pid: int) -> tuple[bool, datetime | None]:
    if pid <= 0:
        return (False, None)
    if os.name != "nt":
        return (PidSentinelLock.is_pid_alive(pid), None)
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    if not (handle := k32.OpenProcess(0x1000, False, pid)):
        return (ctypes.get_last_error() == 5, None)
    try:
        code, ct, et, kt, ut = ctypes.c_uint32(), ctypes.c_uint64(), ctypes.c_uint64(), ctypes.c_uint64(), ctypes.c_uint64()
        if not k32.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:
            return (False, None)
        ok = k32.GetProcessTimes(handle, ctypes.byref(ct), ctypes.byref(et), ctypes.byref(kt), ctypes.byref(ut))
        return (True, datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=max(0, (ct.value - 116444736000000000) // 10)) if ok else None)
    finally:
        k32.CloseHandle(handle)

def _normalize_target(hive: str | PlatformRegistryHive, subkey: str) -> tuple[str, str]:
    if (hive_name := _HIVE_MAP.get(str(hive).strip().upper().replace("_", ""))) is None:
        raise ValueError(f"Unsupported registry hive '{hive}'. Expected HKCU, HKLM, or HKCR.")
    parts = [p for p in _SPLIT_SEP_RE.split(subkey.strip()) if p]
    return hive_name, "\\".join(parts[1:] if parts and _HIVE_MAP.get(parts[0].upper().replace("_", "")) == hive_name else parts)

def _encode_value(value: Any, kind: str | PlatformRegistryValueKind | None) -> tuple[str, Any]:
    k = _KIND_MAP.get(str(kind).strip().upper().replace("_", ""), "") if kind is not None else ("REG_SZ" if isinstance(value, str) else "REG_BINARY" if isinstance(value, (bytes, bytearray, memoryview)) else "REG_MULTI_SZ" if isinstance(value, (list, tuple)) else ("REG_DWORD" if -0x80000000 <= value <= 0xFFFFFFFF else "REG_QWORD") if isinstance(value, int) and not isinstance(value, bool) else "")
    if k in ("REG_SZ", "REG_EXPAND_SZ") and isinstance(value, str):
        return k, value
    if k == "REG_MULTI_SZ" and isinstance(value, (list, tuple)) and all(isinstance(x, str) for x in value):
        return k, [str(x) for x in value]
    if k in ("REG_DWORD", "REG_QWORD") and isinstance(value, int) and not isinstance(value, bool) and ((-0x80000000 <= value <= 0xFFFFFFFF) if k == "REG_DWORD" else (-0x8000000000000000 <= value <= 0xFFFFFFFFFFFFFFFF)):
        return k, int(value)
    if k == "REG_BINARY" and isinstance(value, (bytes, bytearray, memoryview, str)):
        return k, (bytes.fromhex(value.strip()) if isinstance(value, str) else bytes(value)).hex()
    raise ValueError(f"Invalid registryValueKind/type: kind={kind!r}, type={type(value).__name__}")

def _os_lock_op(fd: int, lock: bool) -> None:
    if os.name == "nt":
        import msvcrt
        os.lseek(fd, 0x40000000, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_NBLCK if lock else msvcrt.LK_UNLCK, 1)
        return
    import fcntl
    fcntl.flock(fd, (fcntl.LOCK_EX | fcntl.LOCK_NB) if lock else fcntl.LOCK_UN)

def _is_lock_held_by_other(lock_path: Path) -> bool:
    with suppress(OSError, ValueError, TypeError):
        env = json.loads(raw) if lock_path.exists() and (raw := lock_path.read_text(encoding="utf-8").strip()) else {}
        pid, expires = int(env.get("pid", -1)), _parse_iso_utc(env.get("expiresAtUtc"))
        if pid <= 0 or pid == os.getpid() or (expires is not None and expires < datetime.now(timezone.utc)):
            return False
        if (host := str(env.get("hostName", ""))) and host.lower() != socket.gethostname().lower():
            return True
        alive, actual_start = _get_process_state(pid)
        expected_start = _parse_iso_utc(env.get("processStartTimeUtc"))
        return alive and not (expected_start and actual_start and abs((actual_start - expected_start).total_seconds()) > 2.0)
    return False

class JsonHivePlatformRegistry:
    """Atomic cross-process JSON pseudo-registry matching Standard 18 registry.hive.json schema."""

    def __init__(self, hive_path: str | Path, sandbox_root: str | Path | None = None, lock_path: str | Path | None = None, app_id: str = "foundation_core", lock_timeout_seconds: float = 10.0) -> None:
        self.hive_path = Path(os.path.abspath(str(hive_path)))
        self.sandbox_root = Path(os.path.abspath(str(sandbox_root))) if sandbox_root is not None else (self.hive_path.parent.parent if self.hive_path.parent.name.lower() == "registry" else self.hive_path.parent)
        self.lock_path = Path(os.path.abspath(str(lock_path))) if lock_path is not None else (self.sandbox_root / "locks" / "registry.hive.lock")
        self.app_id, self.lock_timeout_seconds = app_id, max(0.5, lock_timeout_seconds)

    @classmethod
    def from_sandbox(cls, resolution: SandboxResolution | None = None, app_id: str = "foundation_core") -> JsonHivePlatformRegistry:
        res = resolution or resolve_sandbox_environment()
        if not res.is_sandbox_active or res.registry_hive_path is None:
            raise RuntimeError("Stage 2 sandbox is not active; cannot bind JsonHivePlatformRegistry.")
        return cls(res.registry_hive_path, res.sandbox_root, res.registry_lock_path, app_id)

    @contextmanager
    def _acquire_lock(self, operation: str) -> Iterator[None]:
        with _INPROC_GUARD:
            proc_lock = _INPROC_LOCKS.setdefault(Path(os.path.normcase(os.path.realpath(str(self.lock_path)))), threading.Lock())
        if not proc_lock.acquire(timeout=self.lock_timeout_seconds):
            raise TimeoutError(f"Timed out waiting for in-process lock '{self.lock_path}'.")
        fh: Any = None
        try:
            deadline, backoff = time.monotonic() + self.lock_timeout_seconds, 0.015
            self.lock_path.parent.mkdir(parents=True, exist_ok=True)
            while True:
                if not _is_lock_held_by_other(self.lock_path):
                    cand, locked = os.fdopen(os.open(str(self.lock_path), os.O_RDWR | os.O_CREAT, 0o666), "r+b", buffering=0), False
                    try:
                        _os_lock_op(cand.fileno(), True)
                        locked = True
                        if _is_lock_held_by_other(self.lock_path):
                            raise BlockingIOError("Active external holder.")
                        now, (_, p_start) = datetime.now(timezone.utc), _get_process_state(os.getpid())
                        env = {"schemaVersion": 1, "appId": self.app_id, "pid": os.getpid(), "processStartTimeUtc": _utc_iso_ms(p_start), "hostName": socket.gethostname(), "operation": operation, "acquiredAtUtc": _utc_iso_ms(now), "heartbeatAtUtc": _utc_iso_ms(now), "expiresAtUtc": _utc_iso_ms(now + timedelta(minutes=15))}
                        cand.seek(0)
                        cand.truncate(0)
                        cand.write((json.dumps(env, indent=2) + "\n").encode("utf-8"))
                        os.fsync(cand.fileno())
                        fh = cand
                        break
                    except OSError:
                        if locked:
                            _os_lock_op(cand.fileno(), False)
                        cand.close()
                if (rem := deadline - time.monotonic()) <= 0:
                    raise TimeoutError(f"Timed out acquiring '{self.lock_path}'.")
                time.sleep(min(backoff, max(0.005, rem)) + ((time.monotonic_ns() % 7) * 0.001))
                backoff = min(0.25, backoff * 1.6)
            yield
        finally:
            if fh is not None:
                with suppress(OSError):
                    fh.seek(0)
                    fh.truncate(0)
                    _os_lock_op(fh.fileno(), False)
                fh.close()
                with suppress(OSError):
                    self.lock_path.unlink(missing_ok=True)
            proc_lock.release()

    def _normalize_doc(self, raw: dict[str, Any] | None = None) -> dict[str, Any]:
        src = raw or {}
        hives = {h: (dict(v) if isinstance((v := src["registryHives"].get(h)), dict) else {}) for h in ("HKCU", "HKLM", "HKCR")} if isinstance(src.get("registryHives"), dict) else {"HKCU": {}, "HKLM": {}, "HKCR": {}}
        return {"$schema": str(src.get("$schema") or REGISTRY_HIVE_SCHEMA_URI), "schemaVersion": int(src.get("schemaVersion") or REGISTRY_HIVE_SCHEMA_VERSION), "sandboxRoot": str(src.get("sandboxRoot") or self.sandbox_root), "updatedAtUtc": str(src.get("updatedAtUtc") or _utc_iso_ms()), "registryHives": hives}

    def _read_doc_unlocked(self) -> dict[str, Any]:
        for cand in (self.hive_path, self.hive_path.with_name(f"{self.hive_path.name}.bak")):
            with suppress(OSError, json.JSONDecodeError, ValueError):
                if isinstance((parsed := json.loads(cand.read_text(encoding="utf-8"))), dict) and isinstance(parsed.get("registryHives"), dict):
                    return self._normalize_doc(parsed)
        return self._normalize_doc()

    def _write_doc_unlocked(self, doc: dict[str, Any]) -> Path:
        self.hive_path.parent.mkdir(parents=True, exist_ok=True)
        normalized = self._normalize_doc(doc) | {"sandboxRoot": str(self.sandbox_root), "updatedAtUtc": _utc_iso_ms()}
        tmp_path = self.hive_path.with_name(f"{self.hive_path.name}.{os.getpid()}.{threading.get_ident()}.tmp")
        try:
            with tmp_path.open("w", encoding="utf-8", newline="\n") as fh:
                fh.write(json.dumps(normalized, indent=2) + "\n")
                fh.flush()
                os.fsync(fh.fileno())
            if self.hive_path.exists():
                shutil.copy2(self.hive_path, self.hive_path.with_name(f"{self.hive_path.name}.bak"))
            os.replace(tmp_path, self.hive_path)
        finally:
            tmp_path.unlink(missing_ok=True)
        return self.hive_path

    @staticmethod
    def _ci_key(mapping: dict[str, Any], target: str) -> str | None:
        return next((k for k in mapping if k.lower() == target.lower()), None)

    def _read_hive(self, hive: str | PlatformRegistryHive, subkey: str) -> tuple[str, dict[str, Any]]:
        hive_name, norm_sub = _normalize_target(hive, subkey)
        return norm_sub, self.load_document()["registryHives"][hive_name]

    @contextmanager
    def _edit_hive(self, op: str, hive: str | PlatformRegistryHive, subkey: str) -> Iterator[tuple[str, dict[str, Any], list[bool]]]:
        hive_name, norm_sub = _normalize_target(hive, subkey)
        with self._acquire_lock(op):
            doc, dirty = self._read_doc_unlocked(), [False]
            yield norm_sub, doc["registryHives"][hive_name], dirty
            if dirty[0]:
                self._write_doc_unlocked(doc)

    def ensure_initialized(self) -> Path:
        with self._acquire_lock("registry-init"):
            return self.hive_path if self.hive_path.exists() else self._write_doc_unlocked(self._normalize_doc())

    def load_document(self) -> dict[str, Any]:
        with self._acquire_lock("registry-read"):
            return self._read_doc_unlocked()

    def seed_from_baseline(self, baseline_path: str | Path, overwrite: bool = False) -> Path:
        if not isinstance((raw := json.loads(Path(os.path.abspath(str(baseline_path))).read_text(encoding="utf-8"))), dict):
            raise ValueError("Baseline registry must be a JSON object.")
        with self._acquire_lock("registry-seed"):
            return self.hive_path if (self.hive_path.exists() and not overwrite) else self._write_doc_unlocked(raw)

    def key_exists(self, hive: str | PlatformRegistryHive, subkey: str) -> bool:
        norm_sub, hm = self._read_hive(hive, subkey)
        prefix = norm_sub.lower() + "\\" if norm_sub else ""
        return self._ci_key(hm, norm_sub) is not None or (bool(hm) if not prefix else any(k.lower().startswith(prefix) for k in hm))

    def ensure_key(self, hive: str | PlatformRegistryHive, subkey: str) -> None:
        with self._edit_hive("registry-ensure-key", hive, subkey) as (ns, hm, dirty):
            if self._ci_key(hm, ns) is None:
                hm[ns], dirty[0] = {}, True

    def get_value_entry(self, hive: str | PlatformRegistryHive, subkey: str, value_name: str = "") -> dict[str, Any] | None:
        ns, hm = self._read_hive(hive, subkey)
        vm = hm[s] if (s := self._ci_key(hm, ns)) is not None and isinstance(hm[s], dict) else None
        return dict(vm[v]) if vm is not None and (v := self._ci_key(vm, value_name or "")) is not None and isinstance(vm[v], dict) else None

    def get_value(self, hive: str | PlatformRegistryHive, subkey: str, value_name: str = "", default: Any = None) -> Any:
        if (entry := self.get_value_entry(hive, subkey, value_name)) is None:
            return default
        data = entry.get("registryValueData")
        return bytes.fromhex(data) if str(entry.get("registryValueKind", "")).upper() == "REG_BINARY" and isinstance(data, str) else data

    def set_value(self, hive: str | PlatformRegistryHive, subkey: str, value_name: str, value: Any, kind: str | PlatformRegistryValueKind | None = None) -> None:
        val_key, (norm_kind, encoded) = value_name or "", _encode_value(value, kind)
        with self._edit_hive("registry-set-value", hive, subkey) as (ns, hm, dirty):
            val_map = hm.setdefault(self._ci_key(hm, ns) or ns, {})
            if (old := self._ci_key(val_map, val_key)) is not None and old != val_key:
                del val_map[old]
            val_map[val_key], dirty[0] = {"registryValueKind": norm_kind, "registryValueData": encoded}, True

    def delete_value(self, hive: str | PlatformRegistryHive, subkey: str, value_name: str = "") -> bool:
        with self._edit_hive("registry-delete-value", hive, subkey) as (ns, hm, dirty):
            vm = hm[s] if (s := self._ci_key(hm, ns)) is not None and isinstance(hm[s], dict) else None
            if vm is None or (vk := self._ci_key(vm, value_name or "")) is None:
                return False
            del vm[vk]
            dirty[0] = True
            return True

    def delete_key(self, hive: str | PlatformRegistryHive, subkey: str, recursive: bool = True) -> bool:
        with self._edit_hive("registry-delete-key", hive, subkey) as (ns, hm, dirty):
            prefix = ns.lower() + "\\" if ns else ""
            children = [k for k in hm if (k.lower().startswith(prefix) if prefix else k != "")]
            if children and not recursive:
                raise OSError(f"Registry subkey '{ns}' has child subkeys; pass recursive=True.")
            if not (targets := (([ex] if (ex := self._ci_key(hm, ns)) is not None else []) + children)):
                return False
            for k in targets:
                hm.pop(k, None)
            dirty[0] = True
            return True

    def list_subkey_names(self, hive: str | PlatformRegistryHive, subkey: str = "") -> list[str]:
        norm_sub, hm = self._read_hive(hive, subkey)
        prefix, seen = (norm_sub.lower() + "\\" if norm_sub else ""), {}
        for k in hm:
            if k and (not prefix or k.lower().startswith(prefix)) and (parts := [p for p in _SPLIT_SEP_RE.split(k[len(prefix) :] if prefix else k) if p]):
                seen.setdefault(parts[0].lower(), parts[0])
        return sorted(seen.values(), key=str.lower)

    def list_value_names(self, hive: str | PlatformRegistryHive, subkey: str) -> list[str]:
        norm_sub, hm = self._read_hive(hive, subkey)
        return list(hm[found].keys()) if (found := self._ci_key(hm, norm_sub)) is not None and isinstance(hm[found], dict) else []
