# Evlog v0.0.2-beta — Product Spec

## Overview

Evlog v0.0.2-beta adds an OTLP drain adapter that sends wide events to any OpenTelemetry-compatible backend (Grafana, Datadog, Honeycomb, Axiom, etc.).

## Problem

Evlog v0.0.1-beta only outputs wide events to stdout. Users who want to send events to external observability platforms must implement custom drain logic including OTLP protocol mapping, HTTP transport, and error handling.

## Solution

A built-in `OtlpDrain` adapter that:
- Maps wide events to OTLP log records following the OpenTelemetry specification
- POSTs JSON directly to any OTLP-compatible endpoint
- Requires zero OpenTelemetry SDK dependencies (uses raw HttpClient + System.Text.Json)
- Supports configuration via code or OTEL environment variables

## User Experience

### Basic usage

```csharp
builder.Services.AddEvlog(options =>
{
    options.Service = "my-api";
    options.Drain = OtlpDrain.Create(otlp =>
    {
        otlp.Endpoint = "http://localhost:4318";
        otlp.Headers = new() { ["Authorization"] = "Bearer token" };
    });
});
```

### Environment variable configuration

Set `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`, and `OTEL_EXPORTER_OTLP_HEADERS` — the adapter reads them automatically.

## Requirements

1. Map wide events to OTLP `ExportLogsServiceRequest` JSON format
2. Extract resource attributes from well-known event fields (service, environment, version, region, commitHash)
3. Map evlog levels to OTLP severity numbers (Debug=5, Info=9, Warn=13, Error=17)
4. Include full event JSON as `body.stringValue`
5. POST to `{endpoint}/v1/logs` with `Content-Type: application/json`
6. Support custom HTTP headers for authentication
7. Support custom resource attributes
8. Fire-and-forget with non-propagating errors (logs to stderr)
9. Support OTEL environment variable conventions as fallbacks
10. Zero external dependencies beyond System.Net.Http and System.Text.Json

## Non-Goals

- Batching/buffering (single event per request, matching v0.0.1 drain semantics)
- OpenTelemetry SDK integration
- Metrics or traces export
- gRPC transport (JSON/HTTP only)
