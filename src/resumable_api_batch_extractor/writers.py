from __future__ import annotations

import json
from pathlib import Path
from typing import Any


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
        for line in self.path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            payload = json.loads(line)
            value = payload.get(self.id_field)
            if value is not None:
                seen.add(str(value))
        return seen

    def write_many(self, records: list[dict[str, Any]]) -> int:
        written = 0
        with self.path.open("a", encoding="utf-8") as stream:
            for record in records:
                record_id = record.get(self.id_field)
                if record_id is None:
                    raise ValueError(f"record is missing id field {self.id_field!r}")
                normalized_id = str(record_id)
                if normalized_id in self._seen_ids:
                    self.skipped_duplicates += 1
                    continue
                stream.write(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n")
                self._seen_ids.add(normalized_id)
                written += 1
        return written

