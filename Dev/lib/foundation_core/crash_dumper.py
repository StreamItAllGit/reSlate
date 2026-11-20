# @module: foundation_core.crash_dumper
# @purpose: Unhandled exception hook writing structured crash_YYYYMMDD_HHMMSS_fff.json diagnostics to --log-dir (Standard 13 & Standard 15).
# @depends-on: json, pathlib, platform, sys, threading, time, traceback
# @exports: CrashDumper

from __future__ import annotations

import json
import platform
import sys
import threading
import time
import traceback
from pathlib import Path
from types import TracebackType

class CrashDumper:
    """Writes deterministic JSON crash dumps into the configured log directory."""

    def __init__(self, log_dir: str | Path, app_name: str, app_version: str) -> None:
        self.log_dir = Path(log_dir).resolve()
        self.app_name = app_name
        self.app_version = app_version

    def write_crash_report(
        self,
        exc_type: type[BaseException],
        exc_value: BaseException,
        exc_tb: TracebackType | None,
        context_tag: str = "unhandled",
    ) -> Path:
        self.log_dir.mkdir(parents=True, exist_ok=True)
        now_ms = int((time.time() % 1) * 1000)
        stamp = time.strftime("%Y%m%d_%H%M%S", time.gmtime())
        crash_path = self.log_dir / f"crash_{stamp}_{now_ms:03d}.json"

        payload = {
            "schemaVersion": 1,
            "appName": self.app_name,
            "appVersion": self.app_version,
            "timestampUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "contextTag": context_tag,
            "exceptionType": exc_type.__name__,
            "message": str(exc_value),
            "stackTrace": "".join(traceback.format_exception(exc_type, exc_value, exc_tb)),
            "pythonVersion": platform.python_version(),
            "platform": platform.platform(),
        }
        crash_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        return crash_path

    def install_global_hooks(self) -> None:
        orig_excepthook = sys.excepthook

        def _handle_sys(
            exc_type: type[BaseException],
            exc_value: BaseException,
            exc_tb: TracebackType | None,
        ) -> None:
            try:
                self.write_crash_report(exc_type, exc_value, exc_tb, "sys.excepthook")
            except Exception:
                pass
            orig_excepthook(exc_type, exc_value, exc_tb)

        def _handle_thread(args: threading.ExceptHookArgs) -> None:
            if args.exc_type and args.exc_value:
                try:
                    self.write_crash_report(
                        args.exc_type,
                        args.exc_value,
                        args.exc_traceback,
                        f"thread:{getattr(args.thread, 'name', 'worker')}",
                    )
                except Exception:
                    pass

        sys.excepthook = _handle_sys
        threading.excepthook = _handle_thread
