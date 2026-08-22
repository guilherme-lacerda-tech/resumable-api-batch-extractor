using Microsoft.Data.Sqlite;

namespace ResumableExtractor.Worker;

public sealed class SqliteCheckpointStore : ICheckpointStore
{
    private readonly string _connectionString;

    public SqliteCheckpointStore(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public async Task<CheckpointState> LoadAsync(
        string jobName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor, completed, pages, records
            FROM checkpoints
            WHERE job_name = $jobName
            """;
        command.Parameters.AddWithValue("$jobName", jobName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CheckpointState();
        }

        return new CheckpointState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt32(1) == 1,
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    public async Task SaveAsync(
        string jobName,
        string? cursor,
        int pages,
        int records,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checkpoints (job_name, cursor, completed, pages, records, updated_at)
            VALUES ($jobName, $cursor, 0, $pages, $records, $updatedAt)
            ON CONFLICT(job_name) DO UPDATE SET
                cursor = excluded.cursor,
                completed = 0,
                pages = excluded.pages,
                records = excluded.records,
                updated_at = excluded.updated_at,
                completed_at = NULL
            """;
        command.Parameters.AddWithValue("$jobName", jobName);
        command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("$pages", pages);
        command.Parameters.AddWithValue("$records", records);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(
        string jobName,
        int pages,
        int records,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checkpoints (
                job_name, cursor, completed, pages, records, updated_at, completed_at
            )
            VALUES ($jobName, NULL, 1, $pages, $records, $updatedAt, $completedAt)
            ON CONFLICT(job_name) DO UPDATE SET
                cursor = NULL,
                completed = 1,
                pages = excluded.pages,
                records = excluded.records,
                updated_at = excluded.updated_at,
                completed_at = excluded.completed_at
            """;
        command.Parameters.AddWithValue("$jobName", jobName);
        command.Parameters.AddWithValue("$pages", pages);
        command.Parameters.AddWithValue("$records", records);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$completedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetAsync(string jobName, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM checkpoints WHERE job_name = $jobName";
        command.Parameters.AddWithValue("$jobName", jobName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
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
}
