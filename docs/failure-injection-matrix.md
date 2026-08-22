# Failure Injection Matrix

All scenarios use synthetic data and `httpx.MockTransport`; no external API, customer data or private endpoint is used.

| Scenario | Test | Expected result | Manifest | Checkpoint | Duplicate behavior | Recovery |
|---|---|---|---|---|---|---|
| 429 rate limit, then success | `test_transient_status_retries_checkpoint_manifest_and_no_duplicates` | Retry once, complete extraction | `page_written`, `checkpoint_completed`, `run_completed` | Completed with all records | 0 skipped duplicates | Automatic retry |
| 500 transient failure, then success | `test_transient_status_retries_checkpoint_manifest_and_no_duplicates` | Retry once, complete extraction | `page_written`, `checkpoint_completed`, `run_completed` | Completed with all records | 0 skipped duplicates | Automatic retry |
| 503 transient failure, then success | `test_transient_status_retries_checkpoint_manifest_and_no_duplicates` | Retry once, complete extraction | `page_written`, `checkpoint_completed`, `run_completed` | Completed with all records | 0 skipped duplicates | Automatic retry |
| Timeout, then success | `test_network_failures_retry_and_recover_with_manifest` | Retry once, complete extraction | `run_completed` | Completed with all records | 0 skipped duplicates | Automatic retry |
| Connection reset, then success | `test_network_failures_retry_and_recover_with_manifest` | Retry once, complete extraction | `run_completed` | Completed with all records | 0 skipped duplicates | Automatic retry |
| Partial JSON payload | `test_bad_payloads_fail_without_checkpoint_or_output` | Raise `ApiContractError` | `page_fetch_failed` | Unchanged at 0 pages / 0 records | No output written | Fix producer contract and rerun |
| Invalid contract payload | `test_bad_payloads_fail_without_checkpoint_or_output` | Raise `ApiContractError` | `page_fetch_failed` | Unchanged at 0 pages / 0 records | No output written | Fix producer contract and rerun |
| Crash after write before checkpoint | `test_crash_after_write_before_checkpoint_replays_and_skips_duplicates` | First page is written, checkpoint remains behind | `interrupted` with `after_write_before_checkpoint` | Unchanged at 0 pages / 0 records | Restart replays page and skips 4 duplicates | Rerun from start and fill missing records |
| Crash after checkpoint | `test_crash_after_checkpoint_resumes_without_duplicate_writes` | First page and checkpoint are durable | `interrupted` with `after_checkpoint` | Cursor points to next page | 0 duplicate writes on restart | Resume from saved cursor |
| Restart from durable checkpoint | `test_crash_after_checkpoint_resumes_without_duplicate_writes` | New process continues from checkpoint | `run_completed` | Completed with all records | 0 duplicate writes | Resume from saved cursor |
| Incomplete output with completed checkpoint | `test_completed_checkpoint_with_incomplete_output_replays_and_fills_cache` | Detect output count behind checkpoint | `output_incomplete` | Reset and rebuilt to completed | Existing records skipped; missing records appended | Replay from start using idempotent sink |
| Corrupt output cache | `test_corrupt_output_cache_fails_fast` | Raise `OutputIntegrityError` | Not started because sink cannot load safely | Unchanged | No silent duplicate index | Human repair or reset output |
| Rate limit budget exhausted | `test_rate_limit_exhaustion_records_recoverable_failure` | Raise `RecoverableApiError` | `page_fetch_failed` | Unchanged at 0 pages / 0 records | No output written | Rerun after API recovers |

## Notes

- Manifest files are append-only JSONL and are optional for normal use.
- Checkpoint and output are intentionally separate. The duplicate-safe sink handles replay when a crash happens between output write and checkpoint save.
- Corrupt output fails closed because silently rebuilding a duplicate index from invalid NDJSON can hide data loss.
