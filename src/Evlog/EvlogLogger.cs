using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Evlog;

internal sealed class EvlogLogger : ILogger
{
    private readonly string _category;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EvlogLogger(string category, IHttpContextAccessor httpContextAccessor)
    {
        _category = category;
        _httpContextAccessor = httpContextAccessor;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null) return;

        var requestLogger = httpContext.GetEvlogLogger();
        if (!requestLogger.IsActive) return;

        var message = formatter(state, exception);
        var evlogLevel = MapLevel(logLevel);

        switch (evlogLevel)
        {
            case EvlogLevel.Error:
                requestLogger.Error(exception ?? new Exception(message), message, _category);
                break;
            case EvlogLevel.Warn:
                requestLogger.Warn(message, _category);
                break;
            case EvlogLevel.Debug:
                requestLogger.Debug(message, _category);
                break;
            default:
                requestLogger.Info(message, _category);
                break;
        }
    }

    private static EvlogLevel MapLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Critical or LogLevel.Error => EvlogLevel.Error,
        LogLevel.Warning => EvlogLevel.Warn,
        LogLevel.Debug or LogLevel.Trace => EvlogLevel.Debug,
        _ => EvlogLevel.Info,
    };
}
