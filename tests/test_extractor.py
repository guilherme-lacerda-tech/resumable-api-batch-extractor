import json

import pytest

from resumable_api_batch_extractor.checkpoint import SQLiteCheckpointStore
from resumable_api_batch_extractor.client import HttpApiClient
from resumable_api_batch_extractor.demo_api import SyntheticPaginatedApi, build_synthetic_records
from resumable_api_batch_extractor.extractor import BatchExtractor, ExtractionInterrupted
from resumable_api_batch_extractor.models import ExtractorConfig
from resumable_api_batch_extractor.writers import NdjsonSink


def build_extractor(tmp_path, *, total: int = 12, page_size: int = 5, api=None):
    config = ExtractorConfig(page_size=page_size)
    api = api or SyntheticPaginatedApi(build_synthetic_records(total))
    store = SQLiteCheckpointStore(tmp_path / "state.sqlite3")
    sink = NdjsonSink(tmp_path / "records.ndjson")
    client = HttpApiClient("https://synthetic.local", transport=api.transport())
    return BatchExtractor(client, store, sink, config), store, sink, client


def read_output(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def test_extractor_writes_all_records_and_completes_checkpoint(tmp_path) -> None:
    extractor, store, _sink, client = build_extractor(tmp_path, total=12, page_size=5)
    try:
        stats = extractor.run()
    finally:
        client.close()

    assert stats.completed is True
    assert stats.pages_read == 3
    assert stats.records_written == 12
    assert stats.last_cursor is None
    assert len(read_output(tmp_path / "records.ndjson")) == 12
    assert store.load("synthetic-record-extraction").completed is True


def test_extractor_resumes_after_interruption(tmp_path) -> None:
    first, _store, _sink, first_client = build_extractor(tmp_path, total=12, page_size=5)
    try:
        with pytest.raises(ExtractionInterrupted):
            first.run(interrupt_after_pages=2)
    finally:
        first_client.close()

    second, store, sink, second_client = build_extractor(tmp_path, total=12, page_size=5)
    try:
        stats = second.run()
    finally:
        second_client.close()

    assert stats.completed is True
    assert stats.resumed is True
    assert stats.pages_read == 1
    assert stats.records_written == 2
    assert sink.record_count == 12
    assert store.load("synthetic-record-extraction").records == 12


def test_extractor_stops_without_completion_when_max_pages_is_reached(tmp_path) -> None:
    config = ExtractorConfig(page_size=4, max_pages=1)
    api = SyntheticPaginatedApi(build_synthetic_records(9))
    store = SQLiteCheckpointStore(tmp_path / "state.sqlite3")
    sink = NdjsonSink(tmp_path / "records.ndjson")
    client = HttpApiClient("https://synthetic.local", transport=api.transport())
    try:
        stats = BatchExtractor(client, store, sink, config).run()
    finally:
        client.close()

    assert stats.completed is False
    assert stats.last_cursor == "4"
    assert store.load(config.job_name).cursor == "4"
    assert sink.record_count == 4


def test_sink_skips_duplicates_when_checkpoint_is_behind(tmp_path) -> None:
    output = tmp_path / "records.ndjson"
    sink = NdjsonSink(output)
    records = build_synthetic_records(3)

    assert sink.write_many(records) == 3
    second_sink = NdjsonSink(output)
    assert second_sink.write_many(records[:2]) == 0
    assert second_sink.skipped_duplicates == 2
    assert len(read_output(output)) == 3


def test_sink_rejects_records_without_id(tmp_path) -> None:
    sink = NdjsonSink(tmp_path / "records.ndjson")

    with pytest.raises(ValueError):
        sink.write_many([{"name": "missing identifier"}])

