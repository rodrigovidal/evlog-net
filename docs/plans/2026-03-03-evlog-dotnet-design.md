# evlog .NET Port — Design Document

**Date:** 2026-03-03
**Status:** Approved

## Overview

Port evlog's "wide events" structured logging pattern from TypeScript to .NET. One comprehensive log event per request with all context included, instead of scattered log lines.

## Decisions

| Decision | Choice |
|----------|--------|
| Scope | Core + ASP.NET Core middleware |
| Packaging | Single NuGet package (`Evlog`) |
| Target framework | .NET 10 |
| API style | Idiomatic C# with dependency injection |
| ILogger integration | Yes, as an `ILoggerProvider` |
| Request context storage | `HttpContext.Items` + middleware |
| Error response format | Map `EvlogError` to `ProblemDetails` for HTTP responses |
| Performance | Near-zero-allocation, `ObjectPool<RequestLogger>`, `Utf8JsonWriter` |

## Performance Architecture

| Concern | Approach |
|---------|----------|
| Avoid boxing value types | Typed `Set()` overloads + `ContextEntry` discriminated-union struct |
| Avoid per-request allocation | `ObjectPool<RequestLogger>` with `IResettable` |
| Zero-alloc sampled-out path | `_active` bool flag; all methods `[AggressiveInlining]` return immediately |
| JSON output | `Utf8JsonWriter` + `ArrayBufferWriter<byte>` backed by `ArrayPool` |
| Property names | Pre-encoded as `static readonly JsonEncodedText` |
| Nested JSON from flat keys | Dot-notation expanded at emit time (once per request, cold path) |

## Project Structure

```
evlog.net/
├── src/
│   └── Evlog/
│       ├── Evlog.csproj              # Single package, net10.0
│       ├── Core/
│       │   ├── WideEvent.cs          # The wide event class
│       │   ├── EvlogError.cs         # Structured error with Why/Fix
│       │   ├── RequestLogger.cs      # Per-request context accumulator
│       │   └── LogLevel.cs           # Info, Warn, Error, Debug
│       ├── Configuration/
│       │   ├── EvlogOptions.cs       # Main config (service, env, sampling)
│       │   └── SamplingOptions.cs    # Head/tail sampling config
│       ├── AspNetCore/
│       │   ├── EvlogMiddleware.cs    # Request lifecycle middleware
│       │   ├── EvlogServiceExtensions.cs  # services.AddEvlog()
│       │   └── HttpContextExtensions.cs   # context.GetEvlogLogger()
│       ├── Logging/
│       │   ├── EvlogLoggerProvider.cs     # ILoggerProvider impl
│       │   └── EvlogLogger.cs             # ILogger impl
│       └── Output/
│           ├── JsonOutputFormatter.cs     # Production JSON
│           └── PrettyOutputFormatter.cs   # Dev colored output
├── tests/
│   └── Evlog.Tests/
│       └── Evlog.Tests.csproj
└── Evlog.sln
```

## Core Types

### WideEvent

A mutable class for accumulating context during a request. Well-known properties plus a `Dictionary<string, object?>` for user-defined fields.

```csharp
public class WideEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public EvlogLevel Level { get; set; }
    public string Service { get; set; }
    public string Environment { get; set; }
    public string? Version { get; set; }
    public string? CommitHash { get; set; }
    public string? Region { get; set; }

    // Request context
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? RequestId { get; set; }
    public int? Status { get; set; }
    public string? Duration { get; set; }

    // Error info
    public WideEventError? Error { get; set; }

    // Accumulated request-scoped log entries
    public List<RequestLogEntry> RequestLogs { get; set; }

    // User-defined fields from .Set() calls
    public Dictionary<string, object?> Fields { get; set; }
}
```

### EvlogError

Extends `Exception` with structured metadata. Static factory method for creation.

```csharp
public class EvlogError : Exception
{
    public int Status { get; init; } = 500;
    public string? Why { get; init; }
    public string? Fix { get; init; }
    public string? Link { get; init; }

    public static EvlogError Create(
        string message,
        int status = 500,
        string? why = null,
        string? fix = null,
        string? link = null,
        Exception? cause = null);
}
```

**HTTP response mapping:** The middleware maps `EvlogError` to `ProblemDetails`:
- `message` → `Title`
- `status` → `Status`
- `why` → `Detail`
- `link` → `Type`
- `fix` → extension property `Fix`

## Request Logger API

The core API for accumulating context throughout a request.

```csharp
public sealed class RequestLogger : IResettable
{
    // Ergonomic path — anonymous/typed objects (small allocation, STJ serialization)
    public void Set(object context);

    // Zero-alloc path — typed overloads, all [AggressiveInlining]
    public void Set(string key, string value);
    public void Set(string key, int value);
    public void Set(string key, long value);
    public void Set(string key, double value);
    public void Set(string key, bool value);

    // Complex nested objects — write directly to Utf8JsonWriter (zero-alloc)
    public void SetJson(string key, Action<Utf8JsonWriter> writeBody);

    // Capture request-scoped log entries
    public void Info(string message);
    public void Warn(string message);
    public void Error(Exception ex, string? message = null);
}
```

