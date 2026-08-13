from __future__ import annotations

import time
from typing import Any

import httpx

from resumable_api_batch_extractor.models import ExtractorConfig, Page


class ApiContractError(RuntimeError):
    """Raised when the API payload does not match the expected pagination contract."""


class RecoverableApiError(RuntimeError):
    """Raised when a transient HTTP failure remains unresolved after retries."""


class HttpApiClient:
    def __init__(
        self,
        base_url: str,
        *,
        transport: httpx.BaseTransport | None = None,
        headers: dict[str, str] | None = None,
    ):
        self._client = httpx.Client(base_url=base_url, transport=transport, headers=headers)
        self.retry_count = 0

    def fetch_page(self, config: ExtractorConfig, cursor: str | None) -> Page:
        params: dict[str, str | int] = {config.page_size_param: config.page_size}
        if cursor is not None:
            params[config.cursor_param] = cursor

        last_error: Exception | None = None
        for attempt in range(1, config.retry_attempts + 1):
            try:
                response = self._client.get(
                    config.endpoint,
                    params=params,
                    timeout=config.request_timeout,
                )
                if response.status_code in {429, 500, 502, 503, 504}:
                    raise RecoverableApiError(
                        f"transient status {response.status_code} while fetching cursor {cursor!r}"
                    )
                response.raise_for_status()
                return self._parse_page(response.json(), config)
            except (httpx.HTTPError, RecoverableApiError) as exc:
                last_error = exc
                if attempt == config.retry_attempts:
                    break
                self.retry_count += 1
                if config.backoff_seconds:
                    time.sleep(config.backoff_seconds * attempt)

        raise RecoverableApiError(f"API page could not be fetched after retries: {last_error}")

    @staticmethod
    def _parse_page(payload: Any, config: ExtractorConfig) -> Page:
        if not isinstance(payload, dict):
            raise ApiContractError("API response must be a JSON object")
        records = payload.get(config.records_field)
        next_cursor = payload.get(config.cursor_field)
        if not isinstance(records, list):
            raise ApiContractError(f"API response must include a list field named {config.records_field!r}")
        if next_cursor is not None and not isinstance(next_cursor, str):
            raise ApiContractError(f"API cursor field {config.cursor_field!r} must be a string or null")
        typed_records: list[dict[str, Any]] = []
        for record in records:
            if not isinstance(record, dict):
                raise ApiContractError("every API record must be a JSON object")
            typed_records.append(record)
        return Page(records=typed_records, next_cursor=next_cursor, raw=payload)

    def close(self) -> None:
        self._client.close()

    def __enter__(self) -> HttpApiClient:
        return self

    def __exit__(self, *_args: object) -> None:
        self.close()

