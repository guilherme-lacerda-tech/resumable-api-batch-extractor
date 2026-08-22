namespace ResumableExtractor.Worker;

public interface IApiPageClient
{
    int RetryCount { get; }

    Task<ApiPage> FetchPageAsync(
        ExtractorOptions options,
        string? cursor,
        CancellationToken cancellationToken);
}

public interface ICheckpointStore
{
    Task<CheckpointState> LoadAsync(string jobName, CancellationToken cancellationToken);

    Task SaveAsync(
        string jobName,
        string? cursor,
        int pages,
        int records,
        CancellationToken cancellationToken);

    Task MarkCompletedAsync(
        string jobName,
        int pages,
        int records,
        CancellationToken cancellationToken);

    Task ResetAsync(string jobName, CancellationToken cancellationToken);
}

public interface IRecordSink
{
    int RecordCount { get; }

    int SkippedDuplicates { get; }

    Task<int> WriteManyAsync(
        IReadOnlyList<System.Text.Json.JsonElement> records,
        CancellationToken cancellationToken);
}

public interface IManifestRecorder
{
    Task RecordAsync(
        string eventName,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken cancellationToken);
}
