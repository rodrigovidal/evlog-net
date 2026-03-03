using Microsoft.AspNetCore.Http;

namespace Evlog;

public static class HttpContextExtensions
{
    private const string EvlogLoggerKey = "__EvlogRequestLogger";

    public static RequestLogger GetEvlogLogger(this HttpContext context)
    {
        if (context.Items.TryGetValue(EvlogLoggerKey, out var value) && value is RequestLogger logger)
            return logger;

        // Return a non-active logger so callers never get null
        return new RequestLogger();
    }

    internal static void SetEvlogLogger(this HttpContext context, RequestLogger logger)
    {
        context.Items[EvlogLoggerKey] = logger;
    }
}
