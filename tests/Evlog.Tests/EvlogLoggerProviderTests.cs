using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Evlog.Tests;

public class EvlogLoggerProviderTests
{
    [Fact]
    public void Logger_WithActiveRequest_CapturesAsRequestLog()
    {
        var httpContext = new DefaultHttpContext();
        var requestLogger = new RequestLogger();
        requestLogger.Activate("svc", "prod");
        httpContext.SetEvlogLogger(requestLogger);

        var accessor = new TestHttpContextAccessor { HttpContext = httpContext };
        using var provider = new EvlogLoggerProvider(accessor);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("Hello {Name}", "world");

        var logs = requestLogger.GetRequestLogs();
        Assert.Single(logs);
        Assert.Equal(EvlogLevel.Info, logs[0].Level);
        Assert.Contains("Hello world", logs[0].Message);
        Assert.Equal("TestCategory", logs[0].Category);
    }

    [Fact]
    public void Logger_WithoutActiveRequest_DoesNotThrow()
    {
        var accessor = new TestHttpContextAccessor { HttpContext = null };
        using var provider = new EvlogLoggerProvider(accessor);
        var logger = provider.CreateLogger("TestCategory");

        // Should not throw
        logger.LogInformation("No context here");
    }

    [Fact]
    public void Logger_MapsLogLevelsCorrectly()
    {
        var httpContext = new DefaultHttpContext();
        var requestLogger = new RequestLogger();
        requestLogger.Activate("svc", "prod");
        httpContext.SetEvlogLogger(requestLogger);

        var accessor = new TestHttpContextAccessor { HttpContext = httpContext };
        using var provider = new EvlogLoggerProvider(accessor);
        var logger = provider.CreateLogger("Test");

        logger.LogWarning("warn msg");
        logger.LogError("error msg");
        logger.LogDebug("debug msg");

        var logs = requestLogger.GetRequestLogs();
        Assert.Equal(3, logs.Count);
        Assert.Equal(EvlogLevel.Warn, logs[0].Level);
        Assert.Equal(EvlogLevel.Error, logs[1].Level);
        Assert.Equal(EvlogLevel.Debug, logs[2].Level);
    }

    private class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
