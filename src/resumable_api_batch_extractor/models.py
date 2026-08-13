from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class ExtractorConfig:
    endpoint: str = "/records"
    job_name: str = "synthetic-record-extraction"
    page_size: int = 50
    max_pages: int | None = None
    retry_attempts: int = 3
    backoff_seconds: float = 0.0
    request_timeout: float = 5.0
    cursor_param: str = "cursor"
    page_size_param: str = "limit"
    cursor_field: str = "next_cursor"
    records_field: str = "records"
    id_field: str = "id"


@dataclass(frozen=True)
class Page:
    records: list[dict[str, Any]]
    next_cursor: str | None
    raw: dict[str, Any]


@dataclass(frozen=True)
class CheckpointState:
    cursor: str | None = None
    completed: bool = False
    pages: int = 0
    records: int = 0


@dataclass(frozen=True)
class ExtractionStats:
    completed: bool
    pages_read: int
    records_written: int
    last_cursor: str | None
    resumed: bool
    retries: int = 0
    skipped_duplicates: int = 0

