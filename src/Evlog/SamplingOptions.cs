namespace Evlog;

public sealed class SamplingOptions
{
    public Dictionary<EvlogLevel, int>? Rates { get; set; }
    public List<TailSamplingCondition>? Keep { get; set; }
}

public sealed class TailSamplingCondition
{
    public int? Status { get; set; }
    public int? Duration { get; set; }
    public string? Path { get; set; }
}
