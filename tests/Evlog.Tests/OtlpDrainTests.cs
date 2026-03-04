using System.Net;
using System.Text;
using System.Text.Json;
using Evlog.Otlp;

namespace Evlog.Tests;

public class OtlpDrainTests
{
    private static readonly string SampleEventJson = JsonSerializer.Serialize(new
    {
        timestamp = "2026-03-03T12:00:00.000Z",
        level = "info",
        service = "test-api",
        environment = "staging",
        version = "2.1.0",
        commitHash = "abc123",
        region = "us-east-1",
        method = "GET",
        path = "/api/users/1",
        requestId = "req_001",
        status = 200,
        duration = "45ms",
        user = new { id = "usr_1", plan = "premium" },
        requestLogs = new[]
        {
            new { level = "info", message = "User fetched", timestamp = "2026-03-03T12:00:00.000Z" }
        }
    });

    private static EvlogDrainContext MakeContext(EvlogLevel level = EvlogLevel.Info, int status = 200, string? json = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json ?? SampleEventJson);
        return new EvlogDrainContext
        {
            EventJson = bytes,
            Level = level,
            Status = status,
        };
    }

    #region OTLP JSON Structure

    [Fact]
    public void BuildPayload_ProducesCorrectEnvelopeStructure()
    {
        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        var context = MakeContext();
        var payload = OtlpDrain.BuildPayload(context, options);

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Has resourceLogs array
        var resourceLogs = root.GetProperty("resourceLogs");
        Assert.Equal(JsonValueKind.Array, resourceLogs.ValueKind);
        Assert.Equal(1, resourceLogs.GetArrayLength());

        // Has resource.attributes
        var resource = resourceLogs[0].GetProperty("resource");
        var resourceAttrs = resource.GetProperty("attributes");
        Assert.True(resourceAttrs.GetArrayLength() > 0);

        // Has scopeLogs
        var scopeLogs = resourceLogs[0].GetProperty("scopeLogs");
        Assert.Equal(1, scopeLogs.GetArrayLength());

        // Scope name and version
        var scope = scopeLogs[0].GetProperty("scope");
        Assert.Equal("evlog", scope.GetProperty("name").GetString());
        Assert.Equal("0.0.2-beta", scope.GetProperty("version").GetString());

        // Has logRecords
        var logRecords = scopeLogs[0].GetProperty("logRecords");
        Assert.Equal(1, logRecords.GetArrayLength());
    }

    [Fact]
    public void BuildPayload_LogRecord_HasCorrectFields()
    {
        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        var context = MakeContext();
        var payload = OtlpDrain.BuildPayload(context, options);

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var logRecord = doc.RootElement
            .GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords")[0];

        // timeUnixNano — should be non-zero nanosecond string
        var timeNano = logRecord.GetProperty("timeUnixNano").GetString();
        Assert.NotNull(timeNano);
        Assert.True(long.Parse(timeNano!) > 0);

        // severityNumber and severityText
        Assert.Equal(9, logRecord.GetProperty("severityNumber").GetInt32());
        Assert.Equal("INFO", logRecord.GetProperty("severityText").GetString());

        // body.stringValue = full event JSON
        var body = logRecord.GetProperty("body").GetProperty("stringValue").GetString();
        Assert.NotNull(body);
        using var bodyDoc = JsonDocument.Parse(body!);
        Assert.Equal("test-api", bodyDoc.RootElement.GetProperty("service").GetString());

        // attributes array
        var attrs = logRecord.GetProperty("attributes");
        Assert.True(attrs.GetArrayLength() > 0);
    }

    [Fact]
    public void BuildPayload_LogRecord_ExcludesResourceFieldsFromAttributes()
    {
        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        var context = MakeContext();
        var payload = OtlpDrain.BuildPayload(context, options);

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var attrs = doc.RootElement
            .GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords")[0]
            .GetProperty("attributes");

        var keys = new HashSet<string>();
        foreach (var attr in attrs.EnumerateArray())
            keys.Add(attr.GetProperty("key").GetString()!);

        // These should NOT be in attributes (mapped to resource/severity/timestamp)
        Assert.DoesNotContain("timestamp", keys);
        Assert.DoesNotContain("level", keys);
        Assert.DoesNotContain("service", keys);
        Assert.DoesNotContain("environment", keys);
        Assert.DoesNotContain("version", keys);
        Assert.DoesNotContain("commitHash", keys);
        Assert.DoesNotContain("region", keys);

        // These SHOULD be in attributes
        Assert.Contains("method", keys);
        Assert.Contains("path", keys);
        Assert.Contains("status", keys);
        Assert.Contains("duration", keys);
    }

    #endregion

    #region Severity Mapping

    [Theory]
    [InlineData(EvlogLevel.Debug, 5, "DEBUG")]
    [InlineData(EvlogLevel.Info, 9, "INFO")]
    [InlineData(EvlogLevel.Warn, 13, "WARN")]
    [InlineData(EvlogLevel.Error, 17, "ERROR")]
    public void MapSeverity_AllLevels(EvlogLevel level, int expectedNumber, string expectedText)
    {
        var (number, text) = OtlpDrain.MapSeverity(level);
        Assert.Equal(expectedNumber, number);
        Assert.Equal(expectedText, text);
    }

    [Theory]
    [InlineData(EvlogLevel.Debug, 5, "DEBUG")]
    [InlineData(EvlogLevel.Info, 9, "INFO")]
    [InlineData(EvlogLevel.Warn, 13, "WARN")]
    [InlineData(EvlogLevel.Error, 17, "ERROR")]
    public void BuildPayload_UsesCorrectSeverityFromContext(EvlogLevel level, int expectedNumber, string expectedText)
    {
        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        var context = MakeContext(level: level);
        var payload = OtlpDrain.BuildPayload(context, options);

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var logRecord = doc.RootElement
            .GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords")[0];

        Assert.Equal(expectedNumber, logRecord.GetProperty("severityNumber").GetInt32());
        Assert.Equal(expectedText, logRecord.GetProperty("severityText").GetString());
    }

    #endregion

    #region Resource Attributes

    [Fact]
    public void BuildResourceAttributes_ExtractsFromEvent()
    {
        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        using var doc = JsonDocument.Parse(SampleEventJson);
        var attrs = OtlpDrain.BuildResourceAttributes(doc.RootElement, options);

        var json = JsonSerializer.Serialize(attrs);
        using var attrsDoc = JsonDocument.Parse(json);
        var arr = attrsDoc.RootElement;

        AssertResourceAttribute(arr, "service.name", "test-api");
        AssertResourceAttribute(arr, "deployment.environment", "staging");
        AssertResourceAttribute(arr, "service.version", "2.1.0");
        AssertResourceAttribute(arr, "cloud.region", "us-east-1");
        AssertResourceAttribute(arr, "vcs.commit.id", "abc123");
    }

    [Fact]
    public void BuildResourceAttributes_ServiceNameOverride()
    {
        var options = new OtlpDrainOptions
        {
            Endpoint = "http://localhost:4318",
            ServiceName = "overridden-service"
        };
        using var doc = JsonDocument.Parse(SampleEventJson);
        var attrs = OtlpDrain.BuildResourceAttributes(doc.RootElement, options);

        var json = JsonSerializer.Serialize(attrs);
        using var attrsDoc = JsonDocument.Parse(json);
        AssertResourceAttribute(attrsDoc.RootElement, "service.name", "overridden-service");
    }

    [Fact]
    public void BuildResourceAttributes_IncludesCustomResourceAttributes()
    {
        var options = new OtlpDrainOptions
        {
            Endpoint = "http://localhost:4318",
            ResourceAttributes = new() { ["custom.attr"] = "custom-value" }
        };
        using var doc = JsonDocument.Parse(SampleEventJson);
        var attrs = OtlpDrain.BuildResourceAttributes(doc.RootElement, options);

        var json = JsonSerializer.Serialize(attrs);
        using var attrsDoc = JsonDocument.Parse(json);
        AssertResourceAttribute(attrsDoc.RootElement, "custom.attr", "custom-value");
    }

    [Fact]
    public void BuildResourceAttributes_MissingOptionalFields_DoesNotFail()
    {
        var minimalEvent = JsonSerializer.Serialize(new
        {
            timestamp = "2026-03-03T12:00:00.000Z",
            level = "info",
            service = "minimal",
            status = 200,
        });

        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        using var doc = JsonDocument.Parse(minimalEvent);
        var attrs = OtlpDrain.BuildResourceAttributes(doc.RootElement, options);

        var json = JsonSerializer.Serialize(attrs);
        using var attrsDoc = JsonDocument.Parse(json);
        var arr = attrsDoc.RootElement;

        // Should only have service.name
        AssertResourceAttribute(arr, "service.name", "minimal");
        Assert.Equal(1, arr.GetArrayLength());
    }

    #endregion

    #region Attribute Value Conversion

    [Fact]
    public void ToAttributeValue_String()
    {
        using var doc = JsonDocument.Parse("\"hello\"");
        var result = OtlpDrain.ToAttributeValue(doc.RootElement);
        var json = JsonSerializer.Serialize(result);
        using var resultDoc = JsonDocument.Parse(json);
        Assert.Equal("hello", resultDoc.RootElement.GetProperty("stringValue").GetString());
    }

    [Fact]
    public void ToAttributeValue_Integer()
    {
        using var doc = JsonDocument.Parse("42");
        var result = OtlpDrain.ToAttributeValue(doc.RootElement);
        var json = JsonSerializer.Serialize(result);
        using var resultDoc = JsonDocument.Parse(json);
        Assert.Equal("42", resultDoc.RootElement.GetProperty("intValue").GetString());
    }

    [Fact]
    public void ToAttributeValue_Boolean()
    {
        using var doc = JsonDocument.Parse("true");
        var result = OtlpDrain.ToAttributeValue(doc.RootElement);
        var json = JsonSerializer.Serialize(result);
        using var resultDoc = JsonDocument.Parse(json);
        Assert.True(resultDoc.RootElement.GetProperty("boolValue").GetBoolean());
    }

    [Fact]
    public void ToAttributeValue_Object_SerializesToJsonString()
    {
        using var doc = JsonDocument.Parse("{\"nested\":true}");
        var result = OtlpDrain.ToAttributeValue(doc.RootElement);
        var json = JsonSerializer.Serialize(result);
        using var resultDoc = JsonDocument.Parse(json);
        var sv = resultDoc.RootElement.GetProperty("stringValue").GetString();
        Assert.Contains("nested", sv);
    }

    #endregion

    #region Environment Variable Fallback

    [Fact]
    public void ResolveFromEnvironment_SetsEndpoint()
    {
        var options = new OtlpDrainOptions();

        var envKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var original = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable(envKey, "http://collector:4318");
            options.ResolveFromEnvironment();
            Assert.Equal("http://collector:4318", options.Endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }

    [Fact]
    public void ResolveFromEnvironment_SetsServiceName()
    {
        var options = new OtlpDrainOptions();

        var envKey = "OTEL_SERVICE_NAME";
        var original = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable(envKey, "my-svc");
            options.ResolveFromEnvironment();
            Assert.Equal("my-svc", options.ServiceName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }

    [Fact]
    public void ResolveFromEnvironment_ParsesHeaders()
    {
        var options = new OtlpDrainOptions();

        var envKey = "OTEL_EXPORTER_OTLP_HEADERS";
        var original = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable(envKey, "Authorization=Bearer token,X-Custom=value");
            options.ResolveFromEnvironment();
            Assert.NotNull(options.Headers);
            Assert.Equal("Bearer token", options.Headers!["Authorization"]);
            Assert.Equal("value", options.Headers["X-Custom"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }

    [Fact]
    public void ResolveFromEnvironment_DoesNotOverrideExplicitValues()
    {
        var options = new OtlpDrainOptions
        {
            Endpoint = "http://explicit:4318",
            ServiceName = "explicit-svc",
            Headers = new() { ["Auth"] = "mine" }
        };

        var envKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var original = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable(envKey, "http://env:4318");
            options.ResolveFromEnvironment();
            Assert.Equal("http://explicit:4318", options.Endpoint);
            Assert.Equal("explicit-svc", options.ServiceName);
            Assert.Equal("mine", options.Headers!["Auth"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }

    #endregion

    #region Header Parsing

    [Fact]
    public void ParseHeaders_CommaSeparatedKeyValue()
    {
        var result = OtlpDrainOptions.ParseHeaders("Key1=Value1,Key2=Value2");
        Assert.NotNull(result);
        Assert.Equal("Value1", result!["Key1"]);
        Assert.Equal("Value2", result["Key2"]);
    }

    [Fact]
    public void ParseHeaders_UrlEncoded()
    {
        var result = OtlpDrainOptions.ParseHeaders("Authorization=Basic%20token");
        Assert.NotNull(result);
        Assert.Equal("Basic token", result!["Authorization"]);
    }

    [Fact]
    public void ParseHeaders_EmptyString_ReturnsNull()
    {
        var result = OtlpDrainOptions.ParseHeaders("");
        Assert.Null(result);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Create_ThrowsOnMissingEndpoint()
    {
        Assert.Throws<ArgumentException>(() => OtlpDrain.Create(new OtlpDrainOptions()));
    }

    [Fact]
    public async Task Drain_DoesNotThrowOnHttpError()
    {
        // The drain should catch all exceptions and log to stderr
        // We verify by testing with a non-routable endpoint (should timeout or fail)
        var drain = OtlpDrain.Create(new OtlpDrainOptions
        {
            Endpoint = "http://192.0.2.1:1", // TEST-NET, non-routable
            TimeoutMs = 100,
        });

        var context = MakeContext();

        // Should not throw
        await drain(context);
    }

    #endregion

    #region Integration with Mock HTTP

    [Fact]
    public async Task Drain_SendsCorrectPayloadToEndpoint()
    {
        string? capturedBody = null;
        Dictionary<string, string>? capturedHeaders = null;

        // Use a local HTTP listener as mock OTLP collector
        using var listener = new HttpListener();
        var port = GetAvailablePort();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
            capturedBody = await reader.ReadToEndAsync();
            capturedHeaders = new Dictionary<string, string>();
            foreach (string key in ctx.Request.Headers.AllKeys!)
                capturedHeaders[key] = ctx.Request.Headers[key]!;
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var drain = OtlpDrain.Create(new OtlpDrainOptions
        {
            Endpoint = $"http://localhost:{port}",
            Headers = new() { ["X-Test-Header"] = "test-value" },
            TimeoutMs = 5000,
        });

        await drain(MakeContext());
        await listenerTask;

        // Verify request was sent
        Assert.NotNull(capturedBody);

        // Verify OTLP JSON structure
        using var doc = JsonDocument.Parse(capturedBody!);
        var resourceLogs = doc.RootElement.GetProperty("resourceLogs");
        Assert.Equal(1, resourceLogs.GetArrayLength());

        var logRecord = resourceLogs[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords")[0];
        Assert.Equal(9, logRecord.GetProperty("severityNumber").GetInt32());

        // Verify custom header was sent
        Assert.True(capturedHeaders!.ContainsKey("X-Test-Header"));
        Assert.Equal("test-value", capturedHeaders["X-Test-Header"]);

        listener.Stop();
    }

    #endregion

    #region Timestamp Conversion

    [Fact]
    public void BuildPayload_ConvertsTimestampToNanoseconds()
    {
        var options = new OtlpDrainOptions { Endpoint = "http://localhost:4318" };
        var context = MakeContext();
        var payload = OtlpDrain.BuildPayload(context, options);

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var logRecord = doc.RootElement
            .GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords")[0];

        var timeNano = long.Parse(logRecord.GetProperty("timeUnixNano").GetString()!);

        // 2026-03-03T12:00:00.000Z → should be roughly 1.77e18 nanoseconds
        Assert.True(timeNano > 1_000_000_000_000_000_000L, "Timestamp should be in nanoseconds");

        // Verify it's the correct timestamp (2026-03-03T12:00:00Z)
        var expectedMs = new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(expectedMs * 1_000_000, timeNano);
    }

    #endregion

    #region Helpers

    private static void AssertResourceAttribute(JsonElement array, string key, string expectedValue)
    {
        foreach (var attr in array.EnumerateArray())
        {
            if (attr.GetProperty("key").GetString() == key)
            {
                Assert.Equal(expectedValue, attr.GetProperty("value").GetProperty("stringValue").GetString());
                return;
            }
        }
        Assert.Fail($"Resource attribute '{key}' not found");
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    #endregion
}
