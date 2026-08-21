# Benchmark Backlog

Status: prepared, not executed in this pass.

The main benchmark effort was prioritized in `support-operations-intelligence-platform`. This backlog defines the next benchmark step for the resumable extractor without creating a large parallel implementation.

## Tasks

1. Add deterministic workload generator.
2. Add runner for no-failure extraction.
3. Add runner for transient `429` and `5xx` retry profiles.
4. Add interrupted-run scenario after output write and before checkpoint.
5. Add interrupted-run scenario after checkpoint update.
6. Emit raw JSONL benchmark rows.
7. Emit CSV/Markdown summary.
8. Validate output idempotency by comparing total and unique record IDs.
9. Add CI-safe smoke benchmark using a small workload.
10. Document public metric boundaries for LinkedIn and curriculum.

## Proposed Commands

```bash
python benchmarks/generate_workloads.py --sizes 1000,10000,100000
python benchmarks/run_extractor_benchmarks.py --workloads 1000,10000 --runs 5 --warmup 1
python benchmarks/run_extractor_benchmarks.py --workloads 10000 --fault-profile transient-429 --runs 5
python benchmarks/run_resume_faults.py --workload 10000
```

## Outputs

Planned output files:

- `benchmarks/results/extractor_raw_<timestamp>.jsonl`
- `benchmarks/results/extractor_summary_<timestamp>.json`
- `benchmarks/results/extractor_summary_<timestamp>.csv`
- `benchmarks/extractor-results.md`

## LinkedIn/CV Use

Use the professional metric carefully:

> Extrator resiliente com 4.312 requisicoes, >5.4M registros e 0 erros registrados nos manifestos auditados.

Avoid saying that the public repository contains the professional dataset or endpoint. The public repository is a clean, synthetic portfolio version.
