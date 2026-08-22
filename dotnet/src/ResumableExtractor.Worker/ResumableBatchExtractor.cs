namespace ResumableExtractor.Worker;

public sealed class ResumableBatchExtractor(
    IApiPageClient client,
    ICheckpointStore checkpointStore,
    IRecordSink sink,
    ExtractorOptions options,
    IManifestRecorder manifest)
{
    public async Task<ExtractionStats> RunAsync(
        int? interruptAfterPages = null,
        int? interruptAfterWritePages = null,
        CancellationToken cancellationToken = default)
    {
        var state = await checkpointStore.LoadAsync(options.JobName, cancellationToken);
        var recoveredIncompleteOutput = false;
        if (state.Completed && sink.RecordCount < state.Records)
        {
            await RecordAsync(
                "output_incomplete",
                new Dictionary<string, object?>
                {
                    ["checkpoint_records"] = state.Records,
                    ["output_records"] = sink.RecordCount,
                    ["recovery"] = "reset_checkpoint_and_replay_from_start"
                },
                cancellationToken);
            await checkpointStore.ResetAsync(options.JobName, cancellationToken);
            state = state with { Completed = false, Cursor = null, Pages = 0, Records = sink.RecordCount };
            recoveredIncompleteOutput = true;
        }

        if (state.Completed)
        {
            await RecordAsync(
                "run_skipped_completed",
                new Dictionary<string, object?>
                {
                    ["checkpoint_records"] = state.Records,
                    ["output_records"] = sink.RecordCount
                },
                cancellationToken);
            return new ExtractionStats(
                Completed: true,
                PagesRead: 0,
                RecordsWritten: 0,
                LastCursor: null,
                Resumed: true,
                Retries: client.RetryCount,
                SkippedDuplicates: sink.SkippedDuplicates);
        }

        var cursor = state.Cursor;
        var totalPages = state.Pages;
        var pagesThisRun = 0;
        var startingRecordCount = sink.RecordCount;
        var resumed = recoveredIncompleteOutput
            || startingRecordCount > 0
            || cursor is not null
            || state.Pages > 0
            || state.Records > 0;

        await RecordAsync(
            "run_started",
            new Dictionary<string, object?>
            {
                ["cursor"] = cursor,
                ["checkpoint_pages"] = state.Pages,
                ["checkpoint_records"] = state.Records,
                ["output_records"] = startingRecordCount,
                ["resumed"] = resumed
            },
            cancellationToken);

        while (true)
        {
            if (options.MaxPages is not null && pagesThisRun >= options.MaxPages)
            {
                return BuildStats(
                    completed: false,
                    pagesRead: pagesThisRun,
                    recordsWritten: sink.RecordCount - startingRecordCount,
                    cursor: cursor,
                    resumed: resumed);
            }

            var cursorBeforeFetch = cursor;
            await RecordAsync(
                "page_fetch_started",
                new Dictionary<string, object?> { ["cursor"] = cursorBeforeFetch },
                cancellationToken);

            ApiPage page;
            try
            {
                page = await client.FetchPageAsync(options, cursor, cancellationToken);
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                await RecordAsync(
                    "page_fetch_failed",
                    new Dictionary<string, object?>
                    {
                        ["cursor"] = cursorBeforeFetch,
                        ["error_type"] = exc.GetType().Name,
                        ["error"] = exc.Message
                    },
                    cancellationToken);
                throw;
            }

            int recordsWritten;
            try
            {
                recordsWritten = await sink.WriteManyAsync(page.Records, cancellationToken);
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                await RecordAsync(
                    "page_write_failed",
                    new Dictionary<string, object?>
                    {
                        ["cursor"] = cursorBeforeFetch,
                        ["error_type"] = exc.GetType().Name,
                        ["error"] = exc.Message
                    },
                    cancellationToken);
                throw;
            }

            totalPages++;
            pagesThisRun++;
            cursor = page.NextCursor;
            await RecordAsync(
                "page_written",
                new Dictionary<string, object?>
                {
                    ["cursor"] = cursorBeforeFetch,
                    ["next_cursor"] = cursor,
                    ["records_fetched"] = page.Records.Count,
                    ["records_written"] = recordsWritten,
                    ["skipped_duplicates"] = sink.SkippedDuplicates
                },
                cancellationToken);

            if (interruptAfterWritePages is not null && pagesThisRun >= interruptAfterWritePages)
            {
                await RecordAsync(
                    "interrupted",
                    new Dictionary<string, object?>
                    {
                        ["stage"] = "after_write_before_checkpoint",
                        ["pages_this_run"] = pagesThisRun,
                        ["cursor"] = cursor,
                        ["output_records"] = sink.RecordCount
                    },
                    cancellationToken);
                throw new ExtractionInterruptedException(
                    $"simulated interruption after writing {pagesThisRun} page(s) before checkpoint");
            }

            if (cursor is null)
            {
                await checkpointStore.MarkCompletedAsync(
                    options.JobName,
                    totalPages,
                    sink.RecordCount,
                    cancellationToken);
                var stats = BuildStats(
                    completed: true,
                    pagesRead: pagesThisRun,
                    recordsWritten: sink.RecordCount - startingRecordCount,
                    cursor: null,
                    resumed: resumed);
                await RecordAsync(
                    "checkpoint_completed",
                    new Dictionary<string, object?>
                    {
                        ["pages"] = totalPages,
                        ["records"] = sink.RecordCount
                    },
                    cancellationToken);
                await RecordAsync(
                    "run_completed",
                    new Dictionary<string, object?>
                    {
                        ["pages_read"] = pagesThisRun,
                        ["records_written"] = stats.RecordsWritten,
                        ["skipped_duplicates"] = sink.SkippedDuplicates,
                        ["retries"] = client.RetryCount
                    },
                    cancellationToken);
                return stats;
            }

            await checkpointStore.SaveAsync(
                options.JobName,
                cursor,
                totalPages,
                sink.RecordCount,
                cancellationToken);
            await RecordAsync(
                "checkpoint_saved",
                new Dictionary<string, object?>
                {
                    ["cursor"] = cursor,
                    ["pages"] = totalPages,
                    ["records"] = sink.RecordCount
                },
                cancellationToken);

            if (interruptAfterPages is not null && pagesThisRun >= interruptAfterPages)
            {
                var stats = BuildStats(
                    completed: false,
                    pagesRead: pagesThisRun,
                    recordsWritten: sink.RecordCount - startingRecordCount,
                    cursor: cursor,
                    resumed: resumed);
                await RecordAsync(
                    "interrupted",
                    new Dictionary<string, object?>
                    {
                        ["stage"] = "after_checkpoint",
                        ["pages_this_run"] = pagesThisRun,
                        ["cursor"] = cursor,
                        ["output_records"] = sink.RecordCount
                    },
                    cancellationToken);
                throw new ExtractionInterruptedException(
                    $"simulated interruption after {pagesThisRun} page(s)",
                    stats);
            }
        }
    }

    private ExtractionStats BuildStats(
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
            client.RetryCount,
            sink.SkippedDuplicates);
    }

    private Task RecordAsync(
        string eventName,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["job_name"] = options.JobName };
        foreach (var field in fields)
        {
            payload[field.Key] = field.Value;
        }

        return manifest.RecordAsync(eventName, payload, cancellationToken);
    }
}
