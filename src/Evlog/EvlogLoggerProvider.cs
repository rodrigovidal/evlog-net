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
