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
