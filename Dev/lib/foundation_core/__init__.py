# @module: foundation_core
# @purpose: Python 3.12+ multi-module SDK parity with Foundation.Core (path containment, 4-drive sandbox seam, atomic JSON pseudo-registry, 3-file settings, SQLite WAL, rolling logs, crash dumps, .hklock).
# @depends-on: .cli, .crash_dumper, .db, .locks, .logging, .pseudo_registry, .sandbox_seam, .settings
# @exports: SDK_VERSION, MODULE_VERSION, assert_path_contained, StandardCliOptions, parse_standard_cli_flags, open_wal_sqlite, SqliteSchemaMigrationRunner, recover_corrupt_sqlite, emit_heartbeat, RollingLogSink, ThreeFileSettingsStore, CrashDumper, PidSentinelLock, CONTAINMENT_EXIT_CODE, SandboxContainmentError, is_containment_violation, SandboxMountSpec, SandboxResolution, get_subst_mappings, resolve_sandbox_environment, resolve_known_folders, get_known_folder, is_path_within_sandbox, assert_sandbox_path_if_active, map_host_path_to_sandbox, REGISTRY_HIVE_SCHEMA_URI, REGISTRY_HIVE_SCHEMA_VERSION, PlatformRegistryHive, PlatformRegistryValueKind, JsonHivePlatformRegistry

from __future__ import annotations

from .cli import StandardCliOptions, assert_path_contained, parse_standard_cli_flags
from .crash_dumper import CrashDumper
from .db import SqliteSchemaMigrationRunner, open_wal_sqlite, recover_corrupt_sqlite
from .locks import PidSentinelLock
from .logging import RollingLogSink, emit_heartbeat
from .pseudo_registry import (
    REGISTRY_HIVE_SCHEMA_URI,
    REGISTRY_HIVE_SCHEMA_VERSION,
    JsonHivePlatformRegistry,
    PlatformRegistryHive,
    PlatformRegistryValueKind,
)
from .sandbox_seam import (
    CONTAINMENT_EXIT_CODE,
    SandboxContainmentError,
    SandboxMountSpec,
    SandboxResolution,
    assert_sandbox_path_if_active,
    get_known_folder,
    get_subst_mappings,
    is_containment_violation,
    is_path_within_sandbox,
    map_host_path_to_sandbox,
    resolve_known_folders,
    resolve_sandbox_environment,
)
from .settings import ThreeFileSettingsStore

SDK_VERSION: str = "0.5.2"
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
    "CONTAINMENT_EXIT_CODE",
    "SandboxContainmentError",
    "is_containment_violation",
    "SandboxMountSpec",
    "SandboxResolution",
    "get_subst_mappings",
    "resolve_sandbox_environment",
    "resolve_known_folders",
    "get_known_folder",
    "is_path_within_sandbox",
    "assert_sandbox_path_if_active",
    "map_host_path_to_sandbox",
    "REGISTRY_HIVE_SCHEMA_URI",
    "REGISTRY_HIVE_SCHEMA_VERSION",
    "PlatformRegistryHive",
    "PlatformRegistryValueKind",
    "JsonHivePlatformRegistry",
]
