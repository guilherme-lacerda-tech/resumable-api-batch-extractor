FROM python:3.12-slim

WORKDIR /app
COPY . .
RUN python -m pip install --no-cache-dir -e .

CMD ["resumable-extractor-demo", "--output", "/tmp/synthetic-records.ndjson", "--checkpoint", "/tmp/extractor-state.sqlite3"]

