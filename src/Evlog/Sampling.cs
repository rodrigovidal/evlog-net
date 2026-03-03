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
