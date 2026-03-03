# Evlog for .NET — Technical Spec

**Version:** 0.0.1-beta
**Author:** Rodrigo Vidal
**Date:** 2026-03-03
**Status:** Shipped

## Overview

.NET port of evlog's wide events structured logging for ASP.NET Core. Single NuGet package targeting .NET 10 with idiomatic C# APIs, dependency injection, and near-zero-allocation hot paths.

## Technical Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Target framework | .NET 10 | Latest LTS, access to modern APIs |
| Packaging | Single NuGet package (`Evlog`) | Simplicity, no fragmentation |
| API style | Idiomatic C# with DI | Familiar to .NET developers |
| ILogger integration | `ILoggerProvider` implementation | Captures third-party library logs transparently |
| Request context storage | `HttpContext.Items` + middleware | Standard ASP.NET Core pattern, no `AsyncLocal` needed |
| Error HTTP mapping | `EvlogError` → `ProblemDetails` | RFC 9457 compliance |
| JSON serialization | `System.Text.Json` / `Utf8JsonWriter` | Built-in, high performance, no dependencies |

## Performance Architecture

| Concern | Approach |
|---------|----------|
| Avoid boxing value types | Typed `Set()` overloads + `ContextEntry` discriminated-union struct |
| Avoid per-request allocation | `ObjectPool<RequestLogger>` with `IResettable` |
| Zero-alloc sampled-out path | `_active` bool flag; all methods `[AggressiveInlining]` return immediately |
| JSON output | `Utf8JsonWriter` + `ArrayBufferWriter<byte>` backed by `ArrayPool` |
| Property names | Pre-encoded as `static readonly JsonEncodedText` |
| String formatting | `IUtf8SpanFormattable` for value types, no intermediate strings |
| Nested JSON from flat keys | Dot-notation expanded at emit time (once per request, cold path) |
| Request log entries | Pooled `List<RequestLogEntry>` cleared on reset |
| Pretty output | Direct `Console.Out` writes with ANSI escape codes |

### Hybrid Set() API

The `RequestLogger` exposes three paths with different performance characteristics:

- **`Set(object)`** — ergonomic, mirrors the TypeScript API. Allocates the anonymous object but the JSON serialization path is efficient via `Utf8JsonWriter` into a pooled fragment buffer.
- **`Set(string key, T value)`** — zero-alloc typed overloads for `string`, `int`, `long`, `double`, `bool`. Values stored in the `ContextEntry` discriminated-union struct without boxing. Dot-notation keys (e.g. `"user.id"`) are expanded into nested JSON at emit time.
- **`SetJson(string key, Action<Utf8JsonWriter>)`** — zero-alloc for complex objects via direct `Utf8JsonWriter` access.

When a request is sampled out (head sampling), the logger's `_active` flag is `false` and all methods are inlined no-ops.

## Project Structure

```
evlog.net/
├── src/Evlog/
│   ├── Evlog.csproj
│   ├── ContextEntry.cs          # Discriminated-union value struct
│   ├── EvlogError.cs            # Structured error with Why/Fix
│   ├── EvlogLevel.cs            # Debug, Info, Warn, Error enum
│   ├── EvlogLogger.cs           # ILogger implementation
│   ├── EvlogLoggerProvider.cs   # ILoggerProvider implementation
│   ├── EvlogMiddleware.cs       # Request lifecycle middleware
│   ├── EvlogOptions.cs          # Configuration options
│   ├── EvlogServiceExtensions.cs # AddEvlog() / UseEvlog()
│   ├── GlobMatcher.cs           # Path pattern matching for sampling
│   ├── HttpContextExtensions.cs # GetEvlogLogger() extension
│   ├── PrettyOutputFormatter.cs # Dev colored tree output
│   ├── RequestLogEntry.cs       # Captured ILogger entries
│   ├── RequestLogger.cs         # Per-request context accumulator
│   ├── Sampling.cs              # Head/tail sampling logic
│   ├── SamplingOptions.cs       # Sampling configuration
│   └── WideEventWriter.cs      # Utf8JsonWriter-based JSON emitter
├── tests/Evlog.Tests/
│   └── Evlog.Tests.csproj
├── .github/workflows/
│   ├── ci.yml                   # Build + test on every push
│   └── publish.yml              # Pack + push to NuGet on version tags
└── Evlog.sln
```

