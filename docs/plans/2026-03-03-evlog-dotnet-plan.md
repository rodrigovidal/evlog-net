# evlog .NET — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a high-performance, near-zero-allocation .NET port of evlog's wide-event structured logging for ASP.NET Core.

**Architecture:** Hybrid API — typed `Set(key, value)` overloads for zero-alloc hot path + `Set(object)` for ergonomic anonymous objects (serialized via `JsonSerializer` into pooled fragment buffer). At emit time, entries are written directly to a `Utf8JsonWriter` backed by `ArrayPool<byte>`. `RequestLogger` instances are pooled via `ObjectPool<T>`. Head-sampled-out requests get a disabled logger (all methods are inlined no-ops). Dot-notation keys (e.g. `"user.id"`) are expanded into nested JSON at emit time.

**Tech Stack:** .NET 10, ASP.NET Core, System.Text.Json (Utf8JsonWriter), Microsoft.Extensions.ObjectPool, xUnit, NSubstitute

**Reference design:** `docs/plans/2026-03-03-evlog-dotnet-design.md`

---

## High-Performance Design Decisions

| Concern | Approach |
|---------|----------|
| Avoid boxing value types | Typed `Set()` overloads + `ContextEntry` discriminated-union struct |
| Avoid per-request allocation | `ObjectPool<RequestLogger>` with `IResettable` |
| Zero-alloc sampled-out path | `_active` bool flag; all methods `[AggressiveInlining]` return immediately |
| JSON output | `Utf8JsonWriter` + `ArrayBufferWriter<byte>` backed by `ArrayPool` |
| Property names | Pre-encoded as `static readonly JsonEncodedText` |
| String formatting | `IUtf8SpanFormattable` for value types, no intermediate strings |
| Nested JSON from flat keys | Dot-notation expanded at emit time (once per request, cold path) |
| Request log entries | Pooled `List<RequestLogEntry>` cleared on reset |
| Pretty output | Direct `Console.Out` writes with ANSI escape codes |

---

## Task 1: Solution Scaffold

**Files:**
- Create: `Evlog.sln`
- Create: `src/Evlog/Evlog.csproj`
- Create: `tests/Evlog.Tests/Evlog.Tests.csproj`

**Step 1: Create solution and projects**

```bash
cd /Users/rodrigovidalaraujo/desenvolvimento/evlog.net
dotnet new sln -n Evlog
mkdir -p src/Evlog tests/Evlog.Tests
dotnet new classlib -n Evlog -o src/Evlog -f net10.0
dotnet new xunit -n Evlog.Tests -o tests/Evlog.Tests -f net10.0
dotnet sln add src/Evlog/Evlog.csproj tests/Evlog.Tests/Evlog.Tests.csproj
dotnet add tests/Evlog.Tests reference src/Evlog
```

**Step 2: Configure Evlog.csproj**

Replace `src/Evlog/Evlog.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Evlog</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

Replace `tests/Evlog.Tests/Evlog.Tests.csproj` to add NSubstitute:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="*" />
    <PackageReference Include="xunit.v3" Version="*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="*" />
    <PackageReference Include="NSubstitute" Version="*" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Evlog\Evlog.csproj" />
  </ItemGroup>
</Project>
```

Delete auto-generated `Class1.cs` and `UnitTest1.cs`.

**Step 3: Verify build**

Run: `dotnet build Evlog.sln`
Expected: Build succeeded

**Step 4: Commit**

```bash
git init
git add -A
git commit -m "chore: scaffold solution with Evlog lib and test projects"
```

---

## Task 2: EvlogLevel + ContextEntry Value Type

**Files:**
- Create: `src/Evlog/EvlogLevel.cs`
- Create: `src/Evlog/ContextEntry.cs`
- Create: `tests/Evlog.Tests/ContextEntryTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Evlog.Tests/ContextEntryTests.cs
using Evlog;

namespace Evlog.Tests;

public class ContextEntryTests
{
    [Fact]
    public void CreateString_StoresStringValue()
    {
        var entry = ContextEntry.String("user.name", "alice");

        Assert.Equal("user.name", entry.Key);
        Assert.Equal(ContextValueKind.String, entry.Kind);
        Assert.Equal("alice", entry.StringValue);
    }

    [Fact]
    public void CreateInt_StoresIntWithoutBoxing()
    {
        var entry = ContextEntry.Int("order.count", 42);

        Assert.Equal("order.count", entry.Key);
        Assert.Equal(ContextValueKind.Int, entry.Kind);
        Assert.Equal(42, entry.IntValue);
    }

    [Fact]
    public void CreateLong_StoresLong()
    {
        var entry = ContextEntry.Long("timestamp", 1709500000000L);

        Assert.Equal(ContextValueKind.Long, entry.Kind);
        Assert.Equal(1709500000000L, entry.LongValue);
    }

    [Fact]
    public void CreateDouble_StoresDouble()
    {
        var entry = ContextEntry.Double("order.total", 99.99);

        Assert.Equal(ContextValueKind.Double, entry.Kind);
        Assert.Equal(99.99, entry.DoubleValue);
    }

    [Fact]
    public void CreateBool_StoresBool()
    {
        var entry = ContextEntry.Bool("user.premium", true);

        Assert.Equal(ContextValueKind.Bool, entry.Kind);
        Assert.True(entry.BoolValue);
    }

    [Fact]
    public void CreateJsonFragment_StoresOffsetAndLength()
    {
        var entry = ContextEntry.JsonFragment("user", 0, 25);

        Assert.Equal(ContextValueKind.JsonFragment, entry.Kind);
        Assert.Equal(0, entry.FragmentOffset);
        Assert.Equal(25, entry.FragmentLength);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Evlog.Tests --filter "ContextEntryTests" -v q`
Expected: FAIL — types don't exist yet

**Step 3: Write implementation**

```csharp
// src/Evlog/EvlogLevel.cs
namespace Evlog;

public enum EvlogLevel : byte
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}
```

```csharp
// src/Evlog/ContextEntry.cs
using System.Runtime.InteropServices;

namespace Evlog;

public enum ContextValueKind : byte
{
    String,
    Int,
    Long,
    Double,
    Bool,
    JsonFragment,
}

[StructLayout(LayoutKind.Auto)]
public struct ContextEntry
{
    public string Key;
    public ContextValueKind Kind;
    public string? StringValue;
    public long LongValue;
    public double DoubleValue;
    public int FragmentOffset;
    public int FragmentLength;

    public int IntValue
    {
        readonly get => (int)LongValue;
        set => LongValue = value;
    }

    public bool BoolValue
    {
        readonly get => LongValue != 0;
        set => LongValue = value ? 1 : 0;
    }

    public static ContextEntry String(string key, string value) => new()
    {
        Key = key, Kind = ContextValueKind.String, StringValue = value,
    };

    public static ContextEntry Int(string key, int value) => new()
    {
        Key = key, Kind = ContextValueKind.Int, LongValue = value,
    };

    public static ContextEntry Long(string key, long value) => new()
    {
        Key = key, Kind = ContextValueKind.Long, LongValue = value,
    };

    public static ContextEntry Double(string key, double value) => new()
    {
        Key = key, Kind = ContextValueKind.Double, DoubleValue = value,
    };

    public static ContextEntry Bool(string key, bool value) => new()
    {
        Key = key, Kind = ContextValueKind.Bool, LongValue = value ? 1 : 0,
    };

    public static ContextEntry JsonFragment(string key, int offset, int length) => new()
    {
        Key = key, Kind = ContextValueKind.JsonFragment, FragmentOffset = offset, FragmentLength = length,
    };
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Evlog.Tests --filter "ContextEntryTests" -v q`
Expected: PASS (6 tests)

**Step 5: Commit**

```bash
git add src/Evlog/EvlogLevel.cs src/Evlog/ContextEntry.cs tests/Evlog.Tests/ContextEntryTests.cs
git commit -m "feat: add EvlogLevel enum and zero-alloc ContextEntry value type"
```

---

## Task 3: RequestLogEntry + RequestLogger Core (Set Overloads)

