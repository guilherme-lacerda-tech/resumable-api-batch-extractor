# Benchmark Contract

Clean-room benchmark plan for the resumable API batch extractor.

This project uses only synthetic paginated APIs and generated records. It must not use real endpoints, tokens, customer data, private schemas, production logs or internal identifiers.

## Workloads

Planned deterministic workloads:

- 1,000 records, page size 100
- 10,000 records, page size 250
- 100,000 records, page size 500
- 1,000,000 records, page size 1,000, if local disk/time is viable

Fault profiles:

- no failures
- transient `429` every N pages
- transient `500` every N pages
- interrupted run after page write and before checkpoint
- interrupted run after checkpoint and before process completion

## Correctness Criteria

Each run must report:

- completed status
- pages read
- records written
- duplicate records skipped
- retry count
- final cursor
- resumed flag
- manifest errors
- output record count
- unique record count

For resume tests, output must remain idempotent: repeated pages cannot create duplicate records.

## Performance Metrics

Collect:

- elapsed time
- records/second
- pages/second
- CPU time
- working set memory
- output file size
- checkpoint write count
- retry count
- errors/timeouts

Run at least:

- 1 warmup
- 5 measured runs per workload/fault profile

## Public Metrics

Safe professional metric already available for LinkedIn/curriculum:

- 4,312 requests, >5.4M records, 0 errors registered in audited manifests.

Do not imply this public repository contains the professional data or endpoint. It is a sanitized portfolio model.
