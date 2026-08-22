using System.Text.Json;

namespace ResumableExtractor.Worker;

public sealed class ManifestRecorder(string path) : IManifestRecorder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _path = path;

    public async Task RecordAsync(
        string eventName,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
        foreach (var field in fields)
        {
            payload[field.Key] = field.Value;
        }

        var line = JsonSerializer.Serialize(payload, SerializerOptions);
        await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken);
    }
}

public sealed class NullManifestRecorder : IManifestRecorder
{
    public Task RecordAsync(
        string eventName,
        IReadOnlyDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