**Files:**
- Create: `src/Evlog/RequestLogEntry.cs`
- Create: `src/Evlog/RequestLogger.cs`
- Create: `tests/Evlog.Tests/RequestLoggerTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/RequestLoggerTests.cs
using Evlog;

namespace Evlog.Tests;

public class RequestLoggerTests
{
    [Fact]
    public void Set_String_AccumulatesEntry()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("user.id", "usr_123");

        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal("user.id", entries[0].Key);
        Assert.Equal("usr_123", entries[0].StringValue);
    }

    [Fact]
    public void Set_Int_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("order.count", 5);

        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal(ContextValueKind.Int, entries[0].Kind);
        Assert.Equal(5, entries[0].IntValue);
    }

    [Fact]
    public void Set_Double_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("order.total", 99.99);

        var entries = logger.GetEntries();
        Assert.Equal(ContextValueKind.Double, entries[0].Kind);
        Assert.Equal(99.99, entries[0].DoubleValue);
    }

    [Fact]
    public void Set_Bool_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("user.premium", true);

        var entries = logger.GetEntries();
        Assert.Equal(ContextValueKind.Bool, entries[0].Kind);
        Assert.True(entries[0].BoolValue);
    }

    [Fact]
    public void Set_Long_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("event.timestamp", 1709500000000L);

        var entries = logger.GetEntries();
        Assert.Equal(ContextValueKind.Long, entries[0].Kind);
        Assert.Equal(1709500000000L, entries[0].LongValue);
    }

    [Fact]
    public void Set_MultipleKeys_AccumulatesAll()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("user.id", "usr_123");
        logger.Set("user.plan", "premium");
        logger.Set("order.total", 99.99);

        var entries = logger.GetEntries();
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void Set_DuplicateKey_FirstWriteWins()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set("user.id", "first");
        logger.Set("user.id", "second");

        var entries = logger.GetEntries();
        // Both stored, but emit will use first-write-wins
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Set_WhenInactive_IsNoOp()
    {
        var logger = new RequestLogger();
        // Not activated — simulates sampled-out request

        logger.Set("user.id", "usr_123");
        logger.Set("order.total", 99.99);

        var entries = logger.GetEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void TryReset_ClearsAllState()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("user.id", "usr_123");

        logger.TryReset();

        var entries = logger.GetEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void SetJson_WritesFragmentToBuffer()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.SetJson("user", writer =>
        {
            writer.WriteString("id", "usr_123");
            writer.WriteString("plan", "premium");
        });

        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal(ContextValueKind.JsonFragment, entries[0].Kind);
        Assert.True(entries[0].FragmentLength > 0);
    }

    [Fact]
    public void Set_AnonymousObject_SerializesToJsonFragments()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");

        logger.Set(new { User = new { Id = "usr_123", Plan = "premium" } });

        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal(ContextValueKind.JsonFragment, entries[0].Kind);
        // The key should be empty string (root-level merge)
        Assert.Equal("", entries[0].Key);
        Assert.True(entries[0].FragmentLength > 0);
    }

    [Fact]
    public void Set_AnonymousObject_WhenInactive_IsNoOp()
    {
        var logger = new RequestLogger();

        logger.Set(new { User = new { Id = "usr_123" } });

        var entries = logger.GetEntries();
        Assert.Empty(entries);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "RequestLoggerTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/RequestLogEntry.cs
namespace Evlog;

public readonly struct RequestLogEntry
{
    public EvlogLevel Level { get; init; }
    public string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? Category { get; init; }
}
```

```csharp
// src/Evlog/RequestLogger.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;

namespace Evlog;

public sealed class RequestLogger : IResettable
{
    private bool _active;
    private string _service = "";
    private string _environment = "";
    private string? _version;
    private string? _commitHash;
    private string? _region;
    private string? _method;
    private string? _path;
    private string? _requestId;
    private long _startTimestamp;

    private readonly List<ContextEntry> _entries = new(16);
    private readonly List<RequestLogEntry> _requestLogs = new(4);
    private ArrayBufferWriter<byte>? _jsonFragmentBuffer;

    // Error state
    private Exception? _error;
    private string? _errorMessage;

    /// <summary>
    /// Activate this logger for a new request. Called by middleware.
    /// </summary>
    public void Activate(
        string service,
        string environment,
        string? version = null,
        string? commitHash = null,
        string? region = null)
    {
        _active = true;
        _service = service;
        _environment = environment;
        _version = version;
        _commitHash = commitHash;
        _region = region;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public void SetRequest(string method, string path, string? requestId = null)
    {
        _method = method;
        _path = path;
        _requestId = requestId;
    }

    public bool IsActive => _active;
    public string? Path => _path;
    public string? Method => _method;
    public long StartTimestamp => _startTimestamp;

    // --- Set overloads: typed to avoid boxing ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(string key, string value)
    {
        if (!_active) return;
        _entries.Add(ContextEntry.String(key, value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(string key, int value)
    {
        if (!_active) return;
        _entries.Add(ContextEntry.Int(key, value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(string key, long value)
    {
        if (!_active) return;
        _entries.Add(ContextEntry.Long(key, value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(string key, double value)
    {
        if (!_active) return;
        _entries.Add(ContextEntry.Double(key, value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(string key, bool value)
    {
        if (!_active) return;
        _entries.Add(ContextEntry.Bool(key, value));
    }

    /// <summary>
    /// Ergonomic API: set context from an anonymous or typed object.
    /// Serializes via System.Text.Json into the internal fragment buffer.
    /// Allocates the object but the JSON path is efficient.
    /// Example: log.Set(new { User = new { Id = "123", Plan = "premium" } })
    /// </summary>
    public void Set(object context)
    {
        if (!_active) return;

        _jsonFragmentBuffer ??= new ArrayBufferWriter<byte>(256);

        int offset = _jsonFragmentBuffer.WrittenCount;
        using var writer = new Utf8JsonWriter(
            _jsonFragmentBuffer,
            new JsonWriterOptions { SkipValidation = true });

        JsonSerializer.Serialize(writer, context, context.GetType(), JsonOptions);
        writer.Flush();

        int length = _jsonFragmentBuffer.WrittenCount - offset;
        // Empty key means root-level merge at emit time
        _entries.Add(ContextEntry.JsonFragment("", offset, length));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Set a complex nested value by writing directly to a Utf8JsonWriter.
    /// The callback writes the object body (without the enclosing braces).
    /// </summary>
    public void SetJson(string key, Action<Utf8JsonWriter> writeBody)
    {
        if (!_active) return;

        _jsonFragmentBuffer ??= new ArrayBufferWriter<byte>(256);

        int offset = _jsonFragmentBuffer.WrittenCount;
        using var writer = new Utf8JsonWriter(
            _jsonFragmentBuffer,
            new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writeBody(writer);
        writer.WriteEndObject();
        writer.Flush();

        int length = _jsonFragmentBuffer.WrittenCount - offset;
        _entries.Add(ContextEntry.JsonFragment(key, offset, length));
    }

    // --- Log entry methods ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(string message)
    {
        if (!_active) return;
        _requestLogs.Add(new RequestLogEntry
        {
            Level = EvlogLevel.Info,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warn(string message)
    {
        if (!_active) return;
        _requestLogs.Add(new RequestLogEntry
        {
            Level = EvlogLevel.Warn,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(Exception ex, string? message = null)
    {
        if (!_active) return;
        _error = ex;
        _errorMessage = message;
        _requestLogs.Add(new RequestLogEntry
        {
            Level = EvlogLevel.Error,
            Message = message ?? ex.Message,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    // --- Accessors for testing and emit ---

    public IReadOnlyList<ContextEntry> GetEntries() => _entries;
    public IReadOnlyList<RequestLogEntry> GetRequestLogs() => _requestLogs;
    public Exception? GetError() => _error;
    public string? GetErrorMessage() => _errorMessage;

    public string Service => _service;
    public string Environment => _environment;
    public string? Version => _version;
    public string? CommitHash => _commitHash;
    public string? Region => _region;

    public ReadOnlySpan<byte> GetJsonFragmentBuffer()
    {
        return _jsonFragmentBuffer is not null
            ? _jsonFragmentBuffer.WrittenSpan
            : ReadOnlySpan<byte>.Empty;
    }

    // --- IResettable ---

    public bool TryReset()
    {
        _active = false;
        _service = "";
        _environment = "";
        _version = null;
        _commitHash = null;
        _region = null;
        _method = null;
        _path = null;
        _requestId = null;
        _startTimestamp = 0;
        _entries.Clear();
        _requestLogs.Clear();
        _jsonFragmentBuffer?.ResetWrittenCount();
        _error = null;
        _errorMessage = null;
        return true;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "RequestLoggerTests" -v q`
