# Shared Clean-Room Specification

This specification is intentionally generic. It is the contract used by the Python implementation and the independent .NET Worker Service implementation.

## API Contract

- The extractor reads a cursor-paginated HTTP endpoint.
- Request query parameters:
  - `limit`: requested page size.
  - `cursor`: optional cursor returned by the previous page.
- Response JSON fields:
  - `records`: array of JSON objects.
  - `next_cursor`: string cursor or `null` when extraction is complete.
- Each record must contain a stable `id` field.

## Durable State

- SQLite stores one checkpoint per `job_name`.
- Checkpoint fields are cursor, completed flag, page count and record count.
- Checkpoint writes happen after the output page is written.
- A completed checkpoint with fewer output records than checkpoint records is treated as incomplete output and recovered by replaying from the start.

## Output

- Output is NDJSON.
- The sink builds an ID index from existing output before appending.
- Replayed records with IDs already present are skipped.
- Invalid existing NDJSON fails closed because a corrupt duplicate index can hide data loss.

## Retry and Recovery

- Retryable failures: `429`, `500`, `502`, `503`, `504`, timeout and connection-level HTTP failures.
- Contract failures are not retried: invalid JSON, missing records array, invalid cursor type or non-object records.
- Crash after write before checkpoint: replay same page and skip duplicate IDs.
- Crash after checkpoint: resume from saved cursor.

## Manifest

- Manifest is optional append-only JSONL.
- Events include run start, page fetch, page write, checkpoint save, completion, interruption, fetch failure, write failure and incomplete output recovery.

## Concurrency

- Cursor pagination is sequential because each request depends on `next_cursor`.
- The .NET Worker Service uses `BackgroundService` and `IHttpClientFactory`, but does not add artificial page-level concurrency.
- Bounded channels can be added later only if fetch, transform and write become independent stages.

## Benchmark Contract

- Benchmarks use synthetic records generated in memory before timing starts.
- Output and checkpoint files are temporary local files.
- Reported memory is process working set or RSS snapshot, not heap-only allocation.
- Checkpoint overhead is approximated by comparing SQLite checkpoint runs with no-op checkpoint runs.
