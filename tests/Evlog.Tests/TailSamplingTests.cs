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
        Assert.True(Sampling.ShouldKeep(500, 100, null, options));
        Assert.True(Sampling.ShouldKeep(200, 2000, null, options));
        Assert.False(Sampling.ShouldKeep(200, 100, null, options));
    }
}
