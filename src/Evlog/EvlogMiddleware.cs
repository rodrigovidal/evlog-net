using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Evlog;

public sealed class EvlogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EvlogOptions _options;
    private readonly ObjectPool<RequestLogger> _loggerPool;

    public EvlogMiddleware(
        RequestDelegate next,
        IOptions<EvlogOptions> options,
        ObjectPool<RequestLogger> loggerPool)
    {
        _next = next;
        _options = options.Value;
        _loggerPool = loggerPool;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var logger = _loggerPool.Get();
        try
        {
            logger.Activate(
                _options.Service,
                _options.Environment,
                _options.Version,
                _options.CommitHash,
                _options.Region);

            var request = context.Request;
            var requestId = request.Headers["x-request-id"].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N")[..16];
            logger.SetRequest(request.Method, request.Path.Value ?? "/", requestId);

            context.SetEvlogLogger(logger);

            var startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, logger, ex);
            }

            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var status = context.Response.StatusCode;
            var durationMs = elapsed.TotalMilliseconds;

            // Tail sampling check
            bool forceKeep = _options.Sampling is not null
                && Sampling.ShouldKeep(status, durationMs, logger.Path, _options.Sampling);

            await EmitAndDrainAsync(logger, status, durationMs, forceKeep);
        }
        finally
        {
            _loggerPool.Return(logger);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, RequestLogger logger, Exception ex)
    {
        logger.Error(ex);

        if (ex is EvlogError evlogError)
        {
            context.Response.StatusCode = evlogError.Status;
            context.Response.ContentType = "application/problem+json";

            var problem = evlogError.ToProblemDetails();
            await context.Response.WriteAsJsonAsync(problem);
        }
        else
        {
            context.Response.StatusCode = 500;
        }
    }

    private async Task EmitAndDrainAsync(RequestLogger logger, int status, double durationMs, bool forceKeep)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        WideEventWriter.Write(logger, buffer, status, durationMs, forceKeep);

        if (_options.Pretty)
        {
            var pretty = PrettyOutputFormatter.Format(logger, status, durationMs);
            Console.Out.WriteLine(pretty);
        }
        else
        {
            await using var stdout = Console.OpenStandardOutput();
            stdout.Write(buffer.WrittenSpan);
            stdout.WriteByte((byte)'\n');
        }

        if (_options.Drain is not null)
        {
            var level = WideEventWriter.DetermineLevel(status, logger.GetError());
            var drainContext = new EvlogDrainContext
            {
                EventJson = buffer.WrittenMemory,
                Level = level,
                Status = status,
            };

            // Fire-and-forget
            _ = DrainSafeAsync(_options.Drain, drainContext);
        }
    }

    private static async Task DrainSafeAsync(EvlogDrainDelegate drain, EvlogDrainContext context)
    {
        try
        {
            await drain(context);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[evlog] drain error: {ex.Message}");
        }
    }
}
