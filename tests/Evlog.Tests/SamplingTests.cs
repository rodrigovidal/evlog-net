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
            Rates = new Dictionary<EvlogLevel, int> { [EvlogLevel.Info] = 0 }
        };
        Assert.False(Sampling.ShouldSample(EvlogLevel.Info, options));
    }

    [Fact]
    public void ShouldSample_HundredPercent_AlwaysTrue()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int> { [EvlogLevel.Info] = 100 }
        };
        Assert.True(Sampling.ShouldSample(EvlogLevel.Info, options));
    }

    [Fact]
    public void ShouldSample_ErrorDefaultsTo100_WhenNotConfigured()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int> { [EvlogLevel.Info] = 0 }
        };
        Assert.True(Sampling.ShouldSample(EvlogLevel.Error, options));
    }

    [Fact]
    public void ShouldSample_ErrorCanBeOverridden()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int> { [EvlogLevel.Error] = 0 }
        };
        Assert.False(Sampling.ShouldSample(EvlogLevel.Error, options));
    }

    [Fact]
    public void ShouldSample_UnconfiguredLevel_DefaultsTo100()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int> { [EvlogLevel.Info] = 10 }
        };
        Assert.True(Sampling.ShouldSample(EvlogLevel.Debug, options));
    }

    [Fact]
    public void ShouldSample_Probabilistic_RoughlyMatchesRate()
    {
        var options = new SamplingOptions
        {
            Rates = new Dictionary<EvlogLevel, int> { [EvlogLevel.Info] = 50 }
        };

        int sampled = 0;
        const int iterations = 10_000;
        for (int i = 0; i < iterations; i++)
        {
            if (Sampling.ShouldSample(EvlogLevel.Info, options))
                sampled++;
        }

        double rate = (double)sampled / iterations;
        Assert.InRange(rate, 0.40, 0.60);
    }
}
