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

    private Exception? _error;
    private string? _errorMessage;

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
        _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
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
    public string? RequestId => _requestId;
    public long StartTimestamp => _startTimestamp;
    public string Service => _service;
    public string Environment => _environment;
    public string? Version => _version;
    public string? CommitHash => _commitHash;
    public string? Region => _region;

    // --- Ergonomic API: anonymous/typed objects ---

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
        _entries.Add(ContextEntry.JsonFragment("", offset, length));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // --- Zero-alloc typed overloads ---

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string message)
    {
        if (!_active) return;
        _requestLogs.Add(new RequestLogEntry
        {
            Level = EvlogLevel.Debug,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string message, string? category)
    {
        if (!_active) return;
        _requestLogs.Add(new RequestLogEntry
        {
            Level = EvlogLevel.Debug,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(Exception ex, string? message, string? category)
    {
        if (!_active) return;
        _error = ex;
        _errorMessage = message;
        _requestLogs.Add(new RequestLogEntry
        {
            Level = EvlogLevel.Error,
            Message = message ?? ex.Message,
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
        });
    }

    // --- Accessors ---

    public IReadOnlyList<ContextEntry> GetEntries() => _entries;
    public IReadOnlyList<RequestLogEntry> GetRequestLogs() => _requestLogs;
    public Exception? GetError() => _error;
    public string? GetErrorMessage() => _errorMessage;

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
