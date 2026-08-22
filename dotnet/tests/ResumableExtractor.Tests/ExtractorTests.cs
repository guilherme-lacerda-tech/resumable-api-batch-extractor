using ResumableExtractor.Worker;

namespace ResumableExtractor.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task ExtractorWritesAllRecordsAndCompletesCheckpoint()
    {
        var paths = TestPaths();
        var extractor = BuildExtractor(paths, total: 12, pageSize: 5);

        var stats = await extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.Equal(3, stats.PagesRead);
        Assert.Equal(12, stats.RecordsWritten);
        Assert.Equal(12, File.ReadAllLines(paths.Output).Length);
        Assert.True(new SqliteCheckpointStore(paths.Checkpoint).Load().Completed);
    }

    [Fact]
    public async Task ExtractorResumesAfterCheckpointedInterruption()
    {
        var paths = TestPaths();
        var first = BuildExtractor(paths, total: 12, pageSize: 5);

        await Assert.ThrowsAsync<ExtractionInterruptedException>(
            () => first.RunAsync(interruptAfterPages: 2));

        var second = BuildExtractor(paths, total: 12, pageSize: 5);
        var stats = await second.RunAsync();

        Assert.True(stats.Completed);
        Assert.True(stats.Resumed);
        Assert.Equal(1, stats.PagesRead);
        Assert.Equal(2, stats.RecordsWritten);
        Assert.Equal(12, File.ReadAllLines(paths.Output).Length);
    }

    [Fact]
    public async Task DuplicateSafeSinkProtectsCrashAfterWriteBeforeCheckpoint()
    {
        var paths = TestPaths();
        var first = BuildExtractor(paths, total: 12, pageSize: 5);

        await Assert.ThrowsAsync<ExtractionInterruptedException>(
            () => first.RunAsync(1, InterruptMoment.AfterWriteBeforeCheckpoint));

        var second = BuildExtractor(paths, total: 12, pageSize: 5);
        var stats = await second.RunAsync();

        Assert.True(stats.Completed);
        Assert.Equal(12, File.ReadAllLines(paths.Output).Length);
        Assert.Equal(5, stats.SkippedDuplicates);
    }

    [Fact]
    public async Task ClientRetriesTransientFailure()
    {
        var paths = TestPaths();
        var config = new ExtractorConfig(TotalRecords: 5, PageSize: 2, RetryAttempts: 2);
        var client = new SyntheticPageClient(
            SyntheticRecordFactory.Build(config.TotalRecords),
            new Dictionary<int, int> { [0] = 1 });
        var extractor = new BatchExtractor(
            client,
            new JsonCheckpointStore(paths.Checkpoint),
            new NdjsonSink(paths.Output),
            config);

        var stats = await extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.Equal(1, stats.Retries);
        Assert.Equal(1, client.FailuresSeen[0]);
    }

    private static BatchExtractor BuildExtractor((string Output, string Checkpoint) paths, int total, int pageSize)
    {
        var config = new ExtractorConfig(TotalRecords: total, PageSize: pageSize);
        return new BatchExtractor(
            new SyntheticPageClient(SyntheticRecordFactory.Build(total)),
            new SqliteCheckpointStore(paths.Checkpoint),
            new NdjsonSink(paths.Output),
            config);
    }

    private static (string Output, string Checkpoint) TestPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "resumable-dotnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (Path.Combine(root, "records.ndjson"), Path.Combine(root, "checkpoint.sqlite3"));
    }
}
