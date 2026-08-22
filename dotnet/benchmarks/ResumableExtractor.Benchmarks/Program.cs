using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ResumableExtractor.Worker;

namespace ResumableExtractor.Benchmarks;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task Main(string[] args)
    {
        var sizes = new List<int> { 10_000, 100_000 };
        var pageSize = 5_000;
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--sizes")
            {
                sizes.Clear();
                while (index + 1 < args.Length
                    && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    sizes.Add(int.Parse(args[++index], CultureInfo.InvariantCulture));
                }
            }
            else if (args[index] == "--page-size" && index + 1 < args.Length)
            {
                pageSize = int.Parse(args[++index], CultureInfo.InvariantCulture);
            }
            else if (args[index] == "--output" && index + 1 < args.Length)
            {
                output = args[++index];
            }
        }

        var results = new List<SizeBenchmark>();
        foreach (var size in sizes)
        {
            results.Add(await BenchmarkSize(size, pageSize));
        }

        var payload = new BenchmarkPayload(
            "resumable-api-batch-extractor-dotnet",
            [
                "Synthetic records are generated in memory before timing starts.",
                "Memory is a process working-set snapshot and includes the in-memory synthetic fixture.",
                "Checkpoint overhead is approximated by comparing SQLite checkpoint runs with no-op checkpoint runs."
            ],
            results);
        var rendered = JsonSerializer.Serialize(payload, JsonOptions);
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, rendered + Environment.NewLine);
        }

        Console.WriteLine(rendered);
    }

    private static async Task<SizeBenchmark> BenchmarkSize(int recordCount, int pageSize)
    {
        var records = BuildRecords(recordCount);
        var withCheckpoint = await TimedRun(records, pageSize, checkpointEnabled: true);
        var withoutCheckpoint = await TimedRun(records, pageSize, checkpointEnabled: false);
        var checkpointOverhead = withCheckpoint.DurationSeconds - withoutCheckpoint.DurationSeconds;
        return new SizeBenchmark(
            recordCount,
            pageSize,
            withCheckpoint,
            checkpointOverhead,
            checkpointOverhead / withoutCheckpoint.DurationSeconds * 100,
            await TimedResume(records, pageSize));
    }

    private static async Task<RunMetrics> TimedRun(
        IReadOnlyList<Dictionary<string, object?>> records,
        int pageSize,
        bool checkpointEnabled)
    {
        var root = CreateTempDirectory("resumable-extractor-dotnet-bench");
        try
        {
            var options = BuildOptions(root, pageSize);
            ICheckpointStore store = checkpointEnabled
                ? new SqliteCheckpointStore(options.CheckpointPath)
                : new NoopCheckpointStore();
            var sink = new NdjsonRecordSink(options.OutputPath, options.IdField);
            using var httpClient = new HttpClient(
                new SyntheticHandler(request => PageResponse(records, request)))
            {
                BaseAddress = new Uri(options.BaseUrl)
            };
            var client = new HttpApiPageClient(httpClient);
            var extractor = new ResumableBatchExtractor(
                client,
                store,
                sink,
                options,
                new NullManifestRecorder());

            var memoryBefore = MemorySnapshot();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            var stats = await extractor.RunAsync();
            stopwatch.Stop();
            var cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
            var memoryAfter = MemorySnapshot();
            var pages = (int)Math.Ceiling(records.Count / (double)pageSize);

            return new RunMetrics(
                checkpointEnabled,
                stats.Completed,
                records.Count,
                pageSize,
                pages,
                records.Count / stopwatch.Elapsed.TotalSeconds,
                pages / stopwatch.Elapsed.TotalSeconds,
                stopwatch.Elapsed.TotalSeconds,
                (cpuAfter - cpuBefore).TotalSeconds,
                memoryBefore.RssMb,
                memoryAfter.RssMb,
                memoryAfter.RssMb - memoryBefore.RssMb,
                memoryAfter.PeakWorkingSetMb,
                stats);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ResumeMetrics> TimedResume(
        IReadOnlyList<Dictionary<string, object?>> records,
        int pageSize)
    {
        var root = CreateTempDirectory("resumable-extractor-dotnet-resume");
        try
        {
            var options = BuildOptions(root, pageSize);
            var interruptAfterPages = Math.Max(
                1,
                (int)Math.Ceiling(records.Count / (double)pageSize) / 2);

            var firstStore = new SqliteCheckpointStore(options.CheckpointPath);
            var firstSink = new NdjsonRecordSink(options.OutputPath, options.IdField);
            using (var firstHttpClient = new HttpClient(
                new SyntheticHandler(request => PageResponse(records, request)))
            {
                BaseAddress = new Uri(options.BaseUrl)
            })
            {
                var firstExtractor = new ResumableBatchExtractor(
                    new HttpApiPageClient(firstHttpClient),
                    firstStore,
                    firstSink,
                    options,
                    new NullManifestRecorder());
                try
                {
                    await firstExtractor.RunAsync(interruptAfterPages: interruptAfterPages);
                }
                catch (ExtractionInterruptedException)
                {
                }
            }

            var resumedStore = new SqliteCheckpointStore(options.CheckpointPath);
            var resumedSink = new NdjsonRecordSink(options.OutputPath, options.IdField);
            using var resumedHttpClient = new HttpClient(
                new SyntheticHandler(request => PageResponse(records, request)))
            {
                BaseAddress = new Uri(options.BaseUrl)
            };
            var resumedExtractor = new ResumableBatchExtractor(
                new HttpApiPageClient(resumedHttpClient),
                resumedStore,
                resumedSink,
                options,
                new NullManifestRecorder());

            var memoryBefore = MemorySnapshot();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            var stats = await resumedExtractor.RunAsync();
            stopwatch.Stop();
            var cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
            var memoryAfter = MemorySnapshot();

            return new ResumeMetrics(
                interruptAfterPages,
                stopwatch.Elapsed.TotalSeconds,
                (cpuAfter - cpuBefore).TotalSeconds,
                memoryBefore.RssMb,
                memoryAfter.RssMb,
                memoryAfter.RssMb - memoryBefore.RssMb,
                memoryAfter.PeakWorkingSetMb,
                stats.PagesRead,
                stats.RecordsWritten,
                stats.SkippedDuplicates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExtractorOptions BuildOptions(string root, int pageSize)
    {
        return new ExtractorOptions
        {
            PageSize = pageSize,
            OutputPath = Path.Combine(root, "records.ndjson"),
            CheckpointPath = Path.Combine(root, "state.sqlite3"),
            ManifestPath = null
        };
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
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        records = records.Skip(start).Take(stop - start),
                        next_cursor = nextCursor
                    }),
                Encoding.UTF8,
                "application/json")
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

    private static MemoryMetrics MemorySnapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemoryMetrics(
            process.WorkingSet64 / 1024.0 / 1024.0,
            process.PeakWorkingSet64 / 1024.0 / 1024.0);
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class NoopCheckpointStore : ICheckpointStore
    {
        public Task<CheckpointState> LoadAsync(string jobName, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CheckpointState());
        }

        public Task SaveAsync(
            string jobName,
            string? cursor,
            int pages,
            int records,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MarkCompletedAsync(
            string jobName,
            int pages,
            int records,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ResetAsync(string jobName, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

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

    private sealed record MemoryMetrics(double RssMb, double PeakWorkingSetMb);

    private sealed record BenchmarkPayload(
        string Benchmark,
        IReadOnlyList<string> Notes,
        IReadOnlyList<SizeBenchmark> Results);

    private sealed record SizeBenchmark(
        int RecordCount,
        int PageSize,
        RunMetrics BestWithCheckpoint,
        double CheckpointOverheadSeconds,
        double CheckpointOverheadPercent,
        ResumeMetrics Resume);

    private sealed record RunMetrics(
        bool CheckpointEnabled,
        bool Completed,
        int RecordCount,
        int PageSize,
        int Pages,
        double RecordsPerSecond,
        double PagesPerSecond,
        double DurationSeconds,
        double CpuSeconds,
        double RssBeforeMb,
        double RssAfterMb,
        double RssDeltaMb,
        double PeakWorkingSetMb,
        ExtractionStats Stats);

    private sealed record ResumeMetrics(
        int InterruptAfterPages,
        double ResumeDurationSeconds,
        double ResumeCpuSeconds,
        double ResumeRssBeforeMb,
        double ResumeRssAfterMb,
        double ResumeRssDeltaMb,
        double ResumePeakWorkingSetMb,
        int ResumePagesRead,
        int ResumeRecordsWritten,
        int ResumeSkippedDuplicates);
}
