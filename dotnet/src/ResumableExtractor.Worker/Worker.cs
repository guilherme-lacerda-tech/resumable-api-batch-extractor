namespace ResumableExtractor.Worker;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var outputRoot = Path.Combine(AppContext.BaseDirectory, "synthetic-output");
        var config = new ExtractorConfig();
        var client = new SyntheticPageClient(SyntheticRecordFactory.Build(config.TotalRecords));
        var checkpoint = new SqliteCheckpointStore(Path.Combine(outputRoot, "checkpoint.sqlite3"), config.JobName);
        var sink = new NdjsonSink(Path.Combine(outputRoot, "records.ndjson"));
        var extractor = new BatchExtractor(client, checkpoint, sink, config);

        var stats = await extractor.RunAsync(cancellationToken: stoppingToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Synthetic extraction completed={Completed} pages={Pages} records={Records} retries={Retries} duplicates={Duplicates}",
                stats.Completed,
                stats.PagesRead,
                stats.RecordsWritten,
                stats.Retries,
                stats.SkippedDuplicates);
        }
    }
}
