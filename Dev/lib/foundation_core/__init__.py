# @module: foundation_core
# @purpose: Python 3.12+ multi-module SDK parity with Foundation.Core (sandbox containment, 3-file settings, SQLite WAL + migrations, rolling logs, crash dumps, .hklock).
# @depends-on: .cli, .crash_dumper, .db, .locks, .logging, .settings
# @exports: SDK_VERSION, MODULE_VERSION, assert_path_contained, StandardCliOptions, parse_standard_cli_flags, open_wal_sqlite, SqliteSchemaMigrationRunner, recover_corrupt_sqlite, emit_heartbeat, RollingLogSink, ThreeFileSettingsStore, CrashDumper, PidSentinelLock

from __future__ import annotations

from .cli import StandardCliOptions, assert_path_contained, parse_standard_cli_flags
from .crash_dumper import CrashDumper
from .db import SqliteSchemaMigrationRunner, open_wal_sqlite, recover_corrupt_sqlite
from .locks import PidSentinelLock
from .logging import RollingLogSink, emit_heartbeat
from .settings import ThreeFileSettingsStore

SDK_VERSION: str = "0.3.0"
MODULE_VERSION: str = "0.1.0"

__all__ = [
    "SDK_VERSION",
    "MODULE_VERSION",
    "assert_path_contained",
    "StandardCliOptions",
    "parse_standard_cli_flags",
    "open_wal_sqlite",
    "SqliteSchemaMigrationRunner",
    "recover_corrupt_sqlite",
    "emit_heartbeat",
    "RollingLogSink",
    "ThreeFileSettingsStore",
    "CrashDumper",
    "PidSentinelLock",
]
