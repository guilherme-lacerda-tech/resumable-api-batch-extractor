import json
from pathlib import Path
from urllib.parse import parse_qs

import httpx
import pytest

from resumable_api_batch_extractor.checkpoint import SQLiteCheckpointStore
from resumable_api_batch_extractor.client import ApiContractError, HttpApiClient, RecoverableApiError
from resumable_api_batch_extractor.demo_api import build_synthetic_records
from resumable_api_batch_extractor.extractor import BatchExtractor, ExtractionInterrupted
from resumable_api_batch_extractor.manifest import ManifestRecorder
from resumable_api_batch_extractor.models import ExtractorConfig
from resumable_api_batch_extractor.writers import NdjsonSink, OutputIntegrityError


def read_jsonl(path: Path) -> list[dict[str, object]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def read_output(path: Path) -> list[dict[str, object]]:
    return read_jsonl(path)


def response_page(records: list[dict[str, object]], request: httpx.Request) -> httpx.Response:
    params = parse_qs(request.url.query.decode())
    start = int(params.get("cursor", ["0"])[0])
    limit = int(params.get("limit", ["50"])[0])
    stop = start + limit
    next_cursor = str(stop) if stop < len(records) else None
    return httpx.Response(
        200,
        json={"records": records[start:stop], "next_cursor": next_cursor},
        request=request,
    )


def build_extractor_with_transport(
    tmp_path: Path,
    transport: httpx.BaseTransport,
    *,
    page_size: int = 4,
    retry_attempts: int = 3,
) -> tuple[BatchExtractor, SQLiteCheckpointStore, NdjsonSink, HttpApiClient, Path]:
    config = ExtractorConfig(page_size=page_size, retry_attempts=retry_attempts)
    store = SQLiteCheckpointStore(tmp_path / "state.sqlite3")
    sink = NdjsonSink(tmp_path / "records.ndjson", id_field=config.id_field)
    client = HttpApiClient("https://synthetic.local", transport=transport)
    manifest_path = tmp_path / "manifest.jsonl"
    extractor = BatchExtractor(client, store, sink, config, ManifestRecorder(manifest_path))
    return extractor, store, sink, client, manifest_path


@pytest.mark.parametrize("status_code", [429, 500, 503])
def test_transient_status_retries_checkpoint_manifest_and_no_duplicates(
    tmp_path: Path,
    status_code: int,
) -> None:
    records = build_synthetic_records(8)
    attempts = 0

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal attempts
        if attempts == 0:
            attempts += 1
            return httpx.Response(status_code, json={"error": "synthetic transient"}, request=request)
        return response_page(records, request)

    extractor, store, sink, client, manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(handler),
    )
    try:
        stats = extractor.run()
    finally:
        client.close()

    assert stats.completed is True
    assert stats.retries == 1
    assert sink.skipped_duplicates == 0
    assert len(read_output(tmp_path / "records.ndjson")) == 8
    assert store.load("synthetic-record-extraction").completed is True
    events = read_jsonl(manifest_path)
    assert "checkpoint_completed" in {event["event"] for event in events}
    assert "page_written" in {event["event"] for event in events}


@pytest.mark.parametrize("failure", ["timeout", "connection_reset"])
def test_network_failures_retry_and_recover_with_manifest(tmp_path: Path, failure: str) -> None:
    records = build_synthetic_records(6)
    attempts = 0

    def handler(request: httpx.Request) -> httpx.Response:
        nonlocal attempts
        if attempts == 0:
            attempts += 1
            if failure == "timeout":
                raise httpx.ReadTimeout("synthetic timeout", request=request)
            raise httpx.ConnectError("synthetic connection reset", request=request)
        return response_page(records, request)

    extractor, store, sink, client, manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(handler),
        page_size=3,
    )
    try:
        stats = extractor.run()
    finally:
        client.close()

    assert stats.completed is True
    assert stats.retries == 1
    assert sink.record_count == 6
    assert store.load("synthetic-record-extraction").records == 6
    assert "run_completed" in {event["event"] for event in read_jsonl(manifest_path)}


@pytest.mark.parametrize(
    ("payload", "expected_error"),
    [
        (b'{"records":', "API response body must be valid JSON"),
        ({"records": "not-a-list", "next_cursor": None}, "records"),
    ],
    ids=["partial_json_payload", "invalid_contract_payload"],
)
def test_bad_payloads_fail_without_checkpoint_or_output(
    tmp_path: Path,
    payload: bytes | dict[str, object],
    expected_error: str,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if isinstance(payload, bytes):
            return httpx.Response(200, content=payload, request=request)
        return httpx.Response(200, json=payload, request=request)

    extractor, store, _sink, client, manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(handler),
    )
    try:
        with pytest.raises(ApiContractError, match=expected_error):
            extractor.run()
    finally:
        client.close()

    checkpoint = store.load("synthetic-record-extraction")
    assert checkpoint.pages == 0
    assert checkpoint.records == 0
    assert not (tmp_path / "records.ndjson").exists()
    failure_events = [
        event for event in read_jsonl(manifest_path) if event["event"] == "page_fetch_failed"
    ]
    assert failure_events[-1]["error_type"] == "ApiContractError"


