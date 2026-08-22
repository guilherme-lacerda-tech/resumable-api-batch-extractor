namespace ResumableExtractor.Worker;

public sealed class Worker(
    ResumableBatchExtractor extractor,
    ILogger<Worker> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stats = await extractor.RunAsync(cancellationToken: stoppingToken);
            logger.LogInformation(
                "Extraction completed={Completed} pages={Pages} records={Records} retries={Retries} duplicates={Duplicates}",
                stats.Completed,
                stats.PagesRead,
                stats.RecordsWritten,
                stats.Retries,
                stats.SkippedDuplicates);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Extraction worker stopped by cancellation.");
        }
        catch (Exception exc)
        {
            logger.LogError(exc, "Extraction worker failed.");
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
