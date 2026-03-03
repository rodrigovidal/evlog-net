using Microsoft.AspNetCore.Mvc;

namespace Evlog;

public sealed class EvlogError : Exception
{
    public int Status { get; init; } = 500;
    public string? Why { get; init; }
    public string? Fix { get; init; }
    public string? Link { get; init; }

    private EvlogError(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public static EvlogError Create(
        string message,
        int status = 500,
        string? why = null,
        string? fix = null,
        string? link = null,
        Exception? cause = null)
    {
        return new EvlogError(message, cause)
        {
            Status = status,
            Why = why,
            Fix = fix,
            Link = link,
        };
    }

    public ProblemDetails ToProblemDetails()
    {
        var problem = new ProblemDetails
        {
            Title = Message,
            Status = Status,
            Detail = Why,
            Type = Link,
        };

        if (Fix is not null)
            problem.Extensions["fix"] = Fix;

        return problem;
    }
}
