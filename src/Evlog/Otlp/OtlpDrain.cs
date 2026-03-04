using System.Net.Http.Headers;
using System.Text.Json;

namespace Evlog.Otlp;

public static class OtlpDrain
{
    private static readonly HttpClient SharedClient = new();

    // Fields to exclude from log record attributes (mapped to resource or severity/timestamp)
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.Ordinal)
    {
        "timestamp", "level", "service", "environment", "version", "commitHash", "region"
    };

    public static EvlogDrainDelegate Create(OtlpDrainOptions options)
    {
        options.ResolveFromEnvironment();

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new ArgumentException("OTLP endpoint is required. Set OtlpDrainOptions.Endpoint or OTEL_EXPORTER_OTLP_ENDPOINT env var.", nameof(options));

        var url = options.Endpoint!.TrimEnd('/') + "/v1/logs";
        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);

        return async context =>
        {
            try
            {
                var payload = BuildPayload(context, options);
                var json = JsonSerializer.SerializeToUtf8Bytes(payload);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new ByteArrayContent(json);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                if (options.Headers is not null)
                {
                    foreach (var (key, value) in options.Headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }

                using var cts = new CancellationTokenSource(timeout);
                using var response = await SharedClient.SendAsync(request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token);
                    if (body.Length > 200) body = body[..200];
                    Console.Error.WriteLine($"[evlog/otlp] API error: {(int)response.StatusCode} {response.ReasonPhrase} — {body}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[evlog/otlp] send error: {ex.Message}");
            }
        };
    }

    public static EvlogDrainDelegate Create(Action<OtlpDrainOptions> configure)
    {
        var options = new OtlpDrainOptions();
        configure(options);
        return Create(options);
    }

    internal static object BuildPayload(EvlogDrainContext context, OtlpDrainOptions options)
    {
        using var doc = JsonDocument.Parse(context.EventJson);
        var root = doc.RootElement;

        var resourceAttributes = BuildResourceAttributes(root, options);
        var logRecord = BuildLogRecord(root, context);

        return new
        {
            resourceLogs = new object[]
            {
                new
                {
                    resource = new { attributes = resourceAttributes },
                    scopeLogs = new object[]
                    {
                        new
                        {
                            scope = new { name = "evlog", version = "0.0.2-beta" },
                            logRecords = new object[] { logRecord }
                        }
                    }
                }
            }
        };
    }

    internal static List<object> BuildResourceAttributes(JsonElement root, OtlpDrainOptions options)
    {
        var attributes = new List<object>();

        // service.name — config override or event field
        var serviceName = options.ServiceName
            ?? (root.TryGetProperty("service", out var svc) ? svc.GetString() : null)
            ?? "unknown";
        attributes.Add(MakeAttribute("service.name", serviceName));

        // deployment.environment
        if (root.TryGetProperty("environment", out var env) && env.GetString() is { } envStr)
            attributes.Add(MakeAttribute("deployment.environment", envStr));

        // service.version
        if (root.TryGetProperty("version", out var ver) && ver.GetString() is { } verStr)
            attributes.Add(MakeAttribute("service.version", verStr));

        // cloud.region
        if (root.TryGetProperty("region", out var reg) && reg.GetString() is { } regStr)
            attributes.Add(MakeAttribute("cloud.region", regStr));

        // vcs.commit.id
        if (root.TryGetProperty("commitHash", out var commit) && commit.GetString() is { } commitStr)
            attributes.Add(MakeAttribute("vcs.commit.id", commitStr));

        // Custom resource attributes from config
        if (options.ResourceAttributes is not null)
        {
            foreach (var (key, value) in options.ResourceAttributes)
                attributes.Add(MakeAttribute(key, value));
        }

        return attributes;
    }

    internal static object BuildLogRecord(JsonElement root, EvlogDrainContext context)
    {
        // Timestamp → nanoseconds
        var timeUnixNano = "0";
        if (root.TryGetProperty("timestamp", out var ts) && ts.GetString() is { } tsStr)
        {
            if (DateTimeOffset.TryParse(tsStr, out var dto))
                timeUnixNano = (dto.ToUnixTimeMilliseconds() * 1_000_000).ToString();
        }

        var (severityNumber, severityText) = MapSeverity(context.Level);

        // Body = full event JSON as string
        var bodyJson = System.Text.Encoding.UTF8.GetString(context.EventJson.Span);

        // Attributes = all fields except excluded ones
        var attributes = new List<object>();
        foreach (var prop in root.EnumerateObject())
        {
            if (ExcludedFields.Contains(prop.Name)) continue;
            attributes.Add(new
            {
                key = prop.Name,
                value = ToAttributeValue(prop.Value)
            });
        }

        return new
        {
            timeUnixNano,
            severityNumber,
            severityText,
            body = new { stringValue = bodyJson },
            attributes
        };
    }

    internal static (int number, string text) MapSeverity(EvlogLevel level) => level switch
    {
        EvlogLevel.Debug => (5, "DEBUG"),
        EvlogLevel.Info => (9, "INFO"),
        EvlogLevel.Warn => (13, "WARN"),
        EvlogLevel.Error => (17, "ERROR"),
        _ => (9, "INFO"),
    };

    internal static object ToAttributeValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => new { boolValue = true },
        JsonValueKind.False => new { boolValue = false },
        JsonValueKind.Number when element.TryGetInt64(out var l) => (object)new { intValue = l.ToString() },
        JsonValueKind.Number => new { stringValue = element.GetDouble().ToString() },
        JsonValueKind.String => new { stringValue = element.GetString()! },
        _ => new { stringValue = element.GetRawText() },
    };

    private static object MakeAttribute(string key, string value) => new
    {
        key,
        value = new { stringValue = value }
    };
}
