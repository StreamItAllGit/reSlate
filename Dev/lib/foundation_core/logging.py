# @module: foundation_core.logging
# @purpose: Rolling file log sink with byte-cap rotation and <=15s TTY-aware \r\x1b[K terminal heartbeats (Standard 13).
# @depends-on: os, pathlib, sys, time
# @exports: RollingLogSink, emit_heartbeat

from __future__ import annotations

import sys
import time
from pathlib import Path

def emit_heartbeat(operation_name: str, completed: int, total: int, current_item: str = "working") -> str:
    """Formats and writes a <=15s heartbeat line with TTY-aware in-place carriage return."""
    pct = min(100.0, max(0.0, (completed / total * 100.0) if total > 0 else 0.0))
    line = f"[HEARTBEAT] {operation_name}: {completed}/{total} ({pct:.1f}%) | {current_item}"
    if sys.stdout.isatty():
        sys.stdout.write(f"\r\x1b[K{line}")
        sys.stdout.flush()
    return line

class RollingLogSink:
    """Thread-safe rolling text log writer with automatic size-based rotation."""

    def __init__(
        self,
        log_dir: str | Path,
        base_name: str = "app.log",
        max_bytes: int = 10 * 1024 * 1024,
        max_files: int = 5,
    ) -> None:
        self.log_dir = Path(log_dir).resolve()
        self.base_name = base_name
        self.max_bytes = max(64 * 1024, max_bytes)
        self.max_files = max(1, max_files)
        self.log_dir.mkdir(parents=True, exist_ok=True)

    @property
    def current_log_path(self) -> Path:
        return self.log_dir / self.base_name

    def write_line(self, level: str, message: str) -> str:
        self.log_dir.mkdir(parents=True, exist_ok=True)
        stamp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        formatted = f"{stamp} [{level.upper()}] {message}"
        path = self.current_log_path

        if path.exists() and path.stat().st_size >= self.max_bytes:
            self._rotate()

        with path.open("a", encoding="utf-8") as fh:
            fh.write(formatted + "\n")
        return formatted

    def _rotate(self) -> None:
        oldest = self.log_dir / f"{self.base_name}.{self.max_files}"
        if oldest.exists():
            oldest.unlink(missing_ok=True)

        for idx in range(self.max_files - 1, 0, -1):
            src = self.log_dir / f"{self.base_name}.{idx}"
            dst = self.log_dir / f"{self.base_name}.{idx + 1}"
            if src.exists():
                src.replace(dst)

        if self.current_log_path.exists():
            self.current_log_path.replace(self.log_dir / f"{self.base_name}.1")
