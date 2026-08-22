# Extractor Benchmark Results

Generated at `20260822-013649` UTC using deterministic synthetic records.

| Records | Stack | Runs | Mean records/s | Mean elapsed s | Correctness |
| ---: | --- | ---: | ---: | ---: | --- |
| 10000 | dotnet | 3 | 8596.75 | 1.181214 | True |
| 10000 | python | 3 | 3878.45 | 2.593887 | True |
| 100000 | dotnet | 3 | 32193.14 | 3.234546 | True |
| 100000 | python | 3 | 10926.69 | 9.290516 | True |

Interpretation: this benchmark measures a local synthetic batch workflow. It should not be mixed with professional production metrics or support-platform benchmarks.

Canonical-status note: this is a portfolio smoke benchmark, not a final language verdict. The Python path exercises the `httpx.MockTransport` client; the .NET Worker uses an in-memory synthetic page client with SQLite checkpointing. A final canonical comparison should align the transport layer before making throughput claims.

Raw rows: `benchmarks\results\extractor_raw_20260822-013649.jsonl`
Summary JSON: `benchmarks\results\extractor_summary_20260822-013649.json`
Summary CSV: `benchmarks\results\extractor_summary_20260822-013649.csv`
