from __future__ import annotations

from datetime import UTC, datetime, timedelta
from typing import Any
from urllib.parse import parse_qs

import httpx


def build_synthetic_records(total: int = 125) -> list[dict[str, Any]]:
    base = datetime(2026, 1, 1, tzinfo=UTC)
    regions = ["north", "south", "east", "west"]
    statuses = ["active", "pending-review", "archived"]
    records: list[dict[str, Any]] = []
    for index in range(total):
        records.append(
            {
                "id": f"asset-{index + 1:04d}",
                "status": statuses[index % len(statuses)],
                "region": regions[index % len(regions)],
                "priority": 1 + (index % 5),
                "updated_at": (base + timedelta(minutes=index * 7)).isoformat(),
            }
        )
    return records


class SyntheticPaginatedApi:
    def __init__(
        self,
        records: list[dict[str, Any]],
        *,
        endpoint: str = "/records",
        transient_failures: dict[int, int] | None = None,
    ):
        self.records = records
        self.endpoint = endpoint
        self.transient_failures = transient_failures or {}
        self.failures_seen: dict[int, int] = {}
        self.requests_seen: list[int] = []

    def transport(self) -> httpx.MockTransport:
        return httpx.MockTransport(self._handle)

    def _handle(self, request: httpx.Request) -> httpx.Response:
        if request.url.path != self.endpoint:
            return httpx.Response(404, json={"error": "not found"})

        params = parse_qs(request.url.query.decode())
        start = int(params.get("cursor", ["0"])[0])
        limit = int(params.get("limit", ["50"])[0])
        self.requests_seen.append(start)

        failures_allowed = self.transient_failures.get(start, 0)
        failures_current = self.failures_seen.get(start, 0)
        if failures_current < failures_allowed:
            self.failures_seen[start] = failures_current + 1
            return httpx.Response(503, json={"error": "synthetic transient overload"})

        stop = start + limit
        batch = self.records[start:stop]
        next_cursor = str(stop) if stop < len(self.records) else None
        return httpx.Response(
            200,
            json={
                "records": batch,
                "next_cursor": next_cursor,
                "count": len(batch),
            },
        )

