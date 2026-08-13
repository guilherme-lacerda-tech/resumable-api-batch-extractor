from resumable_api_batch_extractor.cli import main


if __name__ == "__main__":
    main(["--reset", "--output", "demo-output.ndjson", "--checkpoint", "demo-state.sqlite3"])

