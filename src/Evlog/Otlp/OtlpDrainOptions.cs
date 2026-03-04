namespace Evlog.Otlp;

public sealed class OtlpDrainOptions
{
    /// <summary>OTLP HTTP endpoint (e.g. "http://localhost:4318").</summary>
    public string? Endpoint { get; set; }

    /// <summary>Override event's service name for the OTLP resource.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Custom HTTP headers (e.g. for authentication).</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Extra OTEL resource attributes.</summary>
    public Dictionary<string, string>? ResourceAttributes { get; set; }

    /// <summary>HTTP request timeout in milliseconds. Default: 5000.</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Resolves configuration from OTEL environment variables for any unset properties.
    /// </summary>
    public void ResolveFromEnvironment()
    {
        Endpoint ??= Env("OTEL_EXPORTER_OTLP_ENDPOINT");
        ServiceName ??= Env("OTEL_SERVICE_NAME");

        if (Headers is null)
        {
            var headersEnv = Env("OTEL_EXPORTER_OTLP_HEADERS");
            if (headersEnv is not null)
            {
                Headers = ParseHeaders(headersEnv);
            }
        }
    }

    internal static Dictionary<string, string>? ParseHeaders(string raw)
    {
        var decoded = Uri.UnescapeDataString(raw);
        var result = new Dictionary<string, string>();

        foreach (var pair in decoded.Split(','))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = pair[..eqIndex].Trim();
            var value = pair[(eqIndex + 1)..].Trim();

            if (key.Length > 0 && value.Length > 0)
                result[key] = value;
        }

        return result.Count > 0 ? result : null;
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
