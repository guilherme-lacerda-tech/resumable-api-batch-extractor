from __future__ import annotations

from dataclasses import asdict

from resumable_api_batch_extractor.checkpoint import SQLiteCheckpointStore
from resumable_api_batch_extractor.client import HttpApiClient
from resumable_api_batch_extractor.manifest import ManifestRecorder
from resumable_api_batch_extractor.models import CheckpointState, ExtractionStats, ExtractorConfig
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
        manifest: ManifestRecorder | None = None,
    ):
        self.client = client
        self.checkpoint_store = checkpoint_store
        self.sink = sink
        self.config = config
        self.manifest = manifest

    def run(
        self,
        *,
        interrupt_after_pages: int | None = None,
        interrupt_after_write_pages: int | None = None,
    ) -> ExtractionStats:
        state = self.checkpoint_store.load(self.config.job_name)
        recovered_incomplete_output = False
        if state.completed and self.sink.record_count < state.records:
            self._record(
                "output_incomplete",
                checkpoint_records=state.records,
                output_records=self.sink.record_count,
                recovery="reset_checkpoint_and_replay_from_start",
            )
            self.checkpoint_store.reset(self.config.job_name)
            state = CheckpointState(records=self.sink.record_count)
            recovered_incomplete_output = True

        if state.completed:
            self._record(
                "run_skipped_completed",
                checkpoint_records=state.records,
                output_records=self.sink.record_count,
            )
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
        starting_record_count = self.sink.record_count
        resumed = (
            recovered_incomplete_output
            or starting_record_count > 0
            or cursor is not None
            or state.pages > 0
            or state.records > 0
        )
        self._record(
            "run_started",
            cursor=cursor,
            checkpoint_pages=state.pages,
            checkpoint_records=state.records,
            output_records=starting_record_count,
            resumed=resumed,
        )

        while True:
            if self.config.max_pages is not None and pages_this_run >= self.config.max_pages:
                return self._stats(
                    completed=False,
                    pages_read=pages_this_run,
                    records_written=self.sink.record_count - starting_record_count,
                    cursor=cursor,
                    resumed=resumed,
                )

            cursor_before_fetch = cursor
            self._record("page_fetch_started", cursor=cursor_before_fetch)
            try:
                page = self.client.fetch_page(self.config, cursor)
            except Exception as exc:
                self._record(
                    "page_fetch_failed",
                    cursor=cursor_before_fetch,
                    error_type=type(exc).__name__,
                    error=str(exc),
                )
                raise

            try:
                records_written = self.sink.write_many(page.records)
            except Exception as exc:
                self._record(
                    "page_write_failed",
                    cursor=cursor_before_fetch,
                    error_type=type(exc).__name__,
                    error=str(exc),
                )
                raise

            total_pages += 1
            pages_this_run += 1
            cursor = page.next_cursor
            self._record(
                "page_written",
                cursor=cursor_before_fetch,
                next_cursor=cursor,
                records_fetched=len(page.records),
                records_written=records_written,
                skipped_duplicates=self.sink.skipped_duplicates,
            )

            if (
                interrupt_after_write_pages is not None
                and pages_this_run >= interrupt_after_write_pages
            ):
                self._record(
                    "interrupted",
                    stage="after_write_before_checkpoint",
                    pages_this_run=pages_this_run,
                    cursor=cursor,
                    output_records=self.sink.record_count,
                )
                raise ExtractionInterrupted(
                    f"simulated interruption after writing {pages_this_run} page(s) before checkpoint"
                )

            if cursor is None:
                self.checkpoint_store.mark_completed(
                    self.config.job_name,
                    total_pages,
                    self.sink.record_count,
                )
                stats = self._stats(
                    completed=True,
                    pages_read=pages_this_run,
                    records_written=self.sink.record_count - starting_record_count,
                    cursor=None,
                    resumed=resumed,
                )
                self._record(
                    "checkpoint_completed",
                    pages=total_pages,
                    records=self.sink.record_count,
                )
                self._record(
                    "run_completed",
                    pages_read=pages_this_run,
                    records_written=stats.records_written,
                    skipped_duplicates=self.sink.skipped_duplicates,
                    retries=self.client.retry_count,
                )
                return stats

            self.checkpoint_store.save(
                self.config.job_name,
                cursor,
                total_pages,
                self.sink.record_count,
            )
            self._record(
                "checkpoint_saved",
                cursor=cursor,
                pages=total_pages,
                records=self.sink.record_count,
            )

            if interrupt_after_pages is not None and pages_this_run >= interrupt_after_pages:
                self._record(
                    "interrupted",
                    stage="after_checkpoint",
                    pages_this_run=pages_this_run,
                    cursor=cursor,
                    output_records=self.sink.record_count,
                )
                raise ExtractionInterrupted(
                    f"simulated interruption after {pages_this_run} page(s): {asdict(self._stats(False, pages_this_run, self.sink.record_count - starting_record_count, cursor, resumed))}"
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

    def _record(self, event: str, **fields: object) -> None:
        if self.manifest is not None:
            self.manifest.record(event, job_name=self.config.job_name, **fields)
