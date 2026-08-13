from __future__ import annotations

from dataclasses import asdict

from resumable_api_batch_extractor.checkpoint import SQLiteCheckpointStore
from resumable_api_batch_extractor.client import HttpApiClient
from resumable_api_batch_extractor.models import ExtractionStats, ExtractorConfig
from resumable_api_batch_extractor.writers import NdjsonSink


class ExtractionInterrupted(RuntimeError):
    """Used by demos/tests to simulate a process stop between resumable runs."""


class BatchExtractor:
    def __init__(
        self,
        client: HttpApiClient,
        checkpoint_store: SQLiteCheckpointStore,
        sink: NdjsonSink,
        config: ExtractorConfig,
    ):
        self.client = client
        self.checkpoint_store = checkpoint_store
        self.sink = sink
        self.config = config

    def run(self, *, interrupt_after_pages: int | None = None) -> ExtractionStats:
        state = self.checkpoint_store.load(self.config.job_name)
        if state.completed:
            return ExtractionStats(
                completed=True,
                pages_read=0,
                records_written=0,
                last_cursor=None,
                resumed=True,
                retries=self.client.retry_count,
                skipped_duplicates=self.sink.skipped_duplicates,
            )

        cursor = state.cursor
        total_pages = state.pages
        pages_this_run = 0
        resumed = cursor is not None or state.pages > 0 or state.records > 0

        while True:
            if self.config.max_pages is not None and pages_this_run >= self.config.max_pages:
                return self._stats(
                    completed=False,
                    pages_read=pages_this_run,
                    records_written=self.sink.record_count - state.records,
                    cursor=cursor,
                    resumed=resumed,
                )

            page = self.client.fetch_page(self.config, cursor)
            self.sink.write_many(page.records)
            total_pages += 1
            pages_this_run += 1
            cursor = page.next_cursor

            if cursor is None:
                self.checkpoint_store.mark_completed(
                    self.config.job_name,
                    total_pages,
                    self.sink.record_count,
                )
                return self._stats(
                    completed=True,
                    pages_read=pages_this_run,
                    records_written=self.sink.record_count - state.records,
                    cursor=None,
                    resumed=resumed,
                )

            self.checkpoint_store.save(
                self.config.job_name,
                cursor,
                total_pages,
                self.sink.record_count,
            )

            if interrupt_after_pages is not None and pages_this_run >= interrupt_after_pages:
                raise ExtractionInterrupted(
                    f"simulated interruption after {pages_this_run} page(s): {asdict(self._stats(False, pages_this_run, self.sink.record_count - state.records, cursor, resumed))}"
                )

    def _stats(
        self,
        completed: bool,
        pages_read: int,
        records_written: int,
        cursor: str | None,
        resumed: bool,
    ) -> ExtractionStats:
        return ExtractionStats(
            completed=completed,
            pages_read=pages_read,
            records_written=records_written,
            last_cursor=cursor,
            resumed=resumed,
            retries=self.client.retry_count,
            skipped_duplicates=self.sink.skipped_duplicates,
        )

