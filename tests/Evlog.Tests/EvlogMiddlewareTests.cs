using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Evlog.Tests;

public class EvlogMiddlewareTests : IAsyncDisposable
{
    private readonly List<byte[]> _drainedEvents = new();

    private IHost CreateHost(Action<EvlogOptions>? configureOptions = null)
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
                        options.Service = "test-svc";
                        options.Environment = "test";
                        options.Pretty = false;
                        options.Drain = async ctx =>
                        {
                            _drainedEvents.Add(ctx.EventJson.ToArray());
                        };
                        configureOptions?.Invoke(options);
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseEvlog();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/test", (HttpContext ctx) =>
                        {
                            var log = ctx.GetEvlogLogger();
                            log.Set("user.id", "usr_123");
                            log.Info("Hello from test");
                            return Results.Ok(new { message = "ok" });
                        });
                        endpoints.MapGet("/api/error", (HttpContext ctx) =>
                        {
                            throw EvlogError.Create("Something broke", status: 422, why: "Bad data");
                        });
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host;
    }

    [Fact]
    public async Task Middleware_EmitsWideEvent_OnSuccessfulRequest()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_drainedEvents);

        using var doc = JsonDocument.Parse(_drainedEvents[0]);
        var root = doc.RootElement;
        Assert.Equal("test-svc", root.GetProperty("service").GetString());
        Assert.Equal("GET", root.GetProperty("method").GetString());
        Assert.Equal("/api/test", root.GetProperty("path").GetString());
        Assert.Equal(200, root.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Middleware_CapturesContextFromLogger()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        await client.GetAsync("/api/test");

        using var doc = JsonDocument.Parse(_drainedEvents[0]);
        var user = doc.RootElement.GetProperty("user");
        Assert.Equal("usr_123", user.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Middleware_CapturesEvlogError_AsStructuredError()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/error");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Single(_drainedEvents);

        using var doc = JsonDocument.Parse(_drainedEvents[0]);
        var root = doc.RootElement;
        Assert.Equal("error", root.GetProperty("level").GetString());
        Assert.Equal(422, root.GetProperty("status").GetInt32());

        var error = root.GetProperty("error");
        Assert.Equal("Something broke", error.GetProperty("message").GetString());
        Assert.Equal("Bad data", error.GetProperty("why").GetString());
    }

    [Fact]
    public async Task Middleware_ReturnsProblemDetails_ForEvlogError()
    {
        using var host = CreateHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/error");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal("Something broke", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task GetEvlogLogger_ReturnsInactiveLogger_WhenNotInMiddleware()
    {
        var ctx = new DefaultHttpContext();
        var logger = ctx.GetEvlogLogger();

        // Should not throw, but logger is inactive
        logger.Set("key", "value");
        Assert.False(logger.IsActive);
    }

    public async ValueTask DisposeAsync()
    {
        _drainedEvents.Clear();
    }
}
