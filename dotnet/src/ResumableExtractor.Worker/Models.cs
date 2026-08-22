using System.Text.Json;

namespace ResumableExtractor.Worker;

public sealed record ExtractorOptions
{
    public string BaseUrl { get; init; } = "https://synthetic.local";
    public string Endpoint { get; init; } = "/records";
    public string JobName { get; init; } = "synthetic-record-extraction";
    public int PageSize { get; init; } = 500;
    public int? MaxPages { get; init; }
    public int RetryAttempts { get; init; } = 3;
    public double BackoffSeconds { get; init; }
    public double RequestTimeoutSeconds { get; init; } = 5;
    public string CursorParam { get; init; } = "cursor";
    public string PageSizeParam { get; init; } = "limit";
    public string CursorField { get; init; } = "next_cursor";
    public string RecordsField { get; init; } = "records";
    public string IdField { get; init; } = "id";
    public string OutputPath { get; init; } = "output.ndjson";
    public string CheckpointPath { get; init; } = "extractor-state.sqlite3";
    public string? ManifestPath { get; init; } = "manifest.jsonl";
}

public sealed record ApiPage(IReadOnlyList<JsonElement> Records, string? NextCursor);

public sealed record CheckpointState(
    string? Cursor = null,
    bool Completed = false,
    int Pages = 0,
    int Records = 0);

public sealed record ExtractionStats(
    bool Completed,
    int PagesRead,
    int RecordsWritten,
    string? LastCursor,
    bool Resumed,
    int Retries = 0,
    int SkippedDuplicates = 0);
