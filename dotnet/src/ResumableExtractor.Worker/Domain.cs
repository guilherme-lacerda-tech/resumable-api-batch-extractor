using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ResumableExtractor.Worker;

public sealed record ExtractorConfig(
    int TotalRecords = 125,
    int PageSize = 50,
    int RetryAttempts = 3,
    int? MaxPages = null,
    string IdField = "id",
    string JobName = "synthetic-record-extraction");

public sealed record SyntheticRecord(
    string Id,
    string Status,
    string Region,
    int Priority,
    DateTimeOffset UpdatedAt);

public sealed record PageResult(
    IReadOnlyList<SyntheticRecord> Records,
    string? NextCursor);

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
    int Retries,
    int SkippedDuplicates);

public enum InterruptMoment
{
    AfterCheckpoint,
    AfterWriteBeforeCheckpoint,
}

public sealed class ExtractionInterruptedException(string message) : Exception(message);

public interface IPageClient
{
    int RetryCount { get; }

    Task<PageResult> FetchPageAsync(ExtractorConfig config, string? cursor, CancellationToken cancellationToken);
}

public sealed class SyntheticPageClient : IPageClient
{
    private readonly IReadOnlyList<SyntheticRecord> _records;
    private readonly Dictionary<int, int> _transientFailures;
    private readonly Dictionary<int, int> _failuresSeen = [];

    public SyntheticPageClient(IReadOnlyList<SyntheticRecord> records, Dictionary<int, int>? transientFailures = null)
    {
        _records = records;
        _transientFailures = transientFailures ?? [];
    }

    public int RetryCount { get; private set; }

    public IReadOnlyDictionary<int, int> FailuresSeen => _failuresSeen;

    public Task<PageResult> FetchPageAsync(ExtractorConfig config, string? cursor, CancellationToken cancellationToken)
    {
        var start = cursor is null ? 0 : int.Parse(cursor);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= config.RetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var failuresAllowed = _transientFailures.GetValueOrDefault(start);
                var failuresCurrent = _failuresSeen.GetValueOrDefault(start);
                if (failuresCurrent < failuresAllowed)
                {
                    _failuresSeen[start] = failuresCurrent + 1;
                    throw new RecoverableApiException($"synthetic transient status 503 at cursor {cursor ?? "0"}");
                }

                var batch = _records.Skip(start).Take(config.PageSize).ToArray();
                var stop = start + batch.Length;
                var nextCursor = stop < _records.Count ? stop.ToString() : null;
                return Task.FromResult(new PageResult(batch, nextCursor));
            }
            catch (RecoverableApiException exc) when (attempt < config.RetryAttempts)
            {
                RetryCount++;
                lastError = exc;
            }
            catch (RecoverableApiException exc)
            {
                lastError = exc;
                break;
            }
        }

        throw new RecoverableApiException($"API page could not be fetched after retries: {lastError?.Message}");
    }
}

public sealed class RecoverableApiException(string message) : Exception(message);

public static class SyntheticRecordFactory
{
    public static IReadOnlyList<SyntheticRecord> Build(int total)
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string[] regions = ["north", "south", "east", "west"];
        string[] statuses = ["active", "pending-review", "archived"];

        return Enumerable.Range(0, total)
            .Select(index => new SyntheticRecord(
                $"asset-{index + 1:0000}",
                statuses[index % statuses.Length],
                regions[index % regions.Length],
                1 + (index % 5),
                baseTime.AddMinutes(index * 7)))
            .ToArray();
    }
}

public interface ICheckpointStore
{
    CheckpointState Load();

    void Save(string? cursor, int pages, int records);

    void MarkCompleted(int pages, int records);
}

public sealed class JsonCheckpointStore : ICheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public JsonCheckpointStore(string path)
    {
        _path = path;
        EnsureParentDirectory(_path);
    }

    public CheckpointState Load()
    {
        if (!File.Exists(_path))
        {
            return new CheckpointState();
        }

        var payload = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<CheckpointState>(payload, JsonOptions) ?? new CheckpointState();
    }

    public void Save(string? cursor, int pages, int records)
    {
        Write(new CheckpointState(cursor, Completed: false, pages, records));
    }

    public void MarkCompleted(int pages, int records)
    {
        Write(new CheckpointState(null, Completed: true, pages, records));
    }

    private void Write(CheckpointState state)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}

public sealed class SqliteCheckpointStore : ICheckpointStore
{
    private readonly string _path;
    private readonly string _jobName;

    public SqliteCheckpointStore(string path, string jobName = "synthetic-record-extraction")
    {
        _path = path;
        _jobName = jobName;
        EnsureParentDirectory(_path);
        Initialize();
    }

