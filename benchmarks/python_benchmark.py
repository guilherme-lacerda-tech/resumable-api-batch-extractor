from __future__ import annotations

import argparse
import ctypes
import json
import math
import os
import statistics
import sys
import tempfile
import time
from ctypes import wintypes
from dataclasses import asdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from resumable_api_batch_extractor.checkpoint import SQLiteCheckpointStore  # noqa: E402
from resumable_api_batch_extractor.client import HttpApiClient  # noqa: E402
from resumable_api_batch_extractor.demo_api import (  # noqa: E402
    SyntheticPaginatedApi,
    build_synthetic_records,
)
from resumable_api_batch_extractor.extractor import BatchExtractor, ExtractionInterrupted  # noqa: E402
from resumable_api_batch_extractor.models import CheckpointState, ExtractorConfig  # noqa: E402
from resumable_api_batch_extractor.writers import NdjsonSink  # noqa: E402


class NoopCheckpointStore:
    def load(self, _job_name: str) -> CheckpointState:
        return CheckpointState()

    def save(self, _job_name: str, _cursor: str | None, _pages: int, _records: int) -> None:
        return None

    def mark_completed(self, _job_name: str, _pages: int, _records: int) -> None:
        return None

    def reset(self, _job_name: str) -> None:
        return None


def memory_snapshot() -> dict[str, float | None]:
    if os.name == "nt":
        class ProcessMemoryCounters(ctypes.Structure):
            _fields_ = [
                ("cb", ctypes.c_ulong),
                ("PageFaultCount", ctypes.c_ulong),
                ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t),
            ]

        counters = ProcessMemoryCounters()
        counters.cb = ctypes.sizeof(counters)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel32.GetCurrentProcess.restype = wintypes.HANDLE
        psapi.GetProcessMemoryInfo.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(ProcessMemoryCounters),
            wintypes.DWORD,
        ]
        psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        handle = kernel32.GetCurrentProcess()
        ok = psapi.GetProcessMemoryInfo(
            handle,
            ctypes.byref(counters),
            counters.cb,
        )
        if not ok:
            return {"rss_mb": None, "peak_working_set_mb": None}
        return {
            "rss_mb": counters.WorkingSetSize / 1024 / 1024,
            "peak_working_set_mb": counters.PeakWorkingSetSize / 1024 / 1024,
        }

    try:
        import resource
    except ImportError:
        return {"rss_mb": None, "peak_working_set_mb": None}

    usage = resource.getrusage(resource.RUSAGE_SELF)
    divisor = 1024 if sys.platform != "darwin" else 1024 * 1024
    return {
        "rss_mb": None,
        "peak_working_set_mb": usage.ru_maxrss / divisor,
    }


def timed_run(
    *,
    records: list[dict[str, object]],
    page_size: int,
    checkpoint_enabled: bool,
) -> dict[str, object]:
    with tempfile.TemporaryDirectory(prefix="resumable-extractor-bench-") as temp:
        temp_path = Path(temp)
        config = ExtractorConfig(page_size=page_size, backoff_seconds=0.0)
        store = (
            SQLiteCheckpointStore(temp_path / "state.sqlite3")
            if checkpoint_enabled
            else NoopCheckpointStore()
        )
        sink = NdjsonSink(temp_path / "records.ndjson")
        api = SyntheticPaginatedApi(records)

        memory_before = memory_snapshot()
        wall_started = time.perf_counter()
        cpu_started = time.process_time()
        with HttpApiClient("https://synthetic.local", transport=api.transport()) as client:
            stats = BatchExtractor(client, store, sink, config).run()
        cpu_seconds = time.process_time() - cpu_started
        wall_seconds = time.perf_counter() - wall_started
        memory_after = memory_snapshot()

    pages = math.ceil(len(records) / page_size)
    return {
        "completed": stats.completed,
        "record_count": len(records),
        "page_size": page_size,
        "pages": pages,
        "records_per_second": len(records) / wall_seconds,
        "pages_per_second": pages / wall_seconds,
        "duration_seconds": wall_seconds,
        "cpu_seconds": cpu_seconds,
        "cpu_to_wall_ratio": cpu_seconds / wall_seconds,
        "rss_before_mb": memory_before["rss_mb"],
        "rss_after_mb": memory_after["rss_mb"],
        "rss_delta_mb": (
            memory_after["rss_mb"] - memory_before["rss_mb"]
            if memory_after["rss_mb"] is not None and memory_before["rss_mb"] is not None
            else None
        ),
        "peak_working_set_mb": memory_after["peak_working_set_mb"],
        "checkpoint_enabled": checkpoint_enabled,
        "stats": asdict(stats),
    }


