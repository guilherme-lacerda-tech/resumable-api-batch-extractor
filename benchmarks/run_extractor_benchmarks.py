from __future__ import annotations

import argparse
import csv
import json
import os
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from statistics import mean


ROOT = Path(__file__).resolve().parents[1]
RESULTS = ROOT / "benchmarks" / "results"
DOTNET_DLL = (
    ROOT
    / "dotnet"
    / "src"
    / "ResumableExtractor.Worker"
    / "bin"
    / "Release"
    / "net10.0"
    / "ResumableExtractor.Worker.dll"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run synthetic Python vs .NET extractor benchmarks.")
    parser.add_argument("--sizes", default="10000,100000")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--warmup", type=int, default=1)
    parser.add_argument("--page-size", type=int, default=500)
    return parser.parse_args()


def count_output(path: Path) -> tuple[int, int]:
    records = 0
    unique_ids: set[str] = set()
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if not line.strip():
                continue
            payload = json.loads(line)
            records += 1
            unique_ids.add(str(payload.get("id") or payload.get("Id")))
    return records, len(unique_ids)


def run_stack(stack: str, total_records: int, page_size: int, phase: str, run: int) -> dict[str, object]:
    with tempfile.TemporaryDirectory(prefix=f"extractor-{stack}-") as temp:
        temp_root = Path(temp)
        output = temp_root / "records.ndjson"
        checkpoint = temp_root / "state.sqlite3"

        if stack == "python":
            env = os.environ.copy()
            env["PYTHONPATH"] = str(ROOT / "src")
            command = [
                sys.executable,
                "-m",
                "resumable_api_batch_extractor.cli",
                "--reset",
                "--total-records",
                str(total_records),
                "--page-size",
                str(page_size),
                "--output",
                str(output),
                "--checkpoint",
                str(checkpoint),
            ]
        else:
            if not DOTNET_DLL.exists():
                raise FileNotFoundError(f".NET benchmark DLL not found: {DOTNET_DLL}")
            env = None
            command = [
                "dotnet",
                str(DOTNET_DLL),
                "--reset",
                "--total-records",
                str(total_records),
                "--page-size",
                str(page_size),
                "--output",
                str(output),
                "--checkpoint",
                str(checkpoint),
            ]

        started = time.perf_counter()
        completed = subprocess.run(
            command,
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            timeout=120,
            check=False,
        )
        elapsed = time.perf_counter() - started
        if completed.returncode != 0:
            raise RuntimeError(completed.stderr or completed.stdout)

        payload = json.loads(completed.stdout)
        output_count, unique_count = count_output(output)
        correctness = (
            payload["completed"] is True
            and payload["records_written"] == total_records
            and output_count == total_records
            and unique_count == total_records
        )
        return {
            "stack": stack,
            "phase": phase,
            "run": run,
            "records": total_records,
            "page_size": page_size,
            "elapsed_seconds": round(elapsed, 6),
            "records_per_second": round(total_records / elapsed, 2),
            "pages_read": payload["pages_read"],
            "records_written": payload["records_written"],
            "retries": payload.get("retries", 0),
            "skipped_duplicates": payload.get("skipped_duplicates", 0),
            "output_count": output_count,
            "unique_count": unique_count,
            "correctness": correctness,
        }


def summarize(rows: list[dict[str, object]]) -> list[dict[str, object]]:
    measured = [row for row in rows if row["phase"] == "measured"]
    keys = sorted({(row["records"], row["stack"]) for row in measured})
    summary = []
    for records, stack in keys:
        group = [row for row in measured if row["records"] == records and row["stack"] == stack]
        summary.append(
            {
                "records": records,
                "stack": stack,
                "runs": len(group),
                "mean_records_per_second": round(mean(float(row["records_per_second"]) for row in group), 2),
                "mean_elapsed_seconds": round(mean(float(row["elapsed_seconds"]) for row in group), 6),
                "correctness": all(bool(row["correctness"]) for row in group),
            }
        )
    return summary


def write_results(rows: list[dict[str, object]], summary: list[dict[str, object]]) -> tuple[Path, Path, Path, Path]:
    RESULTS.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    raw_path = RESULTS / f"extractor_raw_{stamp}.jsonl"
    summary_json = RESULTS / f"extractor_summary_{stamp}.json"
    summary_csv = RESULTS / f"extractor_summary_{stamp}.csv"
    markdown = ROOT / "benchmarks" / "extractor-results.md"

    with raw_path.open("w", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, sort_keys=True) + "\n")

    summary_json.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    with summary_csv.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(summary[0].keys()))
        writer.writeheader()
        writer.writerows(summary)

    lines = [
        "# Extractor Benchmark Results",
        "",
        f"Generated at `{stamp}` UTC using deterministic synthetic records.",
        "",
        "| Records | Stack | Runs | Mean records/s | Mean elapsed s | Correctness |",
        "| ---: | --- | ---: | ---: | ---: | --- |",
    ]
    for row in summary:
        lines.append(
            f"| {row['records']} | {row['stack']} | {row['runs']} | {row['mean_records_per_second']} | {row['mean_elapsed_seconds']} | {row['correctness']} |"
        )
    lines.extend(
        [
            "",
            "Interpretation: this benchmark measures a local synthetic batch workflow. It should not be mixed with professional production metrics or support-platform benchmarks.",
            "",
            "Canonical-status note: this is a portfolio smoke benchmark, not a final language verdict. The Python path exercises the `httpx.MockTransport` client; the .NET Worker uses an in-memory synthetic page client with SQLite checkpointing. A final canonical comparison should align the transport layer before making throughput claims.",
            "",
            f"Raw rows: `{raw_path.relative_to(ROOT)}`",
            f"Summary JSON: `{summary_json.relative_to(ROOT)}`",
            f"Summary CSV: `{summary_csv.relative_to(ROOT)}`",
        ]
    )
    markdown.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return raw_path, summary_json, summary_csv, markdown


def main() -> None:
    args = parse_args()
    sizes = [int(value.strip()) for value in args.sizes.split(",") if value.strip()]
    rows: list[dict[str, object]] = []
    for records in sizes:
        for stack in ("python", "dotnet"):
            for run in range(1, args.warmup + 1):
                rows.append(run_stack(stack, records, args.page_size, "warmup", run))
            for run in range(1, args.runs + 1):
                rows.append(run_stack(stack, records, args.page_size, "measured", run))
    summary = summarize(rows)
    paths = write_results(rows, summary)
    print(json.dumps({"summary": summary, "paths": [str(path) for path in paths]}, indent=2))


if __name__ == "__main__":
    main()
