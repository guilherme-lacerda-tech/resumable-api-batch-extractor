using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ResumableExtractor.Worker;

namespace ResumableExtractor.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task CompletesAndPersistsCheckpoint()
    {
        using var workspace = new TestWorkspace();
        var records = BuildRecords(9);
        var harness = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);

        var stats = await harness.Extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.Equal(3, stats.PagesRead);
        Assert.Equal(9, stats.RecordsWritten);
        Assert.Equal(9, ReadOutput(harness.OutputPath).Count);
        var checkpoint = await harness.Store.LoadAsync("synthetic-record-extraction", default);
        Assert.True(checkpoint.Completed);
        Assert.Equal(9, checkpoint.Records);
    }

    [Fact]
    public async Task RetriesRateLimitAndWritesManifest()
    {
        using var workspace = new TestWorkspace();
        var records = BuildRecords(8);
        var calls = 0;
        var harness = workspace.BuildHarness(
            request =>
            {
                calls++;
                if (calls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = JsonContent(new { error = "synthetic rate limit" })
                    };
                }

                return PageResponse(records, request);
            },
            pageSize: 4);

        var stats = await harness.Extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.Equal(1, stats.Retries);
        Assert.Contains(ReadManifest(harness.ManifestPath), item => item["event"] == "run_completed");
        Assert.Contains(
            ReadManifest(harness.ManifestPath),
            item => item["event"] == "checkpoint_completed");
    }

    [Fact]
    public async Task CrashAfterWriteBeforeCheckpointReplaysAndSkipsDuplicates()
    {
        using var workspace = new TestWorkspace();
        var records = BuildRecords(8);
        var first = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);

        await Assert.ThrowsAsync<ExtractionInterruptedException>(
            () => first.Extractor.RunAsync(interruptAfterWritePages: 1));

        var checkpoint = await first.Store.LoadAsync("synthetic-record-extraction", default);
        Assert.Null(checkpoint.Cursor);
        Assert.Equal(0, checkpoint.Pages);
        Assert.Equal(0, checkpoint.Records);
        Assert.Equal(4, ReadOutput(first.OutputPath).Count);

        var second = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);
        var stats = await second.Extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.True(stats.Resumed);
        Assert.Equal(4, stats.RecordsWritten);
        Assert.Equal(4, stats.SkippedDuplicates);
        Assert.Equal(8, ReadOutput(second.OutputPath).Count);
        Assert.Contains(
            ReadManifest(second.ManifestPath),
            item => item["event"] == "interrupted"
                && item["stage"] == "after_write_before_checkpoint");
    }

    [Fact]
    public async Task CrashAfterCheckpointResumesNextPageWithoutDuplicateWrites()
    {
        using var workspace = new TestWorkspace();
        var records = BuildRecords(8);
        var first = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);

        await Assert.ThrowsAsync<ExtractionInterruptedException>(
            () => first.Extractor.RunAsync(interruptAfterPages: 1));

        var checkpoint = await first.Store.LoadAsync("synthetic-record-extraction", default);
        Assert.Equal("4", checkpoint.Cursor);
        Assert.Equal(1, checkpoint.Pages);
        Assert.Equal(4, checkpoint.Records);

        var second = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);
        var stats = await second.Extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.True(stats.Resumed);
        Assert.Equal(1, stats.PagesRead);
        Assert.Equal(4, stats.RecordsWritten);
        Assert.Equal(0, stats.SkippedDuplicates);
        Assert.Contains(
            ReadManifest(second.ManifestPath),
            item => item["event"] == "interrupted" && item["stage"] == "after_checkpoint");
    }

    [Fact]
    public async Task InvalidPayloadDoesNotAdvanceCheckpoint()
    {
        using var workspace = new TestWorkspace();
        var harness = workspace.BuildHarness(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"records\":", Encoding.UTF8, "application/json")
            },
            pageSize: 4);

        await Assert.ThrowsAsync<ApiContractException>(() => harness.Extractor.RunAsync());

        var checkpoint = await harness.Store.LoadAsync("synthetic-record-extraction", default);
        Assert.Equal(0, checkpoint.Pages);
        Assert.Equal(0, checkpoint.Records);
        Assert.False(File.Exists(harness.OutputPath));
        Assert.Contains(
            ReadManifest(harness.ManifestPath),
            item => item["event"] == "page_fetch_failed"
                && item["error_type"] == nameof(ApiContractException));
    }

    [Fact]
    public async Task CompletedCheckpointWithIncompleteOutputReplaysAndFillsCache()
    {
        using var workspace = new TestWorkspace();
        var records = BuildRecords(12);
        var first = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);
        Assert.True((await first.Extractor.RunAsync()).Completed);

        var lines = File.ReadAllLines(first.OutputPath).Take(10);
        await File.WriteAllLinesAsync(first.OutputPath, lines);

        var second = workspace.BuildHarness(request => PageResponse(records, request), pageSize: 4);
        var stats = await second.Extractor.RunAsync();

        Assert.True(stats.Completed);
        Assert.True(stats.Resumed);
        Assert.Equal(2, stats.RecordsWritten);
        Assert.Equal(10, stats.SkippedDuplicates);
        Assert.Equal(12, ReadOutput(second.OutputPath).Count);
        Assert.Contains(ReadManifest(second.ManifestPath), item => item["event"] == "output_incomplete");
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildRecords(int total)
    {
        return Enumerable.Range(0, total)
            .Select(index => new Dictionary<string, object?>
            {
                ["id"] = $"asset-{index + 1:0000}",
                ["status"] = index % 2 == 0 ? "active" : "pending-review",
                ["priority"] = 1 + (index % 5)
            })
            .ToList();
    }

    private static HttpResponseMessage PageResponse(
        IReadOnlyList<Dictionary<string, object?>> records,
        HttpRequestMessage request)
    {
        var query = ParseQuery(request.RequestUri?.Query ?? string.Empty);
        var start = query.TryGetValue("cursor", out var cursor)
            ? int.Parse(cursor, CultureInfo.InvariantCulture)
            : 0;
        var limit = query.TryGetValue("limit", out var limitValue)
            ? int.Parse(limitValue, CultureInfo.InvariantCulture)
            : 50;
        var stop = Math.Min(start + limit, records.Count);
        var nextCursor = stop < records.Count ? stop.ToString(CultureInfo.InvariantCulture) : null;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                records = records.Skip(start).Take(stop - start),
                next_cursor = nextCursor
            })
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : string.Empty);
    }

    private static StringContent JsonContent(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private static List<Dictionary<string, JsonElement>> ReadOutput(string path)
    {
        return File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!)
            .ToList();
    }

    private static List<Dictionary<string, string>> ReadManifest(string path)
    {
        return File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!)
            .Select(
                item => item.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ValueKind == JsonValueKind.String
                        ? pair.Value.GetString() ?? string.Empty
                        : pair.Value.GetRawText()))
            .ToList();
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"extractor-dotnet-{Guid.NewGuid():N}");

        public TestWorkspace()
        {
            Directory.CreateDirectory(_root);
        }

        public Harness BuildHarness(
            Func<HttpRequestMessage, HttpResponseMessage> handler,
            int pageSize,
            int retryAttempts = 3)
        {
            var outputPath = Path.Combine(_root, "records.ndjson");
            var checkpointPath = Path.Combine(_root, "state.sqlite3");
            var manifestPath = Path.Combine(_root, "manifest.jsonl");
            var options = new ExtractorOptions
            {
                PageSize = pageSize,
                RetryAttempts = retryAttempts,
                OutputPath = outputPath,
                CheckpointPath = checkpointPath,
                ManifestPath = manifestPath
            };
            var httpClient = new HttpClient(new SyntheticHandler(handler))
            {
                BaseAddress = new Uri(options.BaseUrl)
            };
            var client = new HttpApiPageClient(httpClient);
            var store = new SqliteCheckpointStore(checkpointPath);
            var sink = new NdjsonRecordSink(outputPath, options.IdField);
            var manifest = new ManifestRecorder(manifestPath);
            var extractor = new ResumableBatchExtractor(client, store, sink, options, manifest);
            return new Harness(extractor, store, outputPath, manifestPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed record Harness(
        ResumableBatchExtractor Extractor,
        SqliteCheckpointStore Store,
        string OutputPath,
        string ManifestPath);

    private sealed class SyntheticHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