def timed_resume(
    *,
    records: list[dict[str, object]],
    page_size: int,
) -> dict[str, object]:
    pages = math.ceil(len(records) / page_size)
    interrupt_after_pages = max(1, pages // 2)
    with tempfile.TemporaryDirectory(prefix="resumable-extractor-resume-") as temp:
        temp_path = Path(temp)
        config = ExtractorConfig(page_size=page_size, backoff_seconds=0.0)
        store = SQLiteCheckpointStore(temp_path / "state.sqlite3")
        sink = NdjsonSink(temp_path / "records.ndjson")

        first_api = SyntheticPaginatedApi(records)
        with HttpApiClient("https://synthetic.local", transport=first_api.transport()) as client:
            try:
                BatchExtractor(client, store, sink, config).run(
                    interrupt_after_pages=interrupt_after_pages
                )
            except ExtractionInterrupted:
                pass

        resumed_sink = NdjsonSink(temp_path / "records.ndjson")
        resumed_api = SyntheticPaginatedApi(records)
        memory_before = memory_snapshot()
        wall_started = time.perf_counter()
        cpu_started = time.process_time()
        with HttpApiClient("https://synthetic.local", transport=resumed_api.transport()) as client:
            stats = BatchExtractor(client, store, resumed_sink, config).run()
        cpu_seconds = time.process_time() - cpu_started
        wall_seconds = time.perf_counter() - wall_started
        memory_after = memory_snapshot()

    return {
        "interrupt_after_pages": interrupt_after_pages,
        "resume_duration_seconds": wall_seconds,
        "resume_cpu_seconds": cpu_seconds,
        "resume_rss_before_mb": memory_before["rss_mb"],
        "resume_rss_after_mb": memory_after["rss_mb"],
        "resume_rss_delta_mb": (
            memory_after["rss_mb"] - memory_before["rss_mb"]
            if memory_after["rss_mb"] is not None and memory_before["rss_mb"] is not None
            else None
        ),
        "resume_peak_working_set_mb": memory_after["peak_working_set_mb"],
        "resume_pages_read": stats.pages_read,
        "resume_records_written": stats.records_written,
        "resume_skipped_duplicates": stats.skipped_duplicates,
    }


def benchmark_size(record_count: int, page_size: int, repeat: int) -> dict[str, object]:
    records = build_synthetic_records(record_count)
    with_checkpoint = [timed_run(records=records, page_size=page_size, checkpoint_enabled=True)]
    without_checkpoint = [timed_run(records=records, page_size=page_size, checkpoint_enabled=False)]
    for _ in range(repeat - 1):
        with_checkpoint.append(
            timed_run(records=records, page_size=page_size, checkpoint_enabled=True)
        )
        without_checkpoint.append(
            timed_run(records=records, page_size=page_size, checkpoint_enabled=False)
        )

    checkpoint_durations = [result["duration_seconds"] for result in with_checkpoint]
    no_checkpoint_durations = [result["duration_seconds"] for result in without_checkpoint]
    checkpoint_overheads = [
        checkpoint - no_checkpoint
        for checkpoint, no_checkpoint in zip(
            checkpoint_durations,
            no_checkpoint_durations,
            strict=True,
        )
    ]
    representative = min(with_checkpoint, key=lambda result: result["duration_seconds"])

    return {
        "record_count": record_count,
        "page_size": page_size,
        "repeat": repeat,
        "best_with_checkpoint": representative,
        "median_duration_seconds": statistics.median(checkpoint_durations),
        "checkpoint_overhead_seconds_median": statistics.median(checkpoint_overheads),
        "checkpoint_overhead_percent_median": (
            statistics.median(checkpoint_overheads) / statistics.median(no_checkpoint_durations)
        )
        * 100,
        "resume": timed_resume(records=records, page_size=page_size),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Run synthetic Python extractor benchmarks.")
    parser.add_argument("--sizes", type=int, nargs="+", default=[10_000, 100_000])
    parser.add_argument("--page-size", type=int, default=500)
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    payload = {
        "benchmark": "resumable-api-batch-extractor-python",
        "notes": [
            "Synthetic records are generated in memory before timing starts.",
            "Memory is a process RSS snapshot and includes the in-memory synthetic fixture.",
            "Checkpoint overhead is approximated by comparing SQLite checkpoint runs with no-op checkpoint runs.",
        ],
        "results": [benchmark_size(size, args.page_size, args.repeat) for size in args.sizes],
    }
    rendered = json.dumps(payload, indent=2, sort_keys=True)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)


if __name__ == "__main__":
    main()
