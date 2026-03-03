# evlog.net

[![NuGet](https://img.shields.io/nuget/v/Evlog?color=black)](https://www.nuget.org/packages/Evlog)
[![License](https://img.shields.io/github/license/rodrigovidal/evlog-net?color=black)](https://github.com/rodrigovidal/evlog-net/blob/main/LICENSE)

**.NET port of [evlog](https://github.com/HugoRCD/evlog)** — wide events structured logging for ASP.NET Core.

**Your logs are lying to you.**

A single request generates 10+ log lines. When production breaks at 3am, you're grep-ing through noise, praying you'll find signal. Your errors say "Something went wrong" – thanks, very helpful.

**evlog fixes this.** One log per request. All context included. Errors that explain themselves.

## Why evlog?

### The Problem

```csharp
// Controllers/CheckoutController.cs

// ❌ Scattered logs - impossible to debug
_logger.LogInformation("Request received");
_logger.LogInformation("User: {UserId}", user.Id);
_logger.LogInformation("Cart loaded");
_logger.LogError("Payment failed");  // Good luck finding this at 3am

throw new Exception("Something went wrong");  // 🤷‍♂️
```

### The Solution

```csharp
// ✅ One comprehensive event per request
app.MapPost("/api/checkout", async (HttpContext context, CartService carts) =>
{
    var log = context.GetEvlogLogger();

    log.Set(new { User = new { Id = user.Id, Plan = "premium" } });
    log.Set(new { Cart = new { Items = 3, Total = 9999 } });
    log.Error(error);

    return Results.Ok(order);
    // Middleware auto-emits ONE event with ALL context + duration
});
```

Output:

```json
{
  "timestamp": "2026-03-03T10:23:45.612Z",
  "level": "error",
  "service": "my-app",
  "method": "POST",
  "path": "/api/checkout",
  "duration": "1.2s",
  "user": { "id": "123", "plan": "premium" },
  "cart": { "items": 3, "total": 9999 },
  "error": { "message": "Card declined" }
}
```

### Built for AI-Assisted Development

We're in the age of AI agents writing and debugging code. When an agent encounters an error, it needs **clear, structured context** to understand what happened and how to fix it.

Traditional logs force agents to grep through noise. evlog gives them:
- **One event per request** with all context in one place
- **Self-documenting errors** with `Why` and `Fix` fields
- **Structured JSON** that's easy to parse and reason about

Your AI copilot will thank you.

---

## Installation

```bash
dotnet add package Evlog --version 0.0.1-beta
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEvlog(options =>
{
    options.Service = "my-api";
    options.Environment = builder.Environment.EnvironmentName;
    options.Version = "1.0.0";
    options.Pretty = builder.Environment.IsDevelopment();
});

var app = builder.Build();
app.UseEvlog();
```

That's it. Now use `context.GetEvlogLogger()` in any endpoint:

```csharp
app.MapPost("/api/checkout", async (HttpContext context, CartService carts) =>
{
    var log = context.GetEvlogLogger();

    // Authenticate user and add to wide event
    var user = context.User.GetUserId();
    log.Set(new { User = new { Id = user, Plan = "premium" } });

    // Load cart and add to wide event
    var cart = await carts.GetAsync(user);
    log.Set(new { Cart = new { Items = cart.Items.Count, Total = cart.Total } });

    // Process payment
    try
    {
        var payment = await ProcessPayment(cart, user);
        log.Set(new { Payment = new { Id = payment.Id, Method = payment.Method } });
    }
    catch (Exception ex)
    {
        log.Error(ex);

        throw EvlogError.Create(
            "Payment failed",
            status: 402,
            why: ex.Message,
            fix: "Try a different payment method or contact your bank"
        );
    }

    // Create order
    var order = await CreateOrder(cart, user);
    log.Set(new { Order = new { Id = order.Id, Status = order.Status } });

    return Results.Ok(order);
    // log emits automatically at request end
});
```

The wide event emitted at the end contains **everything**:

```json
{
  "timestamp": "2026-03-03T10:23:45.612Z",
  "level": "info",
  "service": "my-api",
  "method": "POST",
  "path": "/api/checkout",
  "duration": "125ms",
  "user": { "id": "usr_123", "plan": "premium" },
  "cart": { "items": 3, "total": 9999 },
  "payment": { "id": "pay_xyz", "method": "card" },
  "order": { "id": "ord_abc", "status": "created" },
  "status": 200
}
```

## Performance

evlog.net is designed for near-zero overhead in production:

| Concern | Approach |
|---------|----------|
| Avoid boxing value types | Typed `Set()` overloads + discriminated-union struct |
| Avoid per-request allocation | `ObjectPool<RequestLogger>` with `IResettable` |
| Zero-alloc sampled-out path | `_active` flag; all methods return immediately |
| JSON output | `Utf8JsonWriter` + `ArrayBufferWriter<byte>` backed by `ArrayPool` |
| Property names | Pre-encoded as `static readonly JsonEncodedText` |

### Zero-Alloc API

For performance-critical paths, use typed overloads instead of anonymous objects:

```csharp
// Zero-alloc path — typed overloads
log.Set("metrics.processingTime", 42);
log.Set("metrics.cacheHit", true);
log.Set("order.id", orderId);

// Zero-alloc for complex objects — direct Utf8JsonWriter access
log.SetJson("details", writer =>
{
    writer.WriteString("region", "us-east-1");
    writer.WriteNumber("retries", 3);
});
```

## Structured Errors

Errors should tell you **what** happened, **why**, and **how to fix it**.

```csharp
throw EvlogError.Create(
    "Failed to sync repository",
    status: 503,
    why: "GitHub API rate limit exceeded",
    fix: "Wait 1 hour or use a different token",
    link: "https://docs.github.com/en/rest/rate-limit"
);
```

evlog automatically maps `EvlogError` to [ProblemDetails](https://datatracker.ietf.org/doc/html/rfc9457) for HTTP responses:

```json
{
  "title": "Failed to sync repository",
  "status": 503,
  "detail": "GitHub API rate limit exceeded",
  "type": "https://docs.github.com/en/rest/rate-limit",
  "fix": "Wait 1 hour or use a different token"
}
```

Console output (development):

```
Error: Failed to sync repository
Why: GitHub API rate limit exceeded
Fix: Wait 1 hour or use a different token
More info: https://docs.github.com/en/rest/rate-limit
```

## ILogger Integration

When registered, any `ILogger` call within an HTTP request is captured into the current request's wide event:

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

At scale, logging everything can become expensive. evlog supports two sampling strategies:

### Head Sampling (rates)

Random sampling based on log level, decided before the request runs:

```csharp
builder.Services.AddEvlog(options =>
{
    options.Sampling = new SamplingOptions
    {
        Rates = new Dictionary<EvlogLevel, int>
        {
            [EvlogLevel.Info] = 10,    // Keep 10% of info logs
            [EvlogLevel.Debug] = 0,    // Disable debug logs
            [EvlogLevel.Error] = 100,  // Always keep errors
        }
    };
});
```

### Tail Sampling (keep)

Force-keep logs based on request outcome, evaluated after the response:

```csharp
options.Sampling = new SamplingOptions
{
    Rates = new Dictionary<EvlogLevel, int>
    {
        [EvlogLevel.Info] = 10,
    },
    Keep =
    [
        new() { Status = 400 },              // Always keep if status >= 400
        new() { Duration = 1000 },           // Always keep if duration >= 1000ms
        new() { Path = "/api/critical/**" }, // Always keep critical paths
    ]
};
```

### Drain

Send logs to external observability platforms:

```csharp
builder.Services.AddEvlog(options =>
{
    options.Drain = async (context) =>
    {
        await axiomClient.IngestAsync(context.Event);
    };
});
```

## Pretty Output

In development (`Pretty = true`), evlog uses a compact tree format:

```
 POST /api/checkout 201 125ms
  ├─ user.id: usr_123
  ├─ user.plan: premium
  ├─ cart.items: 3
  ├─ order.id: ord_456
  ├─ info: Order created successfully
  └─ service: my-api
```

In production (`Pretty = false`), logs are emitted as single-line JSON for machine parsing.

## Environment Auto-Detection

If not explicitly configured, these environment variables are checked:

| Variable | Maps to |
|----------|---------|
| `SERVICE_NAME` | `Service` |
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | `Environment` |
| `APP_VERSION` | `Version` |
| `COMMIT_SHA` / `GITHUB_SHA` | `CommitHash` |
| `REGION` / `FLY_REGION` / `AWS_REGION` | `Region` |

## API Reference

### `services.AddEvlog(options)`

Register evlog services with dependency injection.

### `app.UseEvlog()`

Add the evlog middleware to the ASP.NET Core pipeline.

### `context.GetEvlogLogger()`

Get the request-scoped `RequestLogger` from `HttpContext`.

### `RequestLogger`

```csharp
// Ergonomic path — anonymous objects
log.Set(new { User = new { Id = "123", Plan = "premium" } });

// Zero-alloc path — typed overloads
log.Set("key", "string value");
log.Set("key", 42);
log.Set("key", 3.14);
log.Set("key", true);

// Zero-alloc complex objects
log.SetJson("key", writer => { /* Utf8JsonWriter */ });

// Request-scoped log entries
log.Info("message");
log.Warn("message");
log.Error(exception);
```

### `EvlogError.Create(message, ...)`

Create a structured error with HTTP status support.

```csharp
EvlogError.Create(
    message: "What happened",
    status: 500,            // HTTP status code
    why: "Why it happened",
    fix: "How to fix it",
    link: "https://docs.example.com",
    cause: innerException
);
```

## Philosophy

Inspired by [Logging Sucks](https://loggingsucks.com/) by [Boris Tane](https://x.com/boristane).

1. **Wide Events**: One log per request with all context
2. **Structured Errors**: Errors that explain themselves
3. **Request Scoping**: Accumulate context, emit once
4. **Pretty for Dev, JSON for Prod**: Human-readable locally, machine-parseable in production

## Credits

This is the .NET port of [evlog](https://github.com/HugoRCD/evlog), the TypeScript wide events logging library created by [@HugoRCD](https://github.com/HugoRCD). All credit for the original concept, API design, and philosophy goes to the original project.

## License

[MIT](./LICENSE)