Expected: PASS (10 tests)

**Step 5: Commit**

```bash
git add src/Evlog/RequestLogEntry.cs src/Evlog/RequestLogger.cs tests/Evlog.Tests/RequestLoggerTests.cs
git commit -m "feat: add RequestLogger with zero-alloc typed Set() overloads and object pooling support"
```

---

## Task 4: RequestLogger — Info/Warn/Error Methods

**Files:**
- Create: `tests/Evlog.Tests/RequestLoggerLogEntryTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/RequestLoggerLogEntryTests.cs
using Evlog;

namespace Evlog.Tests;

public class RequestLoggerLogEntryTests
{
    [Fact]
    public void Info_AddsInfoEntry()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        logger.Info("Order created");

        var logs = logger.GetRequestLogs();
        Assert.Single(logs);
        Assert.Equal(EvlogLevel.Info, logs[0].Level);
        Assert.Equal("Order created", logs[0].Message);
    }

    [Fact]
    public void Warn_AddsWarnEntry()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        logger.Warn("Rate limit approaching");

        var logs = logger.GetRequestLogs();
        Assert.Single(logs);
        Assert.Equal(EvlogLevel.Warn, logs[0].Level);
    }

    [Fact]
    public void Error_CapturesExceptionAndMessage()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        var ex = new InvalidOperationException("something broke");

        logger.Error(ex, "Payment processing failed");

        Assert.Same(ex, logger.GetError());
        Assert.Equal("Payment processing failed", logger.GetErrorMessage());

        var logs = logger.GetRequestLogs();
        Assert.Single(logs);
        Assert.Equal(EvlogLevel.Error, logs[0].Level);
        Assert.Equal("Payment processing failed", logs[0].Message);
    }

    [Fact]
    public void Error_WithoutMessage_UsesExceptionMessage()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");
        var ex = new InvalidOperationException("the original message");

        logger.Error(ex);

        var logs = logger.GetRequestLogs();
        Assert.Equal("the original message", logs[0].Message);
    }

    [Fact]
    public void AllMethods_WhenInactive_AreNoOps()
    {
        var logger = new RequestLogger();

        logger.Info("should not appear");
        logger.Warn("should not appear");
        logger.Error(new Exception("nope"));

        Assert.Empty(logger.GetRequestLogs());
        Assert.Null(logger.GetError());
    }

    [Fact]
    public void MultipleEntries_PreserveOrder()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        logger.Info("step 1");
        logger.Warn("step 2");
        logger.Info("step 3");

        var logs = logger.GetRequestLogs();
        Assert.Equal(3, logs.Count);
        Assert.Equal("step 1", logs[0].Message);
        Assert.Equal("step 2", logs[1].Message);
        Assert.Equal("step 3", logs[2].Message);
    }
}
```

**Step 2: Run tests to verify they pass** (implementation already exists from Task 3)

Run: `dotnet test tests/Evlog.Tests --filter "RequestLoggerLogEntryTests" -v q`
Expected: PASS (6 tests) — these should pass with the Task 3 implementation

**Step 3: Commit**

```bash
git add tests/Evlog.Tests/RequestLoggerLogEntryTests.cs
git commit -m "test: add RequestLogger log entry method tests"
```

---

## Task 5: Head Sampling

**Files:**
- Create: `src/Evlog/SamplingOptions.cs`
- Create: `src/Evlog/Sampling.cs`
- Create: `tests/Evlog.Tests/SamplingTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/SamplingTests.cs
using Evlog;

namespace Evlog.Tests;

public class HeadSamplingTests
{
    [Fact]
    public void ShouldSample_NoRates_AlwaysTrue()
    {
        var options = new SamplingOptions();

        Assert.True(Sampling.ShouldSample(EvlogLevel.Info, options));
        Assert.True(Sampling.ShouldSample(EvlogLevel.Debug, options));
    }

    [Fact]
    public void ShouldSample_ZeroPercent_AlwaysFalse()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int>
            {
                [EvlogLevel.Info] = 0,
            }
        };

        Assert.False(Sampling.ShouldSample(EvlogLevel.Info, options));
    }

    [Fact]
    public void ShouldSample_HundredPercent_AlwaysTrue()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int>
            {
                [EvlogLevel.Info] = 100,
            }
        };

        Assert.True(Sampling.ShouldSample(EvlogLevel.Info, options));
    }

    [Fact]
    public void ShouldSample_ErrorDefaultsTo100_WhenNotConfigured()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int>
            {
                [EvlogLevel.Info] = 0,
            }
        };

        // Error not in rates → defaults to 100%
        Assert.True(Sampling.ShouldSample(EvlogLevel.Error, options));
    }

    [Fact]
    public void ShouldSample_ErrorCanBeOverridden()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int>
            {
                [EvlogLevel.Error] = 0,
            }
        };

        Assert.False(Sampling.ShouldSample(EvlogLevel.Error, options));
    }

    [Fact]
    public void ShouldSample_UnconfiguredLevel_DefaultsTo100()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int>
            {
                [EvlogLevel.Info] = 10,
            }
        };

        // Debug not in rates (and not error) → defaults to 100%
        Assert.True(Sampling.ShouldSample(EvlogLevel.Debug, options));
    }

    [Fact]
    public void ShouldSample_Probabilistic_RoughlyMatchesRate()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int>
            {
                [EvlogLevel.Info] = 50,
            }
        };

        int sampled = 0;
        const int iterations = 10_000;
        for (int i = 0; i < iterations; i++)
        {
            if (Sampling.ShouldSample(EvlogLevel.Info, options))
                sampled++;
        }

        double rate = (double)sampled / iterations;
        Assert.InRange(rate, 0.40, 0.60); // ~50% ± 10%
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "HeadSamplingTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/SamplingOptions.cs
namespace Evlog;

public sealed class SamplingOptions
{
    /// <summary>
    /// Head sampling rates per log level (0-100 percentage).
    /// Unconfigured levels default to 100%. Error defaults to 100% unless explicitly set.
    /// </summary>
    public Dictionary<EvlogLevel, int>? Rates { get; set; }

    /// <summary>
    /// Tail sampling conditions. If ANY condition matches, the event is force-kept.
    /// </summary>
    public List<TailSamplingCondition>? Keep { get; set; }
}

public sealed class TailSamplingCondition
{
    /// <summary>Keep if response status >= this value.</summary>
    public int? Status { get; set; }

    /// <summary>Keep if request duration >= this value (milliseconds).</summary>
    public int? Duration { get; set; }

    /// <summary>Keep if request path matches this glob pattern.</summary>
    public string? Path { get; set; }
}
```

```csharp
// src/Evlog/Sampling.cs
using System.Runtime.CompilerServices;

namespace Evlog;

public static class Sampling
{
    [ThreadStatic]
    private static Random? t_random;

    private static Random Random => t_random ??= new Random();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ShouldSample(EvlogLevel level, SamplingOptions options)
    {
        if (options.Rates is null) return true;

        int percentage;
        if (options.Rates.TryGetValue(level, out int configured))
        {
            percentage = configured;
        }
        else
        {
            // Error defaults to 100% when not explicitly configured
            percentage = level == EvlogLevel.Error ? 100 : 100;
        }

        if (percentage <= 0) return false;
        if (percentage >= 100) return true;

        return Random.Next(100) < percentage;
    }

    public static bool ShouldKeep(
        int? status,
        double? durationMs,
        string? path,
        SamplingOptions options)
    {
        if (options.Keep is not { Count: > 0 }) return false;

        foreach (var condition in options.Keep)
        {
            if (condition.Status is not null
                && status is not null
                && status.Value >= condition.Status.Value)
                return true;

            if (condition.Duration is not null
                && durationMs is not null
                && durationMs.Value >= condition.Duration.Value)
                return true;

            if (condition.Path is not null
                && path is not null
                && GlobMatcher.IsMatch(path, condition.Path))
                return true;
        }

        return false;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "HeadSamplingTests" -v q`
Expected: PASS (7 tests)

**Step 5: Commit**

```bash
git add src/Evlog/SamplingOptions.cs src/Evlog/Sampling.cs tests/Evlog.Tests/SamplingTests.cs
git commit -m "feat: add head sampling with per-level rates"
```

---

## Task 6: Tail Sampling + GlobMatcher

