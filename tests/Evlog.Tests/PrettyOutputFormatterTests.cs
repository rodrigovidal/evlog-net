namespace Evlog.Tests;

public class PrettyOutputFormatterTests
{
    [Fact]
    public void Format_IncludesMethodPathStatusDuration()
    {
        var logger = new RequestLogger();
        logger.Activate("my-api", "production");
        logger.SetRequest("POST", "/api/orders");

        var output = PrettyOutputFormatter.Format(logger, status: 201, durationMs: 125.0);

        Assert.Contains("POST", output);
        Assert.Contains("/api/orders", output);
        Assert.Contains("201", output);
        Assert.Contains("125ms", output);
    }

    [Fact]
    public void Format_IncludesContextEntries()
    {
        var logger = new RequestLogger();
        logger.Activate("my-api", "production");
        logger.SetRequest("GET", "/api/test");
        logger.Set("user.id", "usr_123");
        logger.Set("user.plan", "premium");

        var output = PrettyOutputFormatter.Format(logger, status: 200, durationMs: 50.0);

        Assert.Contains("user.id", output);
        Assert.Contains("usr_123", output);
        Assert.Contains("user.plan", output);
        Assert.Contains("premium", output);
    }

    [Fact]
    public void Format_IncludesRequestLogs()
    {
        var logger = new RequestLogger();
        logger.Activate("my-api", "production");
        logger.SetRequest("GET", "/api/test");
        logger.Info("step 1");

        var output = PrettyOutputFormatter.Format(logger, status: 200, durationMs: 50.0);

        Assert.Contains("step 1", output);
    }

    [Fact]
    public void Format_ErrorStatus_UsesErrorColor()
    {
        var logger = new RequestLogger();
        logger.Activate("my-api", "production");
        logger.SetRequest("GET", "/api/test");

        var output = PrettyOutputFormatter.Format(logger, status: 500, durationMs: 50.0);

        // Should contain ANSI red color code for error status
        Assert.Contains("\x1B[31m", output);
    }

    [Fact]
    public void Format_UsesTreeCharacters()
    {
        var logger = new RequestLogger();
        logger.Activate("my-api", "production");
        logger.SetRequest("GET", "/api/test");
        logger.Set("key1", "val1");
        logger.Set("key2", "val2");

        var output = PrettyOutputFormatter.Format(logger, status: 200, durationMs: 50.0);

        Assert.Contains("\u251C\u2500", output);
        Assert.Contains("\u2514\u2500", output);
    }
}
