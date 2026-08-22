using System.Text.Json;

namespace ResumableExtractor.Worker;

public sealed class NdjsonRecordSink : IRecordSink
{
    private readonly string _path;
    private readonly string _idField;
    private readonly HashSet<string> _seenIds;

    public NdjsonRecordSink(string path, string idField = "id")
    {
        _path = path;
        _idField = idField;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _seenIds = LoadSeenIds();
    }

    public int RecordCount => _seenIds.Count;

    public int SkippedDuplicates { get; private set; }

    public async Task<int> WriteManyAsync(
        IReadOnlyList<JsonElement> records,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var newIds = new List<string>();
        var pendingIds = new HashSet<string>();

        foreach (var record in records)
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                throw new ApiContractException("every API record must be a JSON object");
            }

            var id = ExtractId(record);
            if (_seenIds.Contains(id) || pendingIds.Contains(id))
            {
                SkippedDuplicates++;
                continue;
            }

            pendingIds.Add(id);
            newIds.Add(id);
            lines.Add(record.GetRawText());
        }

        if (lines.Count > 0)
        {
            await File.AppendAllLinesAsync(_path, lines, cancellationToken);
            foreach (var id in newIds)
            {
                _seenIds.Add(id);
            }
        }

        return newIds.Count;
    }

    private HashSet<string> LoadSeenIds()
    {
        var seen = new HashSet<string>();
        if (!File.Exists(_path))
        {
            return seen;
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(_path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new OutputIntegrityException(
                        $"output file {_path} contains a non-object record on line {lineNumber}");
                }

                if (root.TryGetProperty(_idField, out var idElement)
                    && idElement.ValueKind != JsonValueKind.Null)
                {
                    seen.Add(ReadIdValue(idElement));
                }
            }
            catch (JsonException exc)
            {
                throw new OutputIntegrityException(
                    $"output file {_path} contains invalid JSON on line {lineNumber}",
                    exc);
            }
        }

        return seen;
    }

    private string ExtractId(JsonElement record)
    {
        if (!record.TryGetProperty(_idField, out var idElement)
            || idElement.ValueKind == JsonValueKind.Null)
        {
            throw new ApiContractException($"record is missing id field '{_idField}'");
        }

        return ReadIdValue(idElement);
    }

    private static string ReadIdValue(JsonElement idElement)
    {
        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            JsonValueKind.Number => idElement.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => idElement.GetRawText()
        };
    }
}
