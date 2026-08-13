# Usage Examples

## Fresh Run

```bash
resumable-extractor-demo --reset --total-records 125 --page-size 25 --output output.ndjson --checkpoint state.sqlite3
```

Expected shape:

```json
{
  "completed": true,
  "pages_read": 5,
  "records_written": 125,
  "last_cursor": null,
  "resumed": false,
  "retries": 1,
  "skipped_duplicates": 0
}
```

## Smaller Batch

```bash
resumable-extractor-demo --reset --total-records 11 --page-size 5 --output small.ndjson --checkpoint small.sqlite3
```

Expected behavior:

- 3 pages are read.
- 11 synthetic records are written.
- The second cursor page triggers one synthetic retry.

## Inspect Output

```bash
python -m json.tool output.ndjson
```

NDJSON contains one JSON object per line, so tools that expect a JSON array may need line-by-line processing.