    public CheckpointState Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor, completed, pages, records
            FROM checkpoints
            WHERE job_name = $jobName
            """;
        command.Parameters.AddWithValue("$jobName", _jobName);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new CheckpointState();
        }

        return new CheckpointState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt32(1) == 1,
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    public void Save(string? cursor, int pages, int records)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checkpoints (job_name, cursor, completed, pages, records, updated_at, completed_at)
            VALUES ($jobName, $cursor, 0, $pages, $records, $updatedAt, NULL)
            ON CONFLICT(job_name) DO UPDATE SET
                cursor = excluded.cursor,
                completed = 0,
                pages = excluded.pages,
                records = excluded.records,
                updated_at = excluded.updated_at,
                completed_at = NULL
            """;
        BindState(command, cursor, completed: false, pages, records);
        command.ExecuteNonQuery();
    }

    public void MarkCompleted(int pages, int records)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checkpoints (job_name, cursor, completed, pages, records, updated_at, completed_at)
            VALUES ($jobName, NULL, 1, $pages, $records, $updatedAt, $completedAt)
            ON CONFLICT(job_name) DO UPDATE SET
                cursor = NULL,
                completed = 1,
                pages = excluded.pages,
                records = excluded.records,
                updated_at = excluded.updated_at,
                completed_at = excluded.completed_at
            """;
        BindState(command, cursor: null, completed: true, pages, records);
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS checkpoints (
                job_name TEXT PRIMARY KEY,
                cursor TEXT,
                completed INTEGER NOT NULL,
                pages INTEGER NOT NULL,
                records INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT
            )
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        return connection;
    }

    private void BindState(
        SqliteCommand command,
        string? cursor,
        bool completed,
        int pages,
        int records)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$jobName", _jobName);
        command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$pages", pages);
        command.Parameters.AddWithValue("$records", records);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$completedAt", completed ? now : DBNull.Value);
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}

public sealed class NdjsonSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly HashSet<string> _seenIds;

    public NdjsonSink(string path)
    {
        _path = path;
        EnsureParentDirectory(_path);
        _seenIds = LoadSeenIds();
    }

    public int RecordCount => _seenIds.Count;

    public int SkippedDuplicates { get; private set; }

    public int WriteMany(IEnumerable<SyntheticRecord> records)
    {
        var written = 0;
        using var stream = new StreamWriter(_path, append: true);
        foreach (var record in records)
        {
            if (!_seenIds.Add(record.Id))
            {
                SkippedDuplicates++;
                continue;
            }

            stream.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            written++;
        }

        return written;
    }

    private HashSet<string> LoadSeenIds()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<SyntheticRecord>(line, JsonOptions);
            if (record is not null)
            {
                seen.Add(record.Id);
            }
        }

        return seen;
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}

public sealed class BatchExtractor
{
    private readonly IPageClient _client;
    private readonly ICheckpointStore _checkpointStore;
    private readonly NdjsonSink _sink;
    private readonly ExtractorConfig _config;

    public BatchExtractor(
        IPageClient client,
        ICheckpointStore checkpointStore,
        NdjsonSink sink,
        ExtractorConfig config)
    {
        _client = client;
        _checkpointStore = checkpointStore;
        _sink = sink;
        _config = config;
    }

    public async Task<ExtractionStats> RunAsync(
        int? interruptAfterPages = null,
        InterruptMoment interruptMoment = InterruptMoment.AfterCheckpoint,
        CancellationToken cancellationToken = default)
    {
        var state = _checkpointStore.Load();
        if (state.Completed)
        {
            return new ExtractionStats(true, 0, 0, null, true, _client.RetryCount, _sink.SkippedDuplicates);
        }

        var cursor = state.Cursor;
        var totalPages = state.Pages;
        var pagesThisRun = 0;
        var resumed = cursor is not null || state.Pages > 0 || state.Records > 0;

        while (true)
        {
            if (_config.MaxPages is not null && pagesThisRun >= _config.MaxPages)
            {
                return Stats(false, pagesThisRun, _sink.RecordCount - state.Records, cursor, resumed);
            }

            var page = await _client.FetchPageAsync(_config, cursor, cancellationToken);
            _sink.WriteMany(page.Records);
            totalPages++;
            pagesThisRun++;
            cursor = page.NextCursor;

            if (interruptAfterPages is not null
                && pagesThisRun >= interruptAfterPages
                && interruptMoment == InterruptMoment.AfterWriteBeforeCheckpoint)
            {
                throw new ExtractionInterruptedException(
                    $"simulated interruption after write before checkpoint at page {pagesThisRun}");
            }

            if (cursor is null)
            {
                _checkpointStore.MarkCompleted(totalPages, _sink.RecordCount);
                return Stats(true, pagesThisRun, _sink.RecordCount - state.Records, null, resumed);
            }

            _checkpointStore.Save(cursor, totalPages, _sink.RecordCount);

            if (interruptAfterPages is not null
                && pagesThisRun >= interruptAfterPages
                && interruptMoment == InterruptMoment.AfterCheckpoint)
            {
                throw new ExtractionInterruptedException(
                    $"simulated interruption after checkpoint at page {pagesThisRun}");
            }
        }
    }

    private ExtractionStats Stats(
        bool completed,
        int pagesRead,
        int recordsWritten,
        string? cursor,
        bool resumed)
    {
        return new ExtractionStats(
            completed,
            pagesRead,
            recordsWritten,
            cursor,
            resumed,
            _client.RetryCount,
            _sink.SkippedDuplicates);
    }
}
