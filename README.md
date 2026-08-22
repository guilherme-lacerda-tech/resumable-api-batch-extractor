# Resumable API Batch Extractor

[![CI](https://github.com/guilherme-lacerda-tech/resumable-api-batch-extractor/actions/workflows/ci.yml/badge.svg)](https://github.com/guilherme-lacerda-tech/resumable-api-batch-extractor/actions/workflows/ci.yml)
[![Python](https://img.shields.io/badge/Python-3.11%2B-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![Release](https://img.shields.io/github/v/release/guilherme-lacerda-tech/resumable-api-batch-extractor)](https://github.com/guilherme-lacerda-tech/resumable-api-batch-extractor/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A production-style batch extraction lab for cursor-paginated APIs. It demonstrates resumable checkpoints, retryable HTTP reads, idempotent NDJSON writes and deterministic synthetic API simulation without using any real service, customer system or private dataset.

## Why This Project Exists

Many support and operations tools need to extract large API datasets without losing progress when rate limits, network errors or interrupted jobs happen. This repository shows that workflow as a small, inspectable Python implementation plus a clean-room .NET Worker Service POC:

- Cursor-based pagination.
- SQLite checkpoint state.
- Retry on transient `429` and `5xx` responses.
- Idempotent output that skips records already written.
- Demo API powered by `httpx.MockTransport`.
- Unit tests for resume, retry, contract validation and CLI behavior.
- .NET Worker Service POC with xUnit tests for checkpoint/resume and duplicate-safe output.

## Architecture

See [docs/architecture.md](docs/architecture.md) for the technical overview.

```mermaid
flowchart LR
    CLI["CLI / scheduled job"] --> Extractor["BatchExtractor"]
    Extractor --> Client["HttpApiClient"]
    Client --> API["Synthetic paginated API"]
    Extractor --> Checkpoint["SQLite checkpoint"]
    Extractor --> Sink["NDJSON sink"]
    Sink --> File["records.ndjson"]
```

## Quick Start

```bash
python -m pip install -e ".[dev]"
python examples/run_demo.py
```

Run the CLI directly:

```bash
resumable-extractor-demo --total-records 125 --page-size 25 --output output.ndjson --checkpoint state.sqlite3
```

Expected shape:

```json
{
  "completed": true,
  "pages_read": 5,
  "records_written": 125,
  "last_cursor": null,
  "resumed": false
}
```

More usage examples are in [docs/usage-examples.md](docs/usage-examples.md).

## Validation

```bash
python -m ruff check .
python -m pytest --cov --cov-report=term-missing -q
dotnet build dotnet/ResumableExtractorCleanRoom.slnx -c Release
dotnet test dotnet/ResumableExtractorCleanRoom.slnx -c Release --no-build
```

The coverage gate is set to 88%.

## Benchmarks

A clean-room benchmark contract, backlog and local smoke runner are available:

- [specification/benchmark-contract.md](specification/benchmark-contract.md)
- [benchmarks/BENCHMARK_BACKLOG.md](benchmarks/BENCHMARK_BACKLOG.md)
- [benchmarks/extractor-results.md](benchmarks/extractor-results.md)

Run:

```bash
python benchmarks/run_extractor_benchmarks.py --sizes 10000,100000 --runs 3 --warmup 1 --page-size 500
```

The current benchmark is a smoke benchmark, not a final language verdict. Python exercises the `httpx.MockTransport` path; the .NET Worker uses an in-memory synthetic page client with SQLite checkpointing. A canonical comparison should align the transport layer before making throughput claims.

The public metric boundary is explicit: professional extractor metrics may be summarized separately, but this repository must continue to use only synthetic APIs and generated records.

## Failure Model

The extractor updates output and checkpoint separately. If a process stops after a page is written but before a checkpoint advances, the next run can read the same page again. The NDJSON sink tracks already-written record IDs and skips duplicates, keeping resumed runs idempotent.

See [docs/adr/0001-checkpoint-and-idempotent-output.md](docs/adr/0001-checkpoint-and-idempotent-output.md) for the design decision behind this model.

## Repository Structure

```text
src/resumable_api_batch_extractor/
  checkpoint.py   # SQLite checkpoint persistence
  client.py       # HTTP page client with retry
  cli.py          # Synthetic demo command
  demo_api.py     # In-memory paginated API simulator
  extractor.py    # Resume orchestration
  models.py       # Shared dataclasses
  writers.py      # Idempotent NDJSON output
tests/
  test_client.py
  test_extractor.py
  test_cli.py
dotnet/
  src/ResumableExtractor.Worker/
  tests/ResumableExtractor.Tests/
```

## Security

This project is a public rewrite from scratch using only synthetic data. It does not contain real endpoints, credentials, tokens, customer names, private logs or internal schemas.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
