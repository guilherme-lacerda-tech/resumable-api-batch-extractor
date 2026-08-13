from __future__ import annotations

import argparse
import json
from dataclasses import asdict
from pathlib import Path

from resumable_api_batch_extractor.checkpoint import SQLiteCheckpointStore
from resumable_api_batch_extractor.client import HttpApiClient
from resumable_api_batch_extractor.demo_api import SyntheticPaginatedApi, build_synthetic_records
from resumable_api_batch_extractor.extractor import BatchExtractor
from resumable_api_batch_extractor.models import ExtractorConfig
from resumable_api_batch_extractor.writers import NdjsonSink


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run a synthetic resumable API extraction demo.")
    parser.add_argument("--total-records", type=int, default=125)
    parser.add_argument("--page-size", type=int, default=25)
    parser.add_argument("--output", default="output.ndjson")
    parser.add_argument("--checkpoint", default="extractor-state.sqlite3")
    parser.add_argument("--reset", action="store_true")
    return parser


def run_demo(args: argparse.Namespace) -> dict[str, object]:
    output = Path(args.output)
    checkpoint = Path(args.checkpoint)
    if args.reset:
        output.unlink(missing_ok=True)
        checkpoint.unlink(missing_ok=True)

    config = ExtractorConfig(page_size=args.page_size)
    api = SyntheticPaginatedApi(
        build_synthetic_records(args.total_records),
        transient_failures={args.page_size: 1},
    )
    store = SQLiteCheckpointStore(checkpoint)
    sink = NdjsonSink(output, id_field=config.id_field)

    with HttpApiClient("https://synthetic.local", transport=api.transport()) as client:
        extractor = BatchExtractor(client, store, sink, config)
        stats = extractor.run()

    payload = asdict(stats)
    payload["output"] = str(output)
    payload["checkpoint"] = str(checkpoint)
    return payload


def main(argv: list[str] | None = None) -> None:
    args = build_parser().parse_args(argv)
    print(json.dumps(run_demo(args), indent=2, sort_keys=True))


if __name__ == "__main__":
    main()

