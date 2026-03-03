using System.Buffers;
using System.Text.Json;

namespace Evlog.Tests;

public class WideEventWriterTests
{
    private static JsonDocument WriteAndParse(RequestLogger logger, int status = 200)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, status, durationMs: 125.0);
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    [Fact]
    public void Write_IncludesEnvelopeFields()
    {
        var logger = new RequestLogger();
        logger.Activate("my-api", "production", version: "1.0.0");
        logger.SetRequest("POST", "/api/orders", "req-123");

        using var doc = WriteAndParse(logger);
        var root = doc.RootElement;

        Assert.Equal("my-api", root.GetProperty("service").GetString());
        Assert.Equal("production", root.GetProperty("environment").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("POST", root.GetProperty("method").GetString());
        Assert.Equal("/api/orders", root.GetProperty("path").GetString());
        Assert.Equal("req-123", root.GetProperty("requestId").GetString());
        Assert.Equal(200, root.GetProperty("status").GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("duration", out _));
    }

    [Fact]
    public void Write_FlatKeys_WrittenDirectly()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        logger.Set("action", "checkout");
        logger.Set("count", 3);
        logger.Set("total", 99.99);
        logger.Set("premium", true);

        using var doc = WriteAndParse(logger);
        var root = doc.RootElement;

        Assert.Equal("checkout", root.GetProperty("action").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(99.99, root.GetProperty("total").GetDouble());
        Assert.True(root.GetProperty("premium").GetBoolean());
    }

    [Fact]
    public void Write_DottedKeys_ExpandedToNestedJson()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        logger.Set("user.id", "usr_123");
        logger.Set("user.plan", "premium");
        logger.Set("order.total", 99.99);

        using var doc = WriteAndParse(logger);
        var root = doc.RootElement;

        var user = root.GetProperty("user");
        Assert.Equal("usr_123", user.GetProperty("id").GetString());
        Assert.Equal("premium", user.GetProperty("plan").GetString());

        var order = root.GetProperty("order");
        Assert.Equal(99.99, order.GetProperty("total").GetDouble());
    }

    [Fact]
    public void Write_DuplicateKeys_FirstWriteWins()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        logger.Set("user.id", "first");
        logger.Set("user.id", "second");

        using var doc = WriteAndParse(logger);
        var user = doc.RootElement.GetProperty("user");

        Assert.Equal("first", user.GetProperty("id").GetString());
    }

    [Fact]
    public void Write_JsonFragment_WrittenAsNestedObject()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        logger.SetJson("user", writer =>
        {
            writer.WriteString("id", "usr_123");
            writer.WriteString("plan", "premium");
        });

        using var doc = WriteAndParse(logger);
        var user = doc.RootElement.GetProperty("user");

        Assert.Equal("usr_123", user.GetProperty("id").GetString());
        Assert.Equal("premium", user.GetProperty("plan").GetString());
    }

    [Fact]
    public void Write_AnonymousObject_MergedAtTopLevel()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        logger.Set(new { User = new { Id = "usr_123", Plan = "premium" } });
        logger.Set(new { Order = new { Total = 99.99 } });

        using var doc = WriteAndParse(logger);
        var root = doc.RootElement;

        var user = root.GetProperty("user");
        Assert.Equal("usr_123", user.GetProperty("id").GetString());
        Assert.Equal("premium", user.GetProperty("plan").GetString());

        var order = root.GetProperty("order");
        Assert.Equal(99.99, order.GetProperty("total").GetDouble());
    }

    [Fact]
    public void Write_RequestLogs_IncludedAsArray()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        logger.Info("step 1");
        logger.Warn("step 2");

        using var doc = WriteAndParse(logger);
        var logs = doc.RootElement.GetProperty("requestLogs");

        Assert.Equal(JsonValueKind.Array, logs.ValueKind);
        Assert.Equal(2, logs.GetArrayLength());
        Assert.Equal("step 1", logs[0].GetProperty("message").GetString());
        Assert.Equal("info", logs[0].GetProperty("level").GetString());
        Assert.Equal("warn", logs[1].GetProperty("level").GetString());
    }

    [Fact]
    public void Write_WithError_IncludesErrorObject()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        var ex = new InvalidOperationException("something broke");
        logger.Error(ex, "Payment failed");

        using var doc = WriteAndParse(logger, status: 500);
        var error = doc.RootElement.GetProperty("error");

        Assert.Equal("InvalidOperationException", error.GetProperty("name").GetString());
        Assert.Equal("something broke", error.GetProperty("message").GetString());
    }

    [Fact]
    public void Write_LevelDerivedFromStatus()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        // Status < 400 -> info
        using var doc200 = WriteAndParse(logger, status: 200);
        Assert.Equal("info", doc200.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void Write_DurationFormatted()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, 200, durationMs: 1250.5);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);

        Assert.Equal("1.25s", doc.RootElement.GetProperty("duration").GetString());
    }

    [Fact]
    public void Write_DurationMs_UnderOneSecond()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, 200, durationMs: 125.0);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);

        Assert.Equal("125ms", doc.RootElement.GetProperty("duration").GetString());
    }
}