**Files:**
- Create: `src/Evlog/GlobMatcher.cs`
- Create: `tests/Evlog.Tests/TailSamplingTests.cs`
- Create: `tests/Evlog.Tests/GlobMatcherTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/GlobMatcherTests.cs
using Evlog;

namespace Evlog.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("/api/orders", "/api/orders", true)]
    [InlineData("/api/orders", "/api/users", false)]
    [InlineData("/api/orders/123", "/api/orders/*", true)]
    [InlineData("/api/orders/123/items", "/api/orders/*", false)]
    [InlineData("/api/orders/123/items", "/api/orders/**", true)]
    [InlineData("/api/orders", "/api/**", true)]
    [InlineData("/health", "/api/**", false)]
    [InlineData("/api/v1/orders", "/api/*/orders", true)]
    public void IsMatch_MatchesGlobPatterns(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(path, pattern));
    }
}
```

```csharp
// tests/Evlog.Tests/TailSamplingTests.cs
using Evlog;

namespace Evlog.Tests;

public class TailSamplingTests
{
    [Fact]
    public void ShouldKeep_NoConditions_ReturnsFalse()
    {
        var options = new SamplingOptions();

        Assert.False(Sampling.ShouldKeep(500, 100, "/api/test", options));
    }

    [Fact]
    public void ShouldKeep_StatusCondition_KeepsWhenAboveThreshold()
    {
        var options = new SamplingOptions
        {
            Keep = [new() { Status = 400 }]
        };

        Assert.True(Sampling.ShouldKeep(500, null, null, options));
        Assert.True(Sampling.ShouldKeep(400, null, null, options));
        Assert.False(Sampling.ShouldKeep(200, null, null, options));
    }

    [Fact]
    public void ShouldKeep_DurationCondition_KeepsWhenAboveThreshold()
    {
        var options = new SamplingOptions
        {
            Keep = [new() { Duration = 1000 }]
        };

        Assert.True(Sampling.ShouldKeep(null, 1500, null, options));
        Assert.True(Sampling.ShouldKeep(null, 1000, null, options));
        Assert.False(Sampling.ShouldKeep(null, 500, null, options));
    }

    [Fact]
    public void ShouldKeep_PathCondition_KeepsOnGlobMatch()
    {
        var options = new SamplingOptions
        {
            Keep = [new() { Path = "/api/critical/**" }]
        };

        Assert.True(Sampling.ShouldKeep(null, null, "/api/critical/orders", options));
        Assert.False(Sampling.ShouldKeep(null, null, "/api/orders", options));
    }

    [Fact]
    public void ShouldKeep_MultipleConditions_OrLogic()
    {
        var options = new SamplingOptions
        {
            Keep =
            [
                new() { Status = 400 },
                new() { Duration = 1000 },
            ]
        };

        // Status matches
        Assert.True(Sampling.ShouldKeep(500, 100, null, options));
        // Duration matches
        Assert.True(Sampling.ShouldKeep(200, 2000, null, options));
        // Neither matches
        Assert.False(Sampling.ShouldKeep(200, 100, null, options));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "GlobMatcherTests|TailSamplingTests" -v q`
