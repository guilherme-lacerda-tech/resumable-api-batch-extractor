# ADR 0001: SQLite Checkpoints and Idempotent NDJSON Output

## Status

Accepted

## Context

API batch jobs often fail for ordinary reasons: rate limits, transient server errors, network instability or a stopped process. A portfolio extractor should demonstrate how to resume safely without needing a production queue or external database.

## Decision

Use SQLite for checkpoint state and an NDJSON sink that tracks already-written record IDs before appending new records.

## Consequences

- The project remains fully local and reproducible.
- Checkpoint state can be inspected with common SQLite tools.
- NDJSON output is append-friendly and easy to diff.
- The sink protects against duplicated output if a page is replayed during resume.
- The design is intentionally serial; concurrency would add complexity that the current demo does not need.