**Hybrid API:**
- `Set(object)` — ergonomic, mirrors TypeScript API, allocates the anonymous object but JSON path is efficient via `Utf8JsonWriter`
- `Set(string key, value)` — zero-alloc typed overloads for performance-critical code, dot-notation expanded to nested JSON at emit time
- `SetJson(key, callback)` — zero-alloc for complex objects via direct `Utf8JsonWriter` access

**Zero-alloc when sampled out:** All methods check `_active` flag and return immediately.

**Usage in endpoints:**

```csharp
app.MapPost("/api/orders", async (HttpContext context, OrderService orders) =>
{
    var log = context.GetEvlogLogger();

    var user = context.User.GetUserId();
    // Ergonomic path — familiar anonymous object syntax
    log.Set(new { User = new { Id = user, Plan = "premium" } });

    var order = await orders.CreateAsync(user);
    log.Set(new { Order = new { Id = order.Id, Total = order.Total } });
    log.Info("Order created successfully");

    // Zero-alloc path — for hot paths where allocation matters
    log.Set("metrics.processingTime", 42);
    log.Set("metrics.cacheHit", true);

    return Results.Ok(order);
    // Middleware auto-emits the wide event after response
});
```

## ASP.NET Core Middleware

### Registration

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEvlog(options =>
{
    options.Service = "my-api";
    options.Environment = builder.Environment.EnvironmentName;
    options.Version = "1.2.0";
    options.Pretty = builder.Environment.IsDevelopment();

    options.Sampling = new SamplingOptions
    {
        Rates = new Dictionary<EvlogLevel, int>
        {
            [EvlogLevel.Info] = 10,
            [EvlogLevel.Debug] = 5,
            [EvlogLevel.Error] = 100,
        },
        Keep =
        [
            new() { Status = 400 },
            new() { Duration = 1000 },
            new() { Path = "/api/critical/**" },
        ]
    };

    options.Drain = async (context) =>
    {
        await axiomClient.IngestAsync(context.Event);
    };
});

var app = builder.Build();
app.UseEvlog();
```

### Middleware Lifecycle

1. **Before request:** Create `RequestLogger`, store in `HttpContext.Items`, start `Stopwatch`, generate request ID if not present.
2. **During request:** Code calls `context.GetEvlogLogger()` to accumulate context.
3. **After response:** Compute duration, set status code, evaluate tail sampling, call `Emit()`.
4. **On exception:** Capture error details into the wide event, set level to `error`. If `EvlogError`, map to `ProblemDetails` response.
5. **Drain:** Fire-and-forget call to the configured drain callback. Errors swallowed, logged to stderr.

## ILoggerProvider Integration

When registered, any `ILogger` call within an HTTP request is captured into the current request's wide event under `RequestLogs`:

```csharp
// Third-party library internally does:
_logger.LogWarning("Rate limit approaching for client {ClientId}", clientId);

// Appears in the wide event:
{
    "requestLogs": [
        {
            "level": "warn",
            "message": "Rate limit approaching for client abc123",
            "timestamp": "2026-03-03T10:23:45.800Z",
            "category": "Stripe.RateLimiter"
        }
    ]
}
```

Outside of an HTTP request (background services, startup), ILogger calls emit immediately as standalone JSON log entries.

## Sampling

### Head Sampling

Evaluated before the request runs. A percentage per log level. Events that fail head sampling are discarded — no `RequestLogger` is created, `GetEvlogLogger()` returns a no-op logger.

### Tail Sampling

Evaluated after the response. Conditions (OR logic):
- `Status` — keep if response status >= value
- `Duration` — keep if duration >= value (ms)
- `Path` — keep if request path matches glob pattern

If any condition matches, the event is force-kept regardless of head sampling.

## Output

### Development (Pretty)

Colored, human-readable output to stdout:

```
 POST /api/orders 201 125ms
  ├─ user.id: usr_123
  ├─ user.plan: premium
  ├─ order.id: ord_456
  ├─ order.total: 99.99
  ├─ info: Order created successfully
  └─ service: my-api
```

### Production (JSON)

Single-line JSON to stdout via `System.Text.Json`:

```json
{"timestamp":"2026-03-03T10:23:45.612Z","level":"info","service":"my-api","method":"POST","path":"/api/orders","status":201,"duration":"125ms","user":{"id":"usr_123","plan":"premium"},"order":{"id":"ord_456","total":99.99},"requestLogs":[{"level":"info","message":"Order created successfully"}]}
```

## Environment Auto-Detection

If not explicitly configured, these environment variables are checked:
- `SERVICE_NAME` → `Service`
- `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` → `Environment`
- `APP_VERSION` → `Version`
- `COMMIT_SHA` / `GITHUB_SHA` → `CommitHash`
- `REGION` / `FLY_REGION` / `AWS_REGION` → `Region`

## Future Work (Not in Initial Scope)

- **Adapters:** Axiom, OTLP, Sentry, PostHog, Better Stack drains
- **Enrichers:** User agent, geo, request size, trace context
- **Pipeline:** Batching, retry, buffering for drains
- **Minimal API source generator:** Compile-time `Set<T>()` optimization
- **gRPC/SignalR support:** Non-HTTP request logging
