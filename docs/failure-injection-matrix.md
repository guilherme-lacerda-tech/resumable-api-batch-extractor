# Failure Injection Matrix

Clean-room scenarios for the resumable extractor. All data is synthetic.

| Scenario | Python | .NET Worker | Expected behavior |
| --- | --- | --- | --- |
| No failure | Covered | Covered | Completed run, output count equals unique count. |
| Transient 429/5xx | Covered via retryable statuses | Covered via synthetic 503 retry | Retry within budget and continue. |
| Timeout/connection reset | Planned | Planned | Treat as recoverable until retry budget is exhausted. |
| Invalid payload | Covered | Planned | Fail fast as contract error; do not advance checkpoint. |
| Partial payload | Planned | Planned | Reject or quarantine before checkpoint advance. |
| Crash after output write before checkpoint | Covered by duplicate-safe sink test | Covered by duplicate-safe sink test | Resume may read the page again; sink skips duplicates. |
| Crash after checkpoint | Covered | Covered | Resume from saved cursor without rewriting previous pages. |
| Incomplete/corrupt output | Planned | Planned | Detect during sink bootstrap and require review/repair. |
| Rate limit exhausted | Covered via retry budget | Planned | Raise recoverable failure and keep checkpoint unchanged. |

The matrix is intentionally small and reproducible. It documents resilience behavior without using real APIs, credentials, endpoints or production logs.
