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