## Core Types

### ContextEntry

Discriminated-union struct that stores typed values without boxing:

```csharp
public readonly struct ContextEntry
{
    public string Key { get; }
    public ContextValueKind Kind { get; }

    // Value stored in the appropriate field based on Kind
    public string? StringValue { get; }
    public long IntValue { get; }      // Also used for bool (0/1)
    public double DoubleValue { get; }
    public int FragmentOffset { get; }  // For JsonFragment kind
    public int FragmentLength { get; }

    // Factory methods
    public static ContextEntry String(string key, string value);
    public static ContextEntry Int(string key, int value);
    public static ContextEntry Long(string key, long value);
    public static ContextEntry Double(string key, double value);
    public static ContextEntry Bool(string key, bool value);
    public static ContextEntry JsonFragment(string key, int offset, int length);
}
```

### RequestLogger

Pooled via `ObjectPool<RequestLogger>` with `IResettable`. Accumulates context entries and request log entries during a request, then emits a single wide event.

### WideEventWriter

Writes the final JSON output using `Utf8JsonWriter`. Handles:
- Well-known fields (timestamp, level, service, method, path, status, duration)
- Dot-notation expansion (flat keys → nested JSON objects)
- JSON fragment splicing (from `Set(object)` serialization)
- Request log entries array
- Error details

### EvlogMiddleware

Middleware lifecycle:
1. **Before request**: Rent `RequestLogger` from pool, store in `HttpContext.Items`, start `Stopwatch`, generate request ID
2. **During request**: Code calls `context.GetEvlogLogger()` to accumulate context
3. **After response**: Compute duration, set status code, evaluate tail sampling, emit event
4. **On exception**: Capture error into wide event, set level to `Error`. If `EvlogError`, write `ProblemDetails` response
5. **Drain**: Fire-and-forget call to configured callback. Errors logged to stderr
6. **Always**: Return `RequestLogger` to pool

### EvlogLoggerProvider / EvlogLogger

`ILoggerProvider` that creates `EvlogLogger` instances. During an HTTP request, log calls are captured as `RequestLogEntry` into the current `RequestLogger`. Outside a request context, log calls emit standalone JSON entries to stdout.

## Sampling

### Head Sampling

Evaluated before the request runs. Random percentage per `EvlogLevel`. Failed head sampling → `RequestLogger._active = false` → all methods are inlined no-ops (zero-alloc fast path).

### Tail Sampling

Evaluated after the response. Conditions use OR logic:
- `Status` — keep if response status code >= configured value
- `Duration` — keep if request duration >= configured milliseconds
- `Path` — keep if request path matches glob pattern

If any condition matches, the event is force-emitted regardless of head sampling result.

## Environment Auto-Detection

When options are not explicitly set, the following environment variables are read:

| Variable | Maps to |
|----------|---------|
| `SERVICE_NAME` | `EvlogOptions.Service` |
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | `EvlogOptions.Environment` |
| `APP_VERSION` | `EvlogOptions.Version` |
| `COMMIT_SHA` / `GITHUB_SHA` | `EvlogOptions.CommitHash` |
| `REGION` / `FLY_REGION` / `AWS_REGION` | `EvlogOptions.Region` |

## Dependencies

### Runtime
- `Microsoft.AspNetCore.App` (framework reference)
- No external NuGet packages

### Test
- xUnit v3
- NSubstitute
- Microsoft.AspNetCore.Mvc.Testing

## Future Work

- Built-in drain adapters (Axiom, OTLP, Sentry, PostHog, Better Stack)
- Enrichment hooks (user agent, geo, request size, trace context)
- Batching/retry/buffering pipeline for drains
- Minimal API source generator for compile-time `Set<T>()` optimization
- gRPC/SignalR non-HTTP request logging
