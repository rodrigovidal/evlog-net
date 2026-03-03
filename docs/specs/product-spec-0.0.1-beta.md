# Evlog for .NET — Product Spec

**Version:** 0.0.1-beta
**Author:** Rodrigo Vidal
**Date:** 2026-03-03
**Status:** Shipped

## Problem

Traditional logging in ASP.NET Core scatters context across dozens of log lines per request. When production breaks at 3am, you're grep-ing through noise trying to reconstruct what happened. Errors say "Something went wrong" with no actionable context.

AI agents debugging code face the same problem — they need structured, complete context in one place to reason about failures.

## Solution

Port [evlog](https://github.com/HugoRCD/evlog) to .NET. One comprehensive log event per HTTP request with all accumulated context, instead of scattered log lines. Errors that explain themselves with `Why` and `Fix` fields.

## Target Users

- .NET developers building HTTP APIs with ASP.NET Core
- Teams using structured logging for observability (Axiom, Datadog, Grafana)
- AI-assisted development workflows that need parseable error context

## Scope

### In Scope (v0.0.1-beta)

- **Core library**: Wide event accumulation, structured errors, request logger
- **ASP.NET Core middleware**: Auto-capture request/response lifecycle
- **ILogger bridge**: Capture third-party `ILogger` calls into the wide event
- **Sampling**: Head sampling (per-level rates) and tail sampling (status, duration, path)
- **Output**: JSON (production) and pretty tree format (development)
- **Drain callback**: Fire-and-forget to external services
- **Environment auto-detection**: Read from standard env vars

### Out of Scope (Future)

- Built-in adapters (Axiom, OTLP, Sentry, PostHog, Better Stack)
- Enrichment hooks (user agent, geo, request size, trace context)
- Batching/retry pipeline for drains
- Source generator for compile-time `Set<T>()` optimization
- gRPC/SignalR support

## API Surface

### Registration

```csharp
builder.Services.AddEvlog(options =>
{
    options.Service = "my-api";
    options.Environment = builder.Environment.EnvironmentName;
    options.Pretty = builder.Environment.IsDevelopment();
});

app.UseEvlog();
```

### Usage in Endpoints

```csharp
var log = context.GetEvlogLogger();

// Ergonomic — anonymous objects
log.Set(new { User = new { Id = userId, Plan = "premium" } });

// Performance — typed overloads (zero-alloc)
log.Set("metrics.processingTime", 42);
log.Set("metrics.cacheHit", true);

// Request-scoped log entries
log.Info("Order created successfully");
log.Warn("Inventory low");
log.Error(exception);
```

### Structured Errors

```csharp
throw EvlogError.Create(
    "Payment failed",
    status: 402,
    why: "Card declined by issuer",
    fix: "Try a different payment method or contact your bank",
    link: "https://docs.example.com/payments/declined"
);
```

Maps automatically to RFC 9457 ProblemDetails in HTTP responses.

### Sampling

```csharp
options.Sampling = new SamplingOptions
{
    Rates = new Dictionary<EvlogLevel, int>
    {
        [EvlogLevel.Info] = 10,
        [EvlogLevel.Error] = 100,
    },
    Keep =
    [
        new() { Status = 400 },
        new() { Duration = 1000 },
        new() { Path = "/api/critical/**" },
    ]
};
```

## Output

### Development (Pretty)

```
 POST /api/orders 201 125ms
  ├─ user.id: usr_123
  ├─ user.plan: premium
  ├─ order.id: ord_456
  ├─ info: Order created successfully
  └─ service: my-api
```

### Production (JSON)

```json
{"timestamp":"2026-03-03T10:23:45.612Z","level":"info","service":"my-api","method":"POST","path":"/api/orders","status":201,"duration":"125ms","user":{"id":"usr_123","plan":"premium"},"order":{"id":"ord_456","total":99.99}}
```

## Success Criteria

- All 75 unit/integration tests passing
- Published to NuGet as `Evlog` v0.0.1-beta
- CI pipeline builds and tests on every push
- README with full usage documentation
