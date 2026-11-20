# @module: foundation_core.db
# @purpose: SQLite WAL connection initializer, monotonic schema migration runner, and corrupt DB recovery (Standard 05 & Standard 13).
# @depends-on: pathlib, shutil, sqlite3, time
# @exports: open_wal_sqlite, SqliteSchemaMigrationRunner, recover_corrupt_sqlite

from __future__ import annotations

import shutil
import sqlite3
import time
from pathlib import Path
from typing import Callable

def open_wal_sqlite(db_path: str | Path, busy_timeout_ms: int = 15000) -> sqlite3.Connection:
    """Opens a SQLite connection configured with WAL mode, NORMAL synchronous, and foreign keys."""
    target = Path(db_path).resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(target), timeout=max(5.0, busy_timeout_ms / 1000.0))
    conn.execute("PRAGMA journal_mode = WAL;")
    conn.execute(f"PRAGMA busy_timeout = {max(1000, busy_timeout_ms)};")
    conn.execute("PRAGMA synchronous = NORMAL;")
    conn.execute("PRAGMA foreign_keys = ON;")
    conn.execute("PRAGMA cache_size = -65536;")
    return conn

def recover_corrupt_sqlite(db_path: str | Path) -> Path | None:
    """Quarantines a corrupt SQLite database file as .corrupt.<timestamp> so a fresh schema can be initialized."""
    target = Path(db_path).resolve()
    if not target.exists():
        return None
    stamp = time.strftime("%Y%m%d_%H%M%S", time.gmtime())
    quarantined = target.with_name(f"{target.name}.corrupt.{stamp}")
    shutil.move(str(target), str(quarantined))
    for suffix in ("-wal", "-shm"):
        side = target.with_name(target.name + suffix)
        if side.exists():
            side.unlink(missing_ok=True)
    return quarantined

class SqliteSchemaMigrationRunner:
    """Monotonic schema_version migration runner for SQLite databases."""

    def __init__(self, conn: sqlite3.Connection) -> None:
        self.conn = conn
        self._ensure_meta_table()

    def _ensure_meta_table(self) -> None:
        with self.conn:
            self.conn.execute(
                """
                CREATE TABLE IF NOT EXISTS _foundation_schema_migrations (
                    version INTEGER PRIMARY KEY,
                    description TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL
                );
                """
            )

    def current_version(self) -> int:
        row = self.conn.execute(
            "SELECT COALESCE(MAX(version), 0) FROM _foundation_schema_migrations;"
        ).fetchone()
        return int(row[0]) if row and row[0] is not None else 0

    def apply_migrations(
        self,
        migrations: list[tuple[int, str, Callable[[sqlite3.Connection], None]]],
    ) -> int:
        current = self.current_version()
        for version, description, fn in sorted(migrations, key=lambda m: m[0]):
            if version <= current:
                continue
            with self.conn:
                fn(self.conn)
                self.conn.execute(
                    "INSERT INTO _foundation_schema_migrations (version, description, applied_at_utc) VALUES (?, ?, ?);",
                    (version, description, time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())),
                )
            current = version
        return current