def test_crash_after_write_before_checkpoint_replays_and_skips_duplicates(
    tmp_path: Path,
) -> None:
    records = build_synthetic_records(8)
    transport = httpx.MockTransport(lambda request: response_page(records, request))
    first, store, _sink, first_client, manifest_path = build_extractor_with_transport(
        tmp_path,
        transport,
    )
    try:
        with pytest.raises(ExtractionInterrupted):
            first.run(interrupt_after_write_pages=1)
    finally:
        first_client.close()

    checkpoint = store.load("synthetic-record-extraction")
    assert checkpoint.cursor is None
    assert checkpoint.pages == 0
    assert checkpoint.records == 0
    assert len(read_output(tmp_path / "records.ndjson")) == 4

    second_transport = httpx.MockTransport(lambda request: response_page(records, request))
    second, store, sink, second_client, _manifest_path = build_extractor_with_transport(
        tmp_path,
        second_transport,
    )
    try:
        stats = second.run()
    finally:
        second_client.close()

    assert stats.completed is True
    assert stats.resumed is True
    assert stats.records_written == 4
    assert sink.skipped_duplicates == 4
    assert len(read_output(tmp_path / "records.ndjson")) == 8
    assert store.load("synthetic-record-extraction").records == 8
    events = read_jsonl(manifest_path)
    assert any(
        event["event"] == "interrupted" and event["stage"] == "after_write_before_checkpoint"
        for event in events
    )
    assert any(
        event["event"] == "page_written" and event["skipped_duplicates"] == 4
        for event in events
    )


def test_crash_after_checkpoint_resumes_without_duplicate_writes(tmp_path: Path) -> None:
    records = build_synthetic_records(8)
    first, store, _sink, first_client, manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(lambda request: response_page(records, request)),
    )
    try:
        with pytest.raises(ExtractionInterrupted):
            first.run(interrupt_after_pages=1)
    finally:
        first_client.close()

    checkpoint = store.load("synthetic-record-extraction")
    assert checkpoint.cursor == "4"
    assert checkpoint.pages == 1
    assert checkpoint.records == 4

    second, store, sink, second_client, _manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(lambda request: response_page(records, request)),
    )
    try:
        stats = second.run()
    finally:
        second_client.close()

    assert stats.completed is True
    assert stats.resumed is True
    assert stats.pages_read == 1
    assert stats.records_written == 4
    assert sink.skipped_duplicates == 0
    assert store.load("synthetic-record-extraction").records == 8
    assert any(
        event["event"] == "interrupted" and event["stage"] == "after_checkpoint"
        for event in read_jsonl(manifest_path)
    )


def test_completed_checkpoint_with_incomplete_output_replays_and_fills_cache(
    tmp_path: Path,
) -> None:
    records = build_synthetic_records(12)
    first, store, _sink, first_client, _manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(lambda request: response_page(records, request)),
    )
    try:
        assert first.run().completed is True
    finally:
        first_client.close()

    output = tmp_path / "records.ndjson"
    lines = output.read_text(encoding="utf-8").splitlines()
    output.write_text("\n".join(lines[:10]) + "\n", encoding="utf-8")

    second, store, sink, second_client, manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(lambda request: response_page(records, request)),
    )
    try:
        stats = second.run()
    finally:
        second_client.close()

    assert stats.completed is True
    assert stats.resumed is True
    assert stats.records_written == 2
    assert sink.skipped_duplicates == 10
    assert len(read_output(output)) == 12
    assert store.load("synthetic-record-extraction").completed is True
    assert any(event["event"] == "output_incomplete" for event in read_jsonl(manifest_path))


def test_corrupt_output_cache_fails_fast(tmp_path: Path) -> None:
    output = tmp_path / "records.ndjson"
    output.write_text('{"id":"asset-0001"}\n{"id":', encoding="utf-8")

    with pytest.raises(OutputIntegrityError, match="invalid JSON"):
        NdjsonSink(output)


def test_rate_limit_exhaustion_records_recoverable_failure(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(429, json={"error": "rate limited"}, request=request)

    extractor, store, _sink, client, manifest_path = build_extractor_with_transport(
        tmp_path,
        httpx.MockTransport(handler),
        retry_attempts=2,
    )
    try:
        with pytest.raises(RecoverableApiError):
            extractor.run()
    finally:
        client.close()

    checkpoint = store.load("synthetic-record-extraction")
    assert checkpoint.pages == 0
    assert checkpoint.records == 0
    assert client.retry_count == 1
    failure_events = [
        event for event in read_jsonl(manifest_path) if event["event"] == "page_fetch_failed"
    ]
    assert failure_events[-1]["error_type"] == "RecoverableApiError"
