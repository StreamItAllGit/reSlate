# @module: foundation_core.settings
# @purpose: 3-file JSON settings split (app.settings.json, paths.settings.json, integrations.settings.json), atomic .tmp -> .bak writes, and config_history/ rotation.
# @depends-on: json, os, pathlib, shutil, time
# @exports: ThreeFileSettingsStore

from __future__ import annotations

import json
import shutil
import time
from pathlib import Path
from typing import Any

class ThreeFileSettingsStore:
    """Manages atomic 3-file JSON settings persistence and config_history/ rotation (Standard 05)."""

    FILE_NAMES = (
        "app.settings.json",
        "paths.settings.json",
        "integrations.settings.json",
    )

    def __init__(self, data_dir: str | Path, max_snapshots: int = 10) -> None:
        self.data_dir = Path(data_dir).resolve()
        self.history_dir = self.data_dir / "config_history"
        self.max_snapshots = max(1, max_snapshots)

    def ensure_directories(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.history_dir.mkdir(parents=True, exist_ok=True)

    def load_file_with_recovery(self, filename: str, default_payload: dict[str, Any]) -> dict[str, Any]:
        self.ensure_directories()
        target = self.data_dir / filename
        bak = self.data_dir / f"{filename}.bak"

        for candidate in (target, bak):
            if candidate.exists():
                try:
                    data = json.loads(candidate.read_text(encoding="utf-8"))
                    if isinstance(data, dict):
                        if candidate == bak:
                            shutil.copy2(bak, target)
                        return data
                except Exception:
                    continue

        self.save_file_atomic(filename, default_payload, create_snapshot=False)
        return dict(default_payload)

    def save_file_atomic(
        self,
        filename: str,
        payload: dict[str, Any],
        create_snapshot: bool = True,
    ) -> Path:
        self.ensure_directories()
        target = self.data_dir / filename
        tmp = self.data_dir / f"{filename}.tmp"
        bak = self.data_dir / f"{filename}.bak"

        serialized = json.dumps(payload, indent=2, sort_keys=True) + "\n"
        tmp.write_text(serialized, encoding="utf-8")

        if target.exists():
            shutil.copy2(target, bak)
            if create_snapshot:
                stamp = time.strftime("%Y%m%d_%H%M%S", time.gmtime())
                snap = self.history_dir / f"{filename}.{stamp}.json"
                shutil.copy2(target, snap)
                self._prune_snapshots(filename)

        tmp.replace(target)
        return target

    def _prune_snapshots(self, filename: str) -> None:
        snaps = sorted(
            self.history_dir.glob(f"{filename}.*.json"),
            key=lambda p: p.stat().st_mtime,
            reverse=True,
        )
        for stale in snaps[self.max_snapshots :]:
            try:
                stale.unlink()
            except OSError:
                pass
