from resumable_api_batch_extractor.cli import build_parser, run_demo


def test_cli_demo_returns_completed_payload(tmp_path) -> None:
    args = build_parser().parse_args(
        [
            "--reset",
            "--total-records",
            "11",
            "--page-size",
            "5",
            "--output",
            str(tmp_path / "records.ndjson"),
            "--checkpoint",
            str(tmp_path / "state.sqlite3"),
        ]
    )

    payload = run_demo(args)

    assert payload["completed"] is True
    assert payload["records_written"] == 11
    assert payload["pages_read"] == 3
    assert (tmp_path / "records.ndjson").exists()
    assert (tmp_path / "state.sqlite3").exists()