Expected: FAIL (GlobMatcher doesn't exist)

**Step 3: Write implementation**

```csharp
// src/Evlog/GlobMatcher.cs
namespace Evlog;

/// <summary>
/// Minimal glob matcher supporting * (any chars except /) and ** (any chars including /).
/// Zero-allocation: operates on ReadOnlySpan&lt;char&gt;.
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(ReadOnlySpan<char> path, ReadOnlySpan<char> pattern)
    {
        int pi = 0, gi = 0;
        int starPi = -1, starGi = -1;

        while (pi < path.Length)
        {
            if (gi < pattern.Length - 1
                && pattern[gi] == '*' && pattern[gi + 1] == '*')
            {
                // ** matches everything including /
                starPi = pi;
                starGi = gi;
                gi += 2;
                // Skip trailing / after **
                if (gi < pattern.Length && pattern[gi] == '/')
                    gi++;
            }
            else if (gi < pattern.Length && pattern[gi] == '*')
            {
                // * matches everything except /
                starPi = pi;
                starGi = gi;
                gi++;
            }
            else if (gi < pattern.Length && pattern[gi] == path[pi])
            {
                pi++;
                gi++;
            }
            else if (starGi >= 0)
            {
                // Backtrack: advance the star match by one character
                bool isDoubleStar = starGi < pattern.Length - 1
                    && pattern[starGi] == '*' && pattern[starGi + 1] == '*';

                if (!isDoubleStar && path[starPi] == '/')
                {
                    // Single star cannot match /
                    return false;
                }

                starPi++;
                pi = starPi;
                gi = starGi;

                // Re-consume the star
                if (isDoubleStar)
                {
                    gi += 2;
                    if (gi < pattern.Length && pattern[gi] == '/')
                        gi++;
                }
                else
                {
                    gi++;
                }
            }
            else
            {
                return false;
            }
        }

        // Consume trailing stars/wildcards in pattern
        while (gi < pattern.Length)
        {
            if (gi < pattern.Length - 1 && pattern[gi] == '*' && pattern[gi + 1] == '*')
                gi += 2;
            else if (pattern[gi] == '*')
                gi++;
            else if (pattern[gi] == '/')
                gi++;
            else
                break;
        }

        return gi == pattern.Length;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "GlobMatcherTests|TailSamplingTests" -v q`
Expected: PASS (all tests)

**Step 5: Commit**

```bash
git add src/Evlog/GlobMatcher.cs tests/Evlog.Tests/GlobMatcherTests.cs tests/Evlog.Tests/TailSamplingTests.cs
git commit -m "feat: add tail sampling with status, duration, and glob path conditions"
```

---

## Task 7: WideEventWriter — JSON Output from Entries

This is the core emit-time component that reads the flat `ContextEntry` list and writes nested JSON via `Utf8JsonWriter`.

**Files:**
- Create: `src/Evlog/WideEventWriter.cs`
- Create: `tests/Evlog.Tests/WideEventWriterTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/WideEventWriterTests.cs
using System.Buffers;
using System.Text;
using System.Text.Json;
using Evlog;

namespace Evlog.Tests;

public class WideEventWriterTests
{
    private static JsonDocument WriteAndParse(RequestLogger logger, int status = 200)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, status, durationMs: 125.0);
        return JsonDocument.Parse(buffer.WrittenSpan);
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
        Assert.True(error.TryGetProperty("stack", out _));
    }

    [Fact]
    public void Write_LevelDerivedFromStatus()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        // Status < 400 → info
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
        using var doc = JsonDocument.Parse(buffer.WrittenSpan);

        Assert.Equal("1.25s", doc.RootElement.GetProperty("duration").GetString());
    }

    [Fact]
    public void Write_DurationMs_UnderOneSecond()
    {
        var logger = new RequestLogger();
        logger.Activate("svc", "prod");

        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, 200, durationMs: 125.0);
        using var doc = JsonDocument.Parse(buffer.WrittenSpan);

        Assert.Equal("125ms", doc.RootElement.GetProperty("duration").GetString());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "WideEventWriterTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/WideEventWriter.cs
using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Evlog;

/// <summary>
/// Writes a wide event as JSON from a RequestLogger's accumulated state.
/// Uses Utf8JsonWriter for zero-alloc JSON output.
/// All property names are pre-encoded as JsonEncodedText.
/// </summary>
public static class WideEventWriter
{
    // Pre-encoded property names
    private static readonly JsonEncodedText PropTimestamp = JsonEncodedText.Encode("timestamp");
    private static readonly JsonEncodedText PropLevel = JsonEncodedText.Encode("level");
    private static readonly JsonEncodedText PropService = JsonEncodedText.Encode("service");
    private static readonly JsonEncodedText PropEnvironment = JsonEncodedText.Encode("environment");
    private static readonly JsonEncodedText PropVersion = JsonEncodedText.Encode("version");
    private static readonly JsonEncodedText PropCommitHash = JsonEncodedText.Encode("commitHash");
    private static readonly JsonEncodedText PropRegion = JsonEncodedText.Encode("region");
    private static readonly JsonEncodedText PropMethod = JsonEncodedText.Encode("method");
    private static readonly JsonEncodedText PropPath = JsonEncodedText.Encode("path");
    private static readonly JsonEncodedText PropRequestId = JsonEncodedText.Encode("requestId");
    private static readonly JsonEncodedText PropStatus = JsonEncodedText.Encode("status");
    private static readonly JsonEncodedText PropDuration = JsonEncodedText.Encode("duration");
    private static readonly JsonEncodedText PropError = JsonEncodedText.Encode("error");
    private static readonly JsonEncodedText PropErrorName = JsonEncodedText.Encode("name");
    private static readonly JsonEncodedText PropErrorMessage = JsonEncodedText.Encode("message");
    private static readonly JsonEncodedText PropErrorStack = JsonEncodedText.Encode("stack");
    private static readonly JsonEncodedText PropRequestLogs = JsonEncodedText.Encode("requestLogs");
    private static readonly JsonEncodedText PropMessage = JsonEncodedText.Encode("message");

    private static readonly JsonEncodedText LevelInfo = JsonEncodedText.Encode("info");
    private static readonly JsonEncodedText LevelWarn = JsonEncodedText.Encode("warn");
    private static readonly JsonEncodedText LevelError = JsonEncodedText.Encode("error");
    private static readonly JsonEncodedText LevelDebug = JsonEncodedText.Encode("debug");

    public static void Write(
        RequestLogger logger,
        IBufferWriter<byte> output,
        int status,
        double durationMs,
        bool forceKeep = false)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            SkipValidation = false,
        });

        var level = DetermineLevel(status, logger.GetError());

        writer.WriteStartObject();

        // Envelope
        writer.WriteString(PropTimestamp, DateTimeOffset.UtcNow);
        writer.WriteString(PropLevel, GetLevelText(level));
        writer.WriteString(PropService, logger.Service);
        writer.WriteString(PropEnvironment, logger.Environment);

        if (logger.Version is not null)
            writer.WriteString(PropVersion, logger.Version);
        if (logger.CommitHash is not null)
            writer.WriteString(PropCommitHash, logger.CommitHash);
        if (logger.Region is not null)
            writer.WriteString(PropRegion, logger.Region);

        // Request context
        if (logger.Method is not null)
            writer.WriteString(PropMethod, logger.Method);
        if (logger.Path is not null)
            writer.WriteString(PropPath, logger.Path);
        if (logger.RequestId is not null)
            writer.WriteString(PropRequestId, logger.RequestId);

        writer.WriteNumber(PropStatus, status);
        writer.WriteString(PropDuration, FormatDuration(durationMs));

        // User-defined fields (expand dot-notation to nested JSON)
        WriteContextEntries(writer, logger);

        // Error
        if (logger.GetError() is { } error)
        {
            WriteError(writer, error);
        }

        // Request logs
        var requestLogs = logger.GetRequestLogs();
        if (requestLogs.Count > 0)
        {
            WriteRequestLogs(writer, requestLogs);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteContextEntries(Utf8JsonWriter writer, RequestLogger logger)
    {
        var entries = logger.GetEntries();
        if (entries.Count == 0) return;

        var fragmentBuffer = logger.GetJsonFragmentBuffer();

        // First pass: write root-level merge fragments (from Set(object))
        // These have empty key and their JSON properties get merged at the top level
        foreach (var entry in entries)
        {
            if (entry.Kind == ContextValueKind.JsonFragment && entry.Key.Length == 0)
            {
                var fragment = fragmentBuffer.Slice(entry.FragmentOffset, entry.FragmentLength);
                using var doc = JsonDocument.Parse(fragment.ToArray());
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
        }

        // Second pass: group typed entries by top-level key and track first-write-wins
        var written = new HashSet<string>();
        var groups = new Dictionary<string, List<(string subKey, ContextEntry entry)>>();

        foreach (var entry in entries)
        {
            // Skip root-level merge fragments (already handled above)
            if (entry.Kind == ContextValueKind.JsonFragment && entry.Key.Length == 0)
                continue;

            int dotIndex = entry.Key.IndexOf('.');
            if (dotIndex < 0)
            {
                // Flat key — write directly (first-write-wins)
                if (written.Add(entry.Key))
                {
                    WriteEntryValue(writer, entry.Key, entry, fragmentBuffer);
                }
            }
            else
            {
                // Dotted key — group by top-level
                string topKey = entry.Key[..dotIndex];
                string subKey = entry.Key[(dotIndex + 1)..];

                if (!groups.TryGetValue(topKey, out var group))
                {
                    group = new List<(string, ContextEntry)>();
                    groups[topKey] = group;
                }
                group.Add((subKey, entry));
            }
        }

        // Write grouped nested objects
        foreach (var (topKey, group) in groups)
        {
            if (!written.Add(topKey))
                continue; // Top-level key already written as flat

            writer.WritePropertyName(topKey);
            writer.WriteStartObject();

            var subWritten = new HashSet<string>();
            foreach (var (subKey, entry) in group)
            {
                if (subWritten.Add(subKey)) // First-write-wins
                {
                    WriteEntryValue(writer, subKey, entry, fragmentBuffer);
                }
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteEntryValue(
        Utf8JsonWriter writer,
        string propertyName,
        ContextEntry entry,
        ReadOnlySpan<byte> fragmentBuffer)
    {
        switch (entry.Kind)
        {
            case ContextValueKind.String:
                writer.WriteString(propertyName, entry.StringValue);
                break;
            case ContextValueKind.Int:
                writer.WriteNumber(propertyName, entry.IntValue);
                break;
            case ContextValueKind.Long:
                writer.WriteNumber(propertyName, entry.LongValue);
                break;
            case ContextValueKind.Double:
                writer.WriteNumber(propertyName, entry.DoubleValue);
                break;
            case ContextValueKind.Bool:
                writer.WriteBoolean(propertyName, entry.BoolValue);
                break;
            case ContextValueKind.JsonFragment:
                var fragment = fragmentBuffer.Slice(entry.FragmentOffset, entry.FragmentLength);
                writer.WritePropertyName(propertyName);
                writer.WriteRawValue(fragment);
                break;
        }
    }

    private static void WriteError(Utf8JsonWriter writer, Exception error)
    {
        writer.WritePropertyName(PropError);
        writer.WriteStartObject();
        writer.WriteString(PropErrorName, error.GetType().Name);
        writer.WriteString(PropErrorMessage, error.Message);
        if (error.StackTrace is not null)
            writer.WriteString(PropErrorStack, error.StackTrace);

        if (error is EvlogError evlogError)
        {
            if (evlogError.Why is not null)
                writer.WriteString("why", evlogError.Why);
            if (evlogError.Fix is not null)
                writer.WriteString("fix", evlogError.Fix);
            if (evlogError.Link is not null)
                writer.WriteString("link", evlogError.Link);
            writer.WriteNumber("status", evlogError.Status);
        }

        writer.WriteEndObject();
    }

    private static void WriteRequestLogs(Utf8JsonWriter writer, IReadOnlyList<RequestLogEntry> logs)
    {
        writer.WritePropertyName(PropRequestLogs);
        writer.WriteStartArray();

        foreach (var log in logs)
        {
            writer.WriteStartObject();
            writer.WriteString(PropLevel, GetLevelText(log.Level));
            writer.WriteString(PropMessage, log.Message);
            writer.WriteString(PropTimestamp, log.Timestamp);
            if (log.Category is not null)
                writer.WriteString("category", log.Category);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static EvlogLevel DetermineLevel(int status, Exception? error)
    {
        if (error is not null || status >= 500) return EvlogLevel.Error;
        if (status >= 400) return EvlogLevel.Warn;
        return EvlogLevel.Info;
    }

    internal static JsonEncodedText GetLevelText(EvlogLevel level) => level switch
    {
        EvlogLevel.Info => LevelInfo,
        EvlogLevel.Warn => LevelWarn,
        EvlogLevel.Error => LevelError,
        EvlogLevel.Debug => LevelDebug,
        _ => LevelInfo,
    };

    internal static string FormatDuration(double ms)
    {
        if (ms < 1000)
            return $"{(int)ms}ms";
        return $"{(ms / 1000.0).ToString("F2", CultureInfo.InvariantCulture)}s";
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "WideEventWriterTests" -v q`
Expected: PASS (10 tests)

Note: This task requires `EvlogError` which is built in Task 8. You may need to create a minimal stub or implement Tasks 7 and 8 together. If `EvlogError` doesn't exist yet, the error-related tests will fail. Either create a stub `EvlogError` class or reorder to build Task 8 first.

**Step 5: Commit**

```bash
git add src/Evlog/WideEventWriter.cs tests/Evlog.Tests/WideEventWriterTests.cs
git commit -m "feat: add WideEventWriter with dot-notation expansion and pre-encoded JSON properties"
```

---

## Task 8: EvlogError + ProblemDetails Mapping

**Files:**
- Create: `src/Evlog/EvlogError.cs`
- Create: `tests/Evlog.Tests/EvlogErrorTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/EvlogErrorTests.cs
using Evlog;
using Microsoft.AspNetCore.Mvc;

namespace Evlog.Tests;

public class EvlogErrorTests
{
    [Fact]
    public void Create_SetsAllProperties()
    {
        var error = EvlogError.Create(
            message: "Payment failed",
            status: 402,
            why: "Card declined",
            fix: "Try another card",
            link: "https://docs.example.com/errors/payment");

        Assert.Equal("Payment failed", error.Message);
        Assert.Equal(402, error.Status);
        Assert.Equal("Card declined", error.Why);
        Assert.Equal("Try another card", error.Fix);
        Assert.Equal("https://docs.example.com/errors/payment", error.Link);
    }

    [Fact]
    public void Create_DefaultsStatusTo500()
    {
        var error = EvlogError.Create("Internal error");

        Assert.Equal(500, error.Status);
    }

    [Fact]
    public void Create_WithCause_SetsInnerException()
    {
        var cause = new InvalidOperationException("original");
        var error = EvlogError.Create("Wrapped error", cause: cause);

        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public void Create_IsException()
    {
        var error = EvlogError.Create("test");

        Assert.IsAssignableFrom<Exception>(error);
    }

    [Fact]
    public void ToProblemDetails_MapsCorrectly()
    {
        var error = EvlogError.Create(
            message: "Payment failed",
            status: 402,
            why: "Card declined",
            fix: "Try another card",
            link: "https://docs.example.com/errors/payment");

        var problem = error.ToProblemDetails();

        Assert.Equal("Payment failed", problem.Title);
        Assert.Equal(402, problem.Status);
        Assert.Equal("Card declined", problem.Detail);
        Assert.Equal("https://docs.example.com/errors/payment", problem.Type);
        Assert.Equal("Try another card", problem.Extensions["fix"]?.ToString());
    }

    [Fact]
    public void ToProblemDetails_WithNulls_OmitsOptionalFields()
    {
        var error = EvlogError.Create("Simple error", status: 400);

        var problem = error.ToProblemDetails();

        Assert.Equal("Simple error", problem.Title);
        Assert.Equal(400, problem.Status);
        Assert.Null(problem.Detail);
        Assert.Null(problem.Type);
        Assert.False(problem.Extensions.ContainsKey("fix"));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogErrorTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/EvlogError.cs
using Microsoft.AspNetCore.Mvc;

namespace Evlog;

public sealed class EvlogError : Exception
{
    public int Status { get; init; } = 500;
    public string? Why { get; init; }
    public string? Fix { get; init; }
    public string? Link { get; init; }

    private EvlogError(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public static EvlogError Create(
        string message,
        int status = 500,
        string? why = null,
        string? fix = null,
        string? link = null,
        Exception? cause = null)
    {
        return new EvlogError(message, cause)
        {
            Status = status,
            Why = why,
            Fix = fix,
            Link = link,
        };
    }

    public ProblemDetails ToProblemDetails()
    {
        var problem = new ProblemDetails
        {
            Title = Message,
            Status = Status,
            Detail = Why,
            Type = Link,
        };

        if (Fix is not null)
            problem.Extensions["fix"] = Fix;

        return problem;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogErrorTests" -v q`
Expected: PASS (6 tests)

**Step 5: Commit**

```bash
git add src/Evlog/EvlogError.cs tests/Evlog.Tests/EvlogErrorTests.cs
git commit -m "feat: add EvlogError with Why/Fix/Link and ProblemDetails mapping"
```

---

## Task 9: EvlogOptions + Configuration

**Files:**
- Create: `src/Evlog/EvlogOptions.cs`
- Create: `tests/Evlog.Tests/EvlogOptionsTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/EvlogOptionsTests.cs
using Evlog;

namespace Evlog.Tests;

public class EvlogOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var options = new EvlogOptions();

        Assert.Equal("app", options.Service);
        Assert.Equal("production", options.Environment);
        Assert.False(options.Pretty);
        Assert.Null(options.Version);
        Assert.Null(options.Sampling);
        Assert.Null(options.Drain);
    }

    [Fact]
    public void ResolveFromEnvironment_ReadsEnvVars()
    {
        System.Environment.SetEnvironmentVariable("SERVICE_NAME", "test-svc");
        System.Environment.SetEnvironmentVariable("APP_VERSION", "2.0.0");
        System.Environment.SetEnvironmentVariable("COMMIT_SHA", "abc123");
        System.Environment.SetEnvironmentVariable("FLY_REGION", "iad");

        try
        {
            var options = new EvlogOptions();
            options.ResolveFromEnvironment();

            Assert.Equal("test-svc", options.Service);
            Assert.Equal("2.0.0", options.Version);
            Assert.Equal("abc123", options.CommitHash);
            Assert.Equal("iad", options.Region);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SERVICE_NAME", null);
            System.Environment.SetEnvironmentVariable("APP_VERSION", null);
            System.Environment.SetEnvironmentVariable("COMMIT_SHA", null);
            System.Environment.SetEnvironmentVariable("FLY_REGION", null);
        }
    }

    [Fact]
    public void ResolveFromEnvironment_DoesNotOverrideExplicitValues()
    {
        System.Environment.SetEnvironmentVariable("SERVICE_NAME", "env-svc");

        try
        {
            var options = new EvlogOptions { Service = "explicit-svc" };
            options.ResolveFromEnvironment();

            Assert.Equal("explicit-svc", options.Service);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SERVICE_NAME", null);
        }
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogOptionsTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/EvlogOptions.cs
namespace Evlog;

public delegate Task EvlogDrainDelegate(EvlogDrainContext context);

public sealed class EvlogDrainContext
{
    public required ReadOnlyMemory<byte> EventJson { get; init; }
    public required EvlogLevel Level { get; init; }
    public required int Status { get; init; }
}

public sealed class EvlogOptions
{
    private const string DefaultService = "app";
    private const string DefaultEnvironment = "production";
    private bool _serviceExplicit;
    private bool _versionExplicit;
    private bool _commitHashExplicit;
    private bool _regionExplicit;

    private string _service = DefaultService;

    public string Service
    {
        get => _service;
        set { _service = value; _serviceExplicit = true; }
    }

    public string Environment { get; set; } = DefaultEnvironment;
    public bool Pretty { get; set; }

    private string? _version;
    public string? Version
    {
        get => _version;
        set { _version = value; _versionExplicit = true; }
    }

    private string? _commitHash;
    public string? CommitHash
    {
        get => _commitHash;
        set { _commitHash = value; _commitHashExplicit = true; }
    }

    private string? _region;
    public string? Region
    {
        get => _region;
        set { _region = value; _regionExplicit = true; }
    }

    public SamplingOptions? Sampling { get; set; }
    public EvlogDrainDelegate? Drain { get; set; }

    /// <summary>
    /// Fill in values from environment variables, without overriding explicitly set values.
    /// </summary>
    public void ResolveFromEnvironment()
    {
        if (!_serviceExplicit)
            _service = Env("SERVICE_NAME") ?? DefaultService;

        var env = Env("ASPNETCORE_ENVIRONMENT") ?? Env("DOTNET_ENVIRONMENT");
        if (env is not null && Environment == DefaultEnvironment)
            Environment = env;

        if (!_versionExplicit)
            _version = Env("APP_VERSION");

        if (!_commitHashExplicit)
            _commitHash = Env("COMMIT_SHA") ?? Env("GITHUB_SHA");

        if (!_regionExplicit)
            _region = Env("REGION") ?? Env("FLY_REGION") ?? Env("AWS_REGION");
    }

    private static string? Env(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogOptionsTests" -v q`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/Evlog/EvlogOptions.cs tests/Evlog.Tests/EvlogOptionsTests.cs
git commit -m "feat: add EvlogOptions with environment variable auto-detection"
```

---

## Task 10: PrettyOutputFormatter

**Files:**
- Create: `src/Evlog/PrettyOutputFormatter.cs`
- Create: `tests/Evlog.Tests/PrettyOutputFormatterTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/PrettyOutputFormatterTests.cs
using Evlog;

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

        Assert.Contains("├─", output);
        Assert.Contains("└─", output);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "PrettyOutputFormatterTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/PrettyOutputFormatter.cs
using System.Text;

namespace Evlog;

/// <summary>
/// Formats a wide event as colored, human-readable console output.
/// </summary>
public static class PrettyOutputFormatter
{
    private const string Reset = "\x1B[0m";
    private const string Dim = "\x1B[2m";
    private const string Red = "\x1B[31m";
    private const string Green = "\x1B[32m";
    private const string Yellow = "\x1B[33m";
    private const string Cyan = "\x1B[36m";
    private const string Gray = "\x1B[90m";

    public static string Format(RequestLogger logger, int status, double durationMs)
    {
        var sb = new StringBuilder(256);

        var level = WideEventWriter.DetermineLevel(status, logger.GetError());
        var levelColor = GetLevelColor(level);
        var duration = WideEventWriter.FormatDuration(durationMs);

        // Header line: METHOD /path STATUS duration
        sb.Append(' ');

        if (logger.Method is not null)
        {
            sb.Append(logger.Method).Append(' ');
        }
        if (logger.Path is not null)
        {
            sb.Append(logger.Path).Append(' ');
        }

        var statusColor = status >= 400 ? Red : Green;
        sb.Append(statusColor).Append(status).Append(Reset).Append(' ');
        sb.Append(Dim).Append(duration).Append(Reset);
        sb.AppendLine();

        // Collect all lines to display
        var lines = new List<(string key, string value)>();

        // Context entries
        foreach (var entry in logger.GetEntries())
        {
            lines.Add((entry.Key, FormatEntryValue(entry)));
        }

        // Request logs
        foreach (var log in logger.GetRequestLogs())
        {
            var logLevelColor = GetLevelColor(log.Level);
            lines.Add((log.Level.ToString().ToLowerInvariant(), log.Message));
        }

        // Service
        lines.Add(("service", logger.Service));

        // Write tree
        for (int i = 0; i < lines.Count; i++)
        {
            bool isLast = i == lines.Count - 1;
            string prefix = isLast ? "└─" : "├─";
            sb.Append("  ")
              .Append(Dim).Append(prefix).Append(Reset)
              .Append(' ')
              .Append(Cyan).Append(lines[i].key).Append(':').Append(Reset)
              .Append(' ')
              .Append(lines[i].value);

            if (!isLast) sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GetLevelColor(EvlogLevel level) => level switch
    {
        EvlogLevel.Error => Red,
        EvlogLevel.Warn => Yellow,
        EvlogLevel.Info => Cyan,
        EvlogLevel.Debug => Gray,
        _ => Cyan,
    };

    private static string FormatEntryValue(ContextEntry entry) => entry.Kind switch
    {
        ContextValueKind.String => entry.StringValue ?? "null",
        ContextValueKind.Int => entry.IntValue.ToString(),
        ContextValueKind.Long => entry.LongValue.ToString(),
        ContextValueKind.Double => entry.DoubleValue.ToString(),
        ContextValueKind.Bool => entry.BoolValue ? "true" : "false",
        ContextValueKind.JsonFragment => "{...}",
        _ => "?",
    };
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "PrettyOutputFormatterTests" -v q`
Expected: PASS (5 tests)

**Step 5: Commit**

```bash
git add src/Evlog/PrettyOutputFormatter.cs tests/Evlog.Tests/PrettyOutputFormatterTests.cs
git commit -m "feat: add PrettyOutputFormatter with ANSI colors and tree structure"
```

---

## Task 11: EvlogMiddleware

**Files:**
- Create: `src/Evlog/EvlogMiddleware.cs`
- Create: `src/Evlog/HttpContextExtensions.cs`
- Create: `tests/Evlog.Tests/EvlogMiddlewareTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/EvlogMiddlewareTests.cs
using System.Buffers;
using System.Net;
using System.Text.Json;
using Evlog;
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
        // Simulate accessing logger outside middleware
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
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogMiddlewareTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

```csharp
// src/Evlog/HttpContextExtensions.cs
using Microsoft.AspNetCore.Http;

namespace Evlog;

public static class HttpContextExtensions
{
    private const string LoggerKey = "__evlog_logger";

    public static RequestLogger GetEvlogLogger(this HttpContext context)
    {
        if (context.Items.TryGetValue(LoggerKey, out var obj) && obj is RequestLogger logger)
            return logger;

        // Return a static inactive logger — all methods are no-ops
        return InactiveLogger.Instance;
    }

    internal static void SetEvlogLogger(this HttpContext context, RequestLogger logger)
    {
        context.Items[LoggerKey] = logger;
    }

    /// <summary>
    /// Singleton inactive logger for when no middleware is active.
    /// All Set/Info/Warn/Error calls are no-ops since _active is false.
    /// </summary>
    private static class InactiveLogger
    {
        internal static readonly RequestLogger Instance = new();
    }
}
```

```csharp
// src/Evlog/EvlogMiddleware.cs
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Evlog;

public sealed class EvlogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EvlogOptions _options;
    private readonly ObjectPool<RequestLogger> _loggerPool;

    public EvlogMiddleware(
        RequestDelegate next,
        IOptions<EvlogOptions> options,
        ObjectPool<RequestLogger> loggerPool)
    {
        _next = next;
        _options = options.Value;
        _loggerPool = loggerPool;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var logger = _loggerPool.Get();
        try
        {
            logger.Activate(
                _options.Service,
                _options.Environment,
                _options.Version,
                _options.CommitHash,
                _options.Region);

            var request = context.Request;
            var requestId = request.Headers["x-request-id"].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N")[..16];
            logger.SetRequest(request.Method, request.Path.Value ?? "/", requestId);

            context.SetEvlogLogger(logger);

            var startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                HandleException(context, logger, ex);
            }

            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var status = context.Response.StatusCode;
            var durationMs = elapsed.TotalMilliseconds;

            // Tail sampling check
            bool forceKeep = _options.Sampling is not null
                && Sampling.ShouldKeep(status, durationMs, logger.Path, _options.Sampling);

            EmitAndDrain(logger, status, durationMs, forceKeep);
        }
        finally
        {
            _loggerPool.Return(logger);
        }
    }

    private void HandleException(HttpContext context, RequestLogger logger, Exception ex)
    {
        logger.Error(ex);

        if (ex is EvlogError evlogError)
        {
            context.Response.StatusCode = evlogError.Status;
            context.Response.ContentType = "application/problem+json";

            var problem = evlogError.ToProblemDetails();
            var json = JsonSerializer.SerializeToUtf8Bytes(problem);
            context.Response.Body.WriteAsync(json);
        }
        else
        {
            context.Response.StatusCode = 500;
        }
    }

    private void EmitAndDrain(RequestLogger logger, int status, double durationMs, bool forceKeep)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, status, durationMs, forceKeep);

        if (_options.Pretty)
        {
            var pretty = PrettyOutputFormatter.Format(logger, status, durationMs);
            Console.Out.WriteLine(pretty);
        }
        else
        {
            Console.Out.Write(buffer.WrittenSpan);
            Console.Out.Write("\n"u8);
        }

        if (_options.Drain is not null)
        {
            var level = WideEventWriter.DetermineLevel(status, logger.GetError());
            var drainContext = new EvlogDrainContext
            {
                EventJson = buffer.WrittenMemory,
                Level = level,
                Status = status,
            };

            // Fire-and-forget
            _ = DrainSafe(_options.Drain, drainContext);
        }
    }

    private static async Task DrainSafe(EvlogDrainDelegate drain, EvlogDrainContext context)
    {
        try
        {
            await drain(context);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[evlog] drain error: {ex.Message}");
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogMiddlewareTests" -v q`
Expected: PASS (5 tests)

Note: The `AddEvlog`/`UseEvlog` extension methods are needed. Implement Task 12 first or create them as part of this task. See Task 12 for the service registration code.

**Step 5: Commit**

```bash
git add src/Evlog/EvlogMiddleware.cs src/Evlog/HttpContextExtensions.cs tests/Evlog.Tests/EvlogMiddlewareTests.cs
git commit -m "feat: add EvlogMiddleware with object pooling, ProblemDetails, and fire-and-forget drain"
```

---

## Task 12: Service Registration (AddEvlog / UseEvlog)

**Files:**
- Create: `src/Evlog/EvlogServiceExtensions.cs`

**Step 1: Write implementation** (tested via middleware tests in Task 11)

```csharp
// src/Evlog/EvlogServiceExtensions.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ObjectPool;

namespace Evlog;

public static class EvlogServiceExtensions
{
    public static IServiceCollection AddEvlog(
        this IServiceCollection services,
        Action<EvlogOptions> configure)
    {
        services.Configure(configure);

        services.TryAddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        services.TryAddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<ObjectPoolProvider>();
            return provider.Create(new DefaultPooledObjectPolicy<RequestLogger>());
        });

        return services;
    }

    public static IApplicationBuilder UseEvlog(this IApplicationBuilder app)
    {
        return app.UseMiddleware<EvlogMiddleware>();
    }
}
```

**Step 2: Verify middleware tests still pass**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogMiddlewareTests" -v q`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Evlog/EvlogServiceExtensions.cs
git commit -m "feat: add AddEvlog/UseEvlog service registration with ObjectPool"
```

---

## Task 13: ILoggerProvider Bridge

**Files:**
- Create: `src/Evlog/EvlogLoggerProvider.cs`
- Create: `src/Evlog/EvlogLogger.cs`
- Create: `tests/Evlog.Tests/EvlogLoggerProviderTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Evlog.Tests/EvlogLoggerProviderTests.cs
using Evlog;
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
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogLoggerProviderTests" -v q`
Expected: FAIL

**Step 3: Write implementation**

Note: `HttpContextExtensions.SetEvlogLogger` needs to be `public` or `internal` accessible from tests. Since the test project references the main project, `internal` with `InternalsVisibleTo` works:

Add to `src/Evlog/Evlog.csproj`:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="Evlog.Tests" />
</ItemGroup>
```

```csharp
// src/Evlog/EvlogLoggerProvider.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Evlog;

[ProviderAlias("Evlog")]
public sealed class EvlogLoggerProvider : ILoggerProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EvlogLoggerProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new EvlogLogger(categoryName, _httpContextAccessor);
    }

    public void Dispose() { }
}
```

```csharp
// src/Evlog/EvlogLogger.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Evlog;

internal sealed class EvlogLogger : ILogger
{
    private readonly string _category;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EvlogLogger(string category, IHttpContextAccessor httpContextAccessor)
    {
        _category = category;
        _httpContextAccessor = httpContextAccessor;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null) return;

        var requestLogger = httpContext.GetEvlogLogger();
        if (!requestLogger.IsActive) return;

        var message = formatter(state, exception);
        var evlogLevel = MapLevel(logLevel);

        switch (evlogLevel)
        {
            case EvlogLevel.Error:
                if (exception is not null)
                    requestLogger.Error(exception, message);
                else
                    requestLogger.Error(new Exception(message), message);
                break;
            case EvlogLevel.Warn:
                requestLogger.Warn(message);
                break;
            case EvlogLevel.Debug:
                // Add as request log with debug level
                requestLogger.Info(message); // RequestLogger doesn't have Debug — use Info for now
                break;
            default:
                requestLogger.Info(message);
                break;
        }

        // Override the log entry's category for the most recent entry
        // (RequestLogger stores entries as RequestLogEntry with Category)
        // This is handled by adding a category-aware overload — see note below.
    }

    private static EvlogLevel MapLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Critical or LogLevel.Error => EvlogLevel.Error,
        LogLevel.Warning => EvlogLevel.Warn,
        LogLevel.Debug or LogLevel.Trace => EvlogLevel.Debug,
        _ => EvlogLevel.Info,
    };
}
```

**Important note:** To support the `Category` field on `RequestLogEntry`, the `RequestLogger` needs category-aware log methods. Add these overloads to `RequestLogger`:

```csharp
// Add to RequestLogger.cs
public void Info(string message, string? category)
{
    if (!_active) return;
    _requestLogs.Add(new RequestLogEntry
    {
        Level = EvlogLevel.Info,
        Message = message,
        Timestamp = DateTimeOffset.UtcNow,
        Category = category,
    });
}

