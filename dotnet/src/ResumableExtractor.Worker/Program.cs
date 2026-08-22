using ResumableExtractor.Worker;
using System.Text.Json;

if (args.Length > 0)
{
    var options = CliOptions.Parse(args);
    if (options.Reset)
    {
        File.Delete(options.Output);
        File.Delete(options.Checkpoint);
    }

    var config = new ExtractorConfig(
        TotalRecords: options.TotalRecords,
        PageSize: options.PageSize);
    var client = new SyntheticPageClient(
        SyntheticRecordFactory.Build(config.TotalRecords),
        new Dictionary<int, int> { [options.PageSize] = 1 });
    var extractor = new BatchExtractor(
        client,
        new SqliteCheckpointStore(options.Checkpoint, config.JobName),
        new NdjsonSink(options.Output),
        config);
    var stats = await extractor.RunAsync();
    var payload = new
    {
        completed = stats.Completed,
        pages_read = stats.PagesRead,
        records_written = stats.RecordsWritten,
        last_cursor = stats.LastCursor,
        resumed = stats.Resumed,
        retries = stats.Retries,
        skipped_duplicates = stats.SkippedDuplicates,
        output = options.Output,
        checkpoint = options.Checkpoint,
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

internal sealed record CliOptions(
    int TotalRecords,
    int PageSize,
    string Output,
    string Checkpoint,
    bool Reset)
{
    public static CliOptions Parse(string[] args)
    {
        var totalRecords = 125;
        var pageSize = 25;
        var output = "output.ndjson";
        var checkpoint = "extractor-state.sqlite3";
        var reset = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--total-records":
                    totalRecords = int.Parse(args[++index]);
                    break;
                case "--page-size":
                    pageSize = int.Parse(args[++index]);
                    break;
                case "--output":
                    output = args[++index];
                    break;
                case "--checkpoint":
                    checkpoint = args[++index];
                    break;
                case "--reset":
                    reset = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        return new CliOptions(totalRecords, pageSize, output, checkpoint, reset);
    }
}
