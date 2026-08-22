using ResumableExtractor.Worker;

var builder = Host.CreateApplicationBuilder(args);
var options = builder.Configuration.GetSection("Extractor").Get<ExtractorOptions>() ?? new();
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<HttpApiPageClient>(
    client =>
    {
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    });
builder.Services.AddSingleton<IApiPageClient>(
    serviceProvider => serviceProvider.GetRequiredService<HttpApiPageClient>());
builder.Services.AddSingleton<ICheckpointStore>(
    _ => new SqliteCheckpointStore(options.CheckpointPath));
builder.Services.AddSingleton<IRecordSink>(
    _ => new NdjsonRecordSink(options.OutputPath, options.IdField));
builder.Services.AddSingleton<IManifestRecorder>(
    _ => string.IsNullOrWhiteSpace(options.ManifestPath)
        ? new NullManifestRecorder()
        : new ManifestRecorder(options.ManifestPath));
builder.Services.AddSingleton<ResumableBatchExtractor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
