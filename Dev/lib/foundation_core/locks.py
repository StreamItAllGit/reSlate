# @module: foundation_core.locks
# @purpose: PID-verified .hklock sentinel lock manager with stale process detection and automatic cleanup (Standard 17).
# @depends-on: json, os, pathlib, socket, time
# @exports: PidSentinelLock

from __future__ import annotations

import json
import os
import socket
import time
from pathlib import Path

class PidSentinelLock:
    """Manages cross-process .hklock sentinel files with stale PID recovery."""

    def __init__(self, target_dir: str | Path, app_id: str, operation: str = "batch") -> None:
        self.target_dir = Path(target_dir).resolve()
        self.lock_path = self.target_dir / ".hklock"
        self.app_id = app_id
        self.operation = operation
        self._acquired = False

    @staticmethod
    def is_pid_alive(pid: int) -> bool:
        if pid <= 0:
            return False
        try:
            os.kill(pid, 0)
            return True
        except PermissionError:
            return True
        except OSError:
            return False

    def acquire(self) -> bool:
        self.target_dir.mkdir(parents=True, exist_ok=True)
        if self.lock_path.exists():
            try:
                existing = json.loads(self.lock_path.read_text(encoding="utf-8"))
                holder_pid = int(existing.get("pid", -1))
                if holder_pid != os.getpid() and self.is_pid_alive(holder_pid):
                    return False
                self.lock_path.unlink(missing_ok=True)
            except Exception:
                self.lock_path.unlink(missing_ok=True)

        payload = {
            "appId": self.app_id,
            "pid": os.getpid(),
            "host": socket.gethostname(),
            "operation": self.operation,
            "acquiredAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }
        self.lock_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        self._acquired = True
        return True

    def release(self) -> None:
        if self._acquired and self.lock_path.exists():
            try:
                self.lock_path.unlink(missing_ok=True)
            except OSError:
                pass
        self._acquired = False

    def __enter__(self) -> PidSentinelLock:
        if not self.acquire():
            raise RuntimeError(f"Directory '{self.target_dir}' is locked by an active sister-app process.")
        return self

    def __exit__(self, exc_type: object, exc_val: object, exc_tb: object) -> None:
        self.release()
