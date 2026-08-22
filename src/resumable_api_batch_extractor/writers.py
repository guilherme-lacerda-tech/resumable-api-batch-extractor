from __future__ import annotations

import json
from pathlib import Path
from typing import Any


class OutputIntegrityError(RuntimeError):
    """Raised when an existing output file cannot be safely resumed."""


class NdjsonSink:
    def __init__(self, path: str | Path, *, id_field: str = "id"):
        self.path = Path(path)
        self.id_field = id_field
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._seen_ids = self._load_seen_ids()
        self.skipped_duplicates = 0

    @property
    def record_count(self) -> int:
        return len(self._seen_ids)

    def _load_seen_ids(self) -> set[str]:
        if not self.path.exists():
            return set()
        seen: set[str] = set()
        for line_number, line in enumerate(self.path.read_text(encoding="utf-8").splitlines(), start=1):
            if not line.strip():
                continue
            try:
                payload = json.loads(line)
            except json.JSONDecodeError as exc:
                raise OutputIntegrityError(
                    f"output file {self.path} contains invalid JSON on line {line_number}"
                ) from exc
            if not isinstance(payload, dict):
                raise OutputIntegrityError(
                    f"output file {self.path} contains a non-object record on line {line_number}"
                )
            value = payload.get(self.id_field)
            if value is not None:
                seen.add(str(value))
        return seen

    def write_many(self, records: list[dict[str, Any]]) -> int:
        lines: list[str] = []
        new_ids: list[str] = []
        pending_ids: set[str] = set()
        seen_ids = self._seen_ids
        for record in records:
            record_id = record.get(self.id_field)
            if record_id is None:
                raise ValueError(f"record is missing id field {self.id_field!r}")
            normalized_id = str(record_id)
            if normalized_id in seen_ids or normalized_id in pending_ids:
                self.skipped_duplicates += 1
                continue
            pending_ids.add(normalized_id)
            new_ids.append(normalized_id)
            lines.append(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n")

        with self.path.open("a", encoding="utf-8") as stream:
            stream.writelines(lines)
        seen_ids.update(new_ids)
        return len(new_ids)
