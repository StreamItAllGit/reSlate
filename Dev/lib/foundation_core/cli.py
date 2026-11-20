# @module: foundation_core.cli
# @purpose: Standard 02 & Standard 10 CLI flag parser (--data-dir, --config, --log-dir, --version, --help) with fail-closed path containment checks.
# @depends-on: argparse, dataclasses, pathlib
# @exports: StandardCliOptions, parse_standard_cli_flags, assert_path_contained

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

def assert_path_contained(candidate_path: str | Path, allowed_root: str | Path) -> Path:
    """Fail-closed containment check ensuring candidate_path resolves strictly inside allowed_root."""
    resolved_candidate = Path(candidate_path).resolve()
    resolved_root = Path(allowed_root).resolve()
    if resolved_candidate != resolved_root and resolved_root not in resolved_candidate.parents:
        raise ValueError(
            f"Fail-Closed Path Containment Violation: '{resolved_candidate}' escapes allowed root '{resolved_root}'."
        )
    return resolved_candidate

@dataclass(frozen=True)
class StandardCliOptions:
    data_dir: Path
    config_path: Path
    log_dir: Path
    extra_args: list[str]

def parse_standard_cli_flags(
    argv: Sequence[str] | None = None,
    default_root: str | Path = ".",
    app_name: str = "Application",
    app_version: str = "0.1.0",
) -> StandardCliOptions:
    base = Path(default_root).resolve()
    parser = argparse.ArgumentParser(prog=app_name, add_help=True)
    parser.add_argument("--version", action="version", version=f"{app_name} v{app_version}")
    parser.add_argument("--data-dir", type=Path, default=base / "data", help="Isolated data directory")
    parser.add_argument("--config", type=Path, default=None, help="Explicit primary config file path")
    parser.add_argument("--log-dir", type=Path, default=base / "logs", help="Isolated log directory")

    known, unknown = parser.parse_known_args(argv)
    data_dir = Path(known.data_dir).resolve()
    log_dir = Path(known.log_dir).resolve()
    config_path = (
        Path(known.config).resolve()
        if known.config is not None
        else (data_dir / "app.settings.json").resolve()
    )

    return StandardCliOptions(
        data_dir=data_dir,
        config_path=config_path,
        log_dir=log_dir,
        extra_args=list(unknown),
    )
