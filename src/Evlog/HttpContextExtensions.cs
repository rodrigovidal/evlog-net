using Microsoft.AspNetCore.Http;

namespace Evlog;

public static class HttpContextExtensions
{
    private const string LoggerKey = "__evlog_logger";

    public static RequestLogger GetEvlogLogger(this HttpContext context)
    {
        if (context.Items.TryGetValue(LoggerKey, out var obj) && obj is RequestLogger logger)
            return logger;

        // Return a static inactive logger -- all methods are no-ops since _active is false
        return InactiveLogger.Instance;
    }

    internal static void SetEvlogLogger(this HttpContext context, RequestLogger logger)
    {
        context.Items[LoggerKey] = logger;
    }

    private static class InactiveLogger
    {
        internal static readonly RequestLogger Instance = new();
    }
}