public void Warn(string message, string? category)
{
    if (!_active) return;
    _requestLogs.Add(new RequestLogEntry
    {
        Level = EvlogLevel.Warn,
        Message = message,
        Timestamp = DateTimeOffset.UtcNow,
        Category = category,
    });
}
```

Then update `EvlogLogger.Log()` to pass `_category` to the category-aware overloads.

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Evlog.Tests --filter "EvlogLoggerProviderTests" -v q`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/Evlog/EvlogLoggerProvider.cs src/Evlog/EvlogLogger.cs src/Evlog/Evlog.csproj tests/Evlog.Tests/EvlogLoggerProviderTests.cs
git commit -m "feat: add ILoggerProvider bridge to capture ILogger calls into wide events"
```

---

## Task 14: Wire ILoggerProvider into AddEvlog + Update Service Registration

**Files:**
- Modify: `src/Evlog/EvlogServiceExtensions.cs`

**Step 1: Update AddEvlog to register ILoggerProvider and IHttpContextAccessor**

```csharp
// Update EvlogServiceExtensions.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Evlog;

public static class EvlogServiceExtensions
{
    public static IServiceCollection AddEvlog(
        this IServiceCollection services,
        Action<EvlogOptions> configure)
    {
        services.Configure(configure);

        services.TryAddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        services.TryAddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<ObjectPoolProvider>();
            return provider.Create(new DefaultPooledObjectPolicy<RequestLogger>());
        });

        services.AddHttpContextAccessor();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, EvlogLoggerProvider>());

        return services;
    }

    public static IApplicationBuilder UseEvlog(this IApplicationBuilder app)
    {
        return app.UseMiddleware<EvlogMiddleware>();
    }
}
```

**Step 2: Run all tests**

Run: `dotnet test tests/Evlog.Tests -v q`
Expected: ALL PASS

**Step 3: Commit**

```bash
git add src/Evlog/EvlogServiceExtensions.cs
git commit -m "feat: wire ILoggerProvider and IHttpContextAccessor into service registration"
```

---

## Task 15: Integration Test — Full Request Pipeline

**Files:**
- Create: `tests/Evlog.Tests/IntegrationTests.cs`

**Step 1: Write the integration tests**

```csharp
// tests/Evlog.Tests/IntegrationTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evlog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
                    services.AddRouting();
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

        // Request logs
        var logs = root.GetProperty("requestLogs");
        Assert.Equal(1, logs.GetArrayLength());
        Assert.Equal("User fetched successfully", logs[0].GetProperty("message").GetString());
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
```

**Step 2: Run integration tests**

Run: `dotnet test tests/Evlog.Tests --filter "IntegrationTests" -v q`
Expected: PASS (4 tests)

**Step 3: Run ALL tests**

Run: `dotnet test tests/Evlog.Tests -v q`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add tests/Evlog.Tests/IntegrationTests.cs
git commit -m "test: add full pipeline integration tests"
```

