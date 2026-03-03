namespace Evlog;

public readonly struct RequestLogEntry
{
    public EvlogLevel Level { get; init; }
    public string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? Category { get; init; }
}
