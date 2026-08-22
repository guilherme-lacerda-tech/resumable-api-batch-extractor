using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ResumableExtractor.Worker;

public sealed class HttpApiPageClient(HttpClient httpClient) : IApiPageClient
{
    public int RetryCount { get; private set; }

    public async Task<ApiPage> FetchPageAsync(
        ExtractorOptions options,
        string? cursor,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= options.RetryAttempts; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(
                    BuildPath(options, cursor),
                    cancellationToken);
                if (IsTransient(response.StatusCode))
                {
                    throw new RecoverableApiException(
                        $"transient status {(int)response.StatusCode} while fetching cursor '{cursor}'");
                }

                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParsePage(content, options);
            }
            catch (Exception exc) when (IsRetryable(exc, cancellationToken))
            {
                lastError = exc;
                if (attempt == options.RetryAttempts)
                {
                    break;
                }

                RetryCount++;
                if (options.BackoffSeconds > 0)
                {
                    var delay = TimeSpan.FromSeconds(options.BackoffSeconds * attempt);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        throw new RecoverableApiException(
            $"API page could not be fetched after retries: {lastError?.Message}",
            lastError);
    }

    private static string BuildPath(ExtractorOptions options, string? cursor)
    {
        var separator = options.Endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.Endpoint}{separator}{Uri.EscapeDataString(options.PageSizeParam)}={options.PageSize}");
        if (cursor is null)
        {
            return path;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{path}&{Uri.EscapeDataString(options.CursorParam)}={Uri.EscapeDataString(cursor)}");
    }

    private static ApiPage ParsePage(string content, ExtractorOptions options)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ApiContractException("API response must be a JSON object");
            }

            if (!root.TryGetProperty(options.RecordsField, out var recordsElement)
                || recordsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ApiContractException(
                    $"API response must include a list field named '{options.RecordsField}'");
            }

            string? nextCursor = null;
            if (root.TryGetProperty(options.CursorField, out var cursorElement)
                && cursorElement.ValueKind != JsonValueKind.Null)
            {
                if (cursorElement.ValueKind != JsonValueKind.String)
                {
                    throw new ApiContractException(
                        $"API cursor field '{options.CursorField}' must be a string or null");
                }

                nextCursor = cursorElement.GetString();
            }

            var records = new List<JsonElement>();
            foreach (var record in recordsElement.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object)
                {
                    throw new ApiContractException("every API record must be a JSON object");
                }

                records.Add(record.Clone());
            }

            return new ApiPage(records, nextCursor);
        }
        catch (JsonException exc)
        {
            throw new ApiContractException("API response body must be valid JSON", exc);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static bool IsRetryable(Exception exc, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exc is HttpRequestException or TaskCanceledException or RecoverableApiException;
    }
}
