using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Evlog.Tests;

public class IntegrationTests : IAsyncDisposable
{
    private readonly List<byte[]> _events = new();

    private IHost CreateHost()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddEvlog(options =>
                    {
                        options.Service = "integration-test";
                        options.Environment = "test";
                        options.Pretty = false;
                        options.Version = "1.0.0";
                        options.Sampling = new SamplingOptions
                        {
                            Keep =
                            [
                                new() { Status = 400 },
                            ]
                        };
                        options.Drain = async ctx =>
                        {
                            _events.Add(ctx.EventJson.ToArray());
                        };
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseEvlog();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/users/{id}", (string id, HttpContext ctx) =>
                        {
                            var log = ctx.GetEvlogLogger();
                            log.Set("user.id", id);
                            log.Set("user.plan", "premium");
                            log.Set("user.active", true);
                            log.Set("query.count", 3);
                            log.Set("query.duration", 12.5);
                            log.Info("User fetched successfully");
                            return Results.Ok(new { id, plan = "premium" });
                        });

                        endpoints.MapPost("/api/orders", (HttpContext ctx) =>
                        {
                            var log = ctx.GetEvlogLogger();
                            log.SetJson("order", writer =>
                            {
                                writer.WriteString("id", "ord_456");
                                writer.WriteNumber("total", 99.99);
                                writer.WriteNumber("items", 3);
                            });
                            log.Info("Order created");
                            return Results.Created("/api/orders/ord_456", new { id = "ord_456" });
                        });

                        endpoints.MapGet("/api/fail", (HttpContext ctx) =>
                        {
                            throw EvlogError.Create(
                                message: "Payment failed",
                                status: 422,
                                why: "Card declined",
                                fix: "Try another card");
                        });

                        endpoints.MapGet("/api/crash", (HttpContext ctx) =>
                        {
                            throw new InvalidOperationException("Unexpected error");
                        });
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host;
    }

    [Fact]
    public async Task FullPipeline_Success_EmitsCompleteWideEvent()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/users/usr_123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_events);

        using var doc = JsonDocument.Parse(_events[0]);
        var root = doc.RootElement;

        // Envelope
        Assert.Equal("integration-test", root.GetProperty("service").GetString());
        Assert.Equal("test", root.GetProperty("environment").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("info", root.GetProperty("level").GetString());
        Assert.Equal("GET", root.GetProperty("method").GetString());
        Assert.Equal(200, root.GetProperty("status").GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("duration", out _));
        Assert.True(root.TryGetProperty("requestId", out _));

        // Nested context from dot-notation
        var user = root.GetProperty("user");
        Assert.Equal("usr_123", user.GetProperty("id").GetString());
        Assert.Equal("premium", user.GetProperty("plan").GetString());
        Assert.True(user.GetProperty("active").GetBoolean());

        var query = root.GetProperty("query");
        Assert.Equal(3, query.GetProperty("count").GetInt32());
        Assert.Equal(12.5, query.GetProperty("duration").GetDouble());

        // Request logs (may include ASP.NET Core internal logs from ILoggerProvider)
        var logs = root.GetProperty("requestLogs");
        Assert.True(logs.GetArrayLength() >= 1);
        Assert.Contains(
            logs.EnumerateArray().ToArray(),
            log => log.GetProperty("message").GetString() == "User fetched successfully");
    }

    [Fact]
    public async Task FullPipeline_JsonFragment_EmitsNestedObject()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.PostAsync("/api/orders", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(_events);

        using var doc = JsonDocument.Parse(_events[0]);
        var order = doc.RootElement.GetProperty("order");
        Assert.Equal("ord_456", order.GetProperty("id").GetString());
        Assert.Equal(99.99, order.GetProperty("total").GetDouble());
        Assert.Equal(3, order.GetProperty("items").GetInt32());
    }

    [Fact]
    public async Task FullPipeline_EvlogError_ReturnsProblemDetails()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/fail");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // Response body should be ProblemDetails
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Payment failed", doc.RootElement.GetProperty("title").GetString());

        // Wide event should capture error
        Assert.Single(_events);
        using var eventDoc = JsonDocument.Parse(_events[0]);
        Assert.Equal("error", eventDoc.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public async Task FullPipeline_UnhandledException_Emits500Error()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/crash");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(_events);

        using var doc = JsonDocument.Parse(_events[0]);
        Assert.Equal("error", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());

        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("Unexpected error", error.GetProperty("message").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        _events.Clear();
    }
}
