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
                // Flat key -- write directly (first-write-wins)
                if (written.Add(entry.Key))
                {
                    WriteEntryValue(writer, entry.Key, entry, fragmentBuffer);
                }
            }
            else
            {
                // Dotted key -- group by top-level
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
