# Evlog v0.0.2-beta — Technical Spec

## Architecture

The OTLP adapter lives in the `Evlog.Otlp` namespace with two files:

- `OtlpDrainOptions` — configuration POCO with env var resolution
- `OtlpDrain` — static factory that returns an `EvlogDrainDelegate`

No new dependencies are introduced. The adapter uses `System.Net.Http.HttpClient` and `System.Text.Json` which are part of the .NET runtime.

## Files

| File | Purpose |
|------|---------|
| `src/Evlog/Otlp/OtlpDrainOptions.cs` | Configuration class |
| `src/Evlog/Otlp/OtlpDrain.cs` | Drain factory + OTLP mapping |
| `tests/Evlog.Tests/OtlpDrainTests.cs` | Unit + integration tests |

## OtlpDrainOptions

```csharp
public sealed class OtlpDrainOptions
{
    public string? Endpoint { get; set; }
    public string? ServiceName { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? ResourceAttributes { get; set; }
    public int TimeoutMs { get; set; } = 5000;
    public void ResolveFromEnvironment();
}
```

Environment variable fallbacks:
- `OTEL_EXPORTER_OTLP_ENDPOINT` → Endpoint
- `OTEL_SERVICE_NAME` → ServiceName
- `OTEL_EXPORTER_OTLP_HEADERS` → Headers (comma-separated `key=value`, URL-decoded)

## OtlpDrain

### Factory methods

```csharp
public static EvlogDrainDelegate Create(OtlpDrainOptions options);
public static EvlogDrainDelegate Create(Action<OtlpDrainOptions> configure);
```

### Event mapping

The drain receives `EvlogDrainContext` containing raw UTF-8 JSON bytes and parses them to construct the OTLP envelope.

### OTLP JSON envelope

```json
{
  "resourceLogs": [{
    "resource": {
      "attributes": [
        { "key": "service.name", "value": { "stringValue": "..." } },
        { "key": "deployment.environment", "value": { "stringValue": "..." } },
        { "key": "service.version", "value": { "stringValue": "..." } },
        { "key": "cloud.region", "value": { "stringValue": "..." } },
        { "key": "vcs.commit.id", "value": { "stringValue": "..." } }
      ]
    },
    "scopeLogs": [{
      "scope": { "name": "evlog", "version": "0.0.2-beta" },
      "logRecords": [{
        "timeUnixNano": "1772625600000000000",
        "severityNumber": 9,
        "severityText": "INFO",
        "body": { "stringValue": "{...full event JSON...}" },
        "attributes": [
          { "key": "method", "value": { "stringValue": "GET" } },
          { "key": "status", "value": { "intValue": "200" } }
        ]
      }]
    }]
  }]
}
```

### Severity mapping

| EvlogLevel | severityNumber | severityText |
|------------|---------------|--------------|
| Debug | 5 | DEBUG |
| Info | 9 | INFO |
| Warn | 13 | WARN |
| Error | 17 | ERROR |

### Resource attribute extraction

| Event field | OTLP resource attribute |
|-------------|------------------------|
| `service` | `service.name` |
| `environment` | `deployment.environment` |
| `version` | `service.version` |
| `region` | `cloud.region` |
| `commitHash` | `vcs.commit.id` |

### Attribute value conversion

| JSON type | OTLP value |
|-----------|------------|
| string | `{ stringValue: "..." }` |
| integer | `{ intValue: "..." }` |
| boolean | `{ boolValue: true/false }` |
| object/array | `{ stringValue: "<json>" }` |

### HTTP transport

- POST to `{endpoint}/v1/logs`
- `Content-Type: application/json`
- Custom headers added to request
- Timeout via `CancellationTokenSource`
- Shared `HttpClient` instance (follows .NET best practices)
- Errors caught and logged to stderr (non-propagating)

## Error handling

The drain follows fire-and-forget semantics matching the existing `DrainSafeAsync` pattern in `EvlogMiddleware`. All exceptions are caught and logged to stderr with `[evlog/otlp]` prefix. HTTP error responses include the status code and truncated response body.

## Testing

- Unit tests for OTLP JSON structure, severity mapping, resource attributes, attribute value conversion
- Environment variable fallback tests
- Header parsing tests (comma-separated, URL-encoded)
- Error handling tests (missing endpoint, non-routable host)
- Integration test with `HttpListener` mock OTLP collector
