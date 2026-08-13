from __future__ import annotations

import sqlite3
from datetime import UTC, datetime
from pathlib import Path

from resumable_api_batch_extractor.models import CheckpointState


class SQLiteCheckpointStore:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._initialize()

    def _connect(self) -> sqlite3.Connection:
        return sqlite3.connect(self.path)

    def _initialize(self) -> None:
        with self._connect() as connection:
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS checkpoints (
                    job_name TEXT PRIMARY KEY,
                    cursor TEXT,
                    completed INTEGER NOT NULL,
                    pages INTEGER NOT NULL,
                    records INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT
                )
                """
            )

    def load(self, job_name: str) -> CheckpointState:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT cursor, completed, pages, records FROM checkpoints WHERE job_name = ?",
                (job_name,),
            ).fetchone()
        if row is None:
            return CheckpointState()
        return CheckpointState(
            cursor=row[0],
            completed=bool(row[1]),
            pages=row[2],
            records=row[3],
        )

    def save(self, job_name: str, cursor: str | None, pages: int, records: int) -> None:
        now = datetime.now(UTC).isoformat()
        with self._connect() as connection:
            connection.execute(
                """
                INSERT INTO checkpoints (job_name, cursor, completed, pages, records, updated_at)
                VALUES (?, ?, 0, ?, ?, ?)
                ON CONFLICT(job_name) DO UPDATE SET
                    cursor = excluded.cursor,
                    completed = 0,
                    pages = excluded.pages,
                    records = excluded.records,
                    updated_at = excluded.updated_at,
                    completed_at = NULL
                """,
                (job_name, cursor, pages, records, now),
            )

    def mark_completed(self, job_name: str, pages: int, records: int) -> None:
        now = datetime.now(UTC).isoformat()
        with self._connect() as connection:
            connection.execute(
                """
                INSERT INTO checkpoints (
                    job_name, cursor, completed, pages, records, updated_at, completed_at
                )
                VALUES (?, NULL, 1, ?, ?, ?, ?)
                ON CONFLICT(job_name) DO UPDATE SET
                    cursor = NULL,
                    completed = 1,
                    pages = excluded.pages,
                    records = excluded.records,
                    updated_at = excluded.updated_at,
                    completed_at = excluded.completed_at
                """,
                (job_name, pages, records, now, now),
            )

    def reset(self, job_name: str) -> None:
        with self._connect() as connection:
            connection.execute("DELETE FROM checkpoints WHERE job_name = ?", (job_name,))