---

## Task 16: Final Cleanup + Verify All Tests

**Step 1: Run full test suite**

Run: `dotnet test Evlog.sln -v n`
Expected: ALL PASS

**Step 2: Verify build with no warnings**

Run: `dotnet build Evlog.sln -warnaserror`
Expected: Build succeeded, 0 warnings

**Step 3: Final commit**

```bash
git add -A
git commit -m "chore: final cleanup and verify all tests pass"
```

---

## Dependency Graph

```
Task 1 (scaffold)
  ├── Task 2 (ContextEntry)
  │     └── Task 3 (RequestLogger Set)
  │           ├── Task 4 (RequestLogger Info/Warn/Error)
  │           └── Task 7 (WideEventWriter) ← depends on Task 8
  ├── Task 5 (Head Sampling)
  ├── Task 6 (Tail Sampling + GlobMatcher)
  ├── Task 8 (EvlogError)
  ├── Task 9 (EvlogOptions)
  └── Task 10 (PrettyOutputFormatter) ← depends on Task 7

Task 11 (Middleware) ← depends on Tasks 3,7,8,9,10,12
Task 12 (Service Registration) ← depends on Task 11
Task 13 (ILoggerProvider) ← depends on Task 3
Task 14 (Wire ILoggerProvider) ← depends on Tasks 12,13
Task 15 (Integration Tests) ← depends on all above
Task 16 (Cleanup) ← depends on Task 15
```
