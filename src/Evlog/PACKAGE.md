# Evlog

**Wide events** structured logging for ASP.NET Core. One comprehensive log event per request instead of scattered log lines.

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEvlog(options =>
{
    options.Service = "my-api";
    options.Environment = builder.Environment.EnvironmentName;
    options.Pretty = builder.Environment.IsDevelopment();
});

var app = builder.Build();
app.UseEvlog();
```

## Usage

```csharp
app.MapPost("/api/orders", async (HttpContext context, OrderService orders) =>
{
    var log = context.GetEvlogLogger();

    log.Set(new { User = new { Id = userId, Plan = "premium" } });

    var order = await orders.CreateAsync(userId);
    log.Set(new { Order = new { Id = order.Id, Total = order.Total } });
    log.Info("Order created successfully");

    return Results.Ok(order);
    // Middleware auto-emits the wide event after response
});
```

## Features

- **Wide events**: accumulate context throughout a request, emit once
- **Zero-alloc hot path**: typed `Set()` overloads and `SetJson()` for performance-critical code
- **Ergonomic API**: `Set(new { ... })` for familiar anonymous object syntax
- **Head + tail sampling**: control log volume with per-level rates and keep rules
- **ILogger integration**: capture third-party library logs into the wide event
- **Structured errors**: `EvlogError` with `Why`/`Fix` fields, auto-mapped to `ProblemDetails`
- **Pretty dev output**: colored, tree-formatted output for local development
