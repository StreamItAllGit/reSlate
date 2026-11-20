# @module: Foundation.Tools.notion_task_helper
# @purpose: Shared helper for formatting Notion Unified Task Backbone payloads and 4-doc Manual Shelf sync manifests (Standard 16 §5).
# @depends-on: json, os, pathlib, time
# @exports: resolve_notion_ids, build_task_backbone_payload, build_manual_shelf_manifest

from __future__ import annotations

import json
import os
import time
from pathlib import Path
from typing import Any


def resolve_notion_ids(data_dir: str | Path | None = None) -> tuple[str, str]:
    """Resolves Notion Task Backbone and Projects Hub IDs from env vars or optional <dataDir>/notion.config.json."""
    task_id = os.environ.get("FOUNDATION_NOTION_TASK_BACKBONE_ID", "").strip()
    hub_id = os.environ.get("FOUNDATION_NOTION_PROJECTS_HUB_ID", "").strip()

    if (not task_id or not hub_id) and data_dir is not None:
        cfg_path = Path(data_dir) / "notion.config.json"
        if cfg_path.exists():
            try:
                cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
                task_id = task_id or str(cfg.get("taskBackboneDataSourceId", "")).strip()
                hub_id = hub_id or str(cfg.get("projectsHubPageId", "")).strip()
            except (OSError, json.JSONDecodeError):
                pass

    return task_id, hub_id


def build_task_backbone_payload(
    task_title: str,
    project_name: str,
    target_version: str,
    status: str = "To Do",
    priority: str = "High",
    data_dir: str | Path | None = None,
) -> dict[str, Any]:
    """Builds a standardized Notion Unified Task Backbone properties dictionary."""
    task_id, _ = resolve_notion_ids(data_dir)
    return {
        "dataSourceId": task_id,
        "properties": {
            "Task": f"[{target_version}] {task_title}",
            "Project": project_name,
            "Status": status,
            "Priority": priority,
            "UpdatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        },
    }


def build_manual_shelf_manifest(project_root: str | Path) -> dict[str, Any]:
    """Inspects the 4-doc Manual Shelf in Dev\\docs\\ and returns line/byte metrics for Notion sync."""
    root = Path(project_root).resolve()
    docs_dir = root / "Dev" / "docs"
    shelf_files = ("ARCHITECTURE.md", "USER_GUIDE.md", "OPERATIONS.md", "INTERFACES.md")
    metrics: dict[str, Any] = {}
    for name in shelf_files:
        p = docs_dir / name
        if p.exists():
            text = p.read_text(encoding="utf-8", errors="replace")
            metrics[name] = {
                "exists": True,
                "lines": len(text.splitlines()),
                "bytes": p.stat().st_size,
            }
        else:
            metrics[name] = {"exists": False, "lines": 0, "bytes": 0}
    return {"projectRoot": str(root), "manualShelf": metrics}


if __name__ == "__main__":
    task_id, hub_id = resolve_notion_ids(os.environ.get("FOUNDATION_DATA_DIR"))
    print(json.dumps({"taskBackboneId": task_id, "projectsHubId": hub_id}, indent=2))
