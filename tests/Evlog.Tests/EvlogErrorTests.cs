using Evlog;
using Microsoft.AspNetCore.Mvc;

namespace Evlog.Tests;

public class EvlogErrorTests
{
    [Fact]
    public void Create_SetsAllProperties()
    {
        var error = EvlogError.Create(
            message: "Payment failed",
            status: 402,
            why: "Card declined",
            fix: "Try another card",
            link: "https://docs.example.com/errors/payment");

        Assert.Equal("Payment failed", error.Message);
        Assert.Equal(402, error.Status);
        Assert.Equal("Card declined", error.Why);
        Assert.Equal("Try another card", error.Fix);
        Assert.Equal("https://docs.example.com/errors/payment", error.Link);
    }

    [Fact]
    public void Create_DefaultsStatusTo500()
    {
        var error = EvlogError.Create("Internal error");
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public void Create_WithCause_SetsInnerException()
    {
        var cause = new InvalidOperationException("original");
        var error = EvlogError.Create("Wrapped error", cause: cause);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public void Create_IsException()
    {
        var error = EvlogError.Create("test");
        Assert.IsAssignableFrom<Exception>(error);
    }

    [Fact]
    public void ToProblemDetails_MapsCorrectly()
    {
        var error = EvlogError.Create(
            message: "Payment failed",
            status: 402,
            why: "Card declined",
            fix: "Try another card",
            link: "https://docs.example.com/errors/payment");

        var problem = error.ToProblemDetails();

        Assert.Equal("Payment failed", problem.Title);
        Assert.Equal(402, problem.Status);
        Assert.Equal("Card declined", problem.Detail);
        Assert.Equal("https://docs.example.com/errors/payment", problem.Type);
        Assert.Equal("Try another card", problem.Extensions["fix"]?.ToString());
    }

    [Fact]
    public void ToProblemDetails_WithNulls_OmitsOptionalFields()
    {
        var error = EvlogError.Create("Simple error", status: 400);

        var problem = error.ToProblemDetails();

        Assert.Equal("Simple error", problem.Title);
        Assert.Equal(400, problem.Status);
        Assert.Null(problem.Detail);
        Assert.Null(problem.Type);
        Assert.False(problem.Extensions.ContainsKey("fix"));
    }
}
