using Evlog;

namespace Evlog.Tests;

public class RequestLoggerTests
{
    [Fact]
    public void Set_String_AccumulatesEntry()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("user.id", "usr_123");
        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal("user.id", entries[0].Key);
        Assert.Equal("usr_123", entries[0].StringValue);
    }

    [Fact]
    public void Set_Int_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("order.count", 5);
        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal(ContextValueKind.Int, entries[0].Kind);
        Assert.Equal(5, entries[0].IntValue);
    }

    [Fact]
    public void Set_Double_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("order.total", 99.99);
        var entries = logger.GetEntries();
        Assert.Equal(ContextValueKind.Double, entries[0].Kind);
        Assert.Equal(99.99, entries[0].DoubleValue);
    }

    [Fact]
    public void Set_Bool_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("user.premium", true);
        var entries = logger.GetEntries();
        Assert.Equal(ContextValueKind.Bool, entries[0].Kind);
        Assert.True(entries[0].BoolValue);
    }

    [Fact]
    public void Set_Long_AccumulatesWithoutBoxing()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("event.timestamp", 1709500000000L);
        var entries = logger.GetEntries();
        Assert.Equal(ContextValueKind.Long, entries[0].Kind);
        Assert.Equal(1709500000000L, entries[0].LongValue);
    }

    [Fact]
    public void Set_MultipleKeys_AccumulatesAll()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("user.id", "usr_123");
        logger.Set("user.plan", "premium");
        logger.Set("order.total", 99.99);
        var entries = logger.GetEntries();
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void Set_DuplicateKey_FirstWriteWins()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("user.id", "first");
        logger.Set("user.id", "second");
        var entries = logger.GetEntries();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Set_WhenInactive_IsNoOp()
    {
        var logger = new RequestLogger();
        logger.Set("user.id", "usr_123");
        logger.Set("order.total", 99.99);
        var entries = logger.GetEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void TryReset_ClearsAllState()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set("user.id", "usr_123");
        logger.TryReset();
        var entries = logger.GetEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void SetJson_WritesFragmentToBuffer()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.SetJson("user", writer =>
        {
            writer.WriteString("id", "usr_123");
            writer.WriteString("plan", "premium");
        });
        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal(ContextValueKind.JsonFragment, entries[0].Kind);
        Assert.True(entries[0].FragmentLength > 0);
    }

    [Fact]
    public void Set_AnonymousObject_SerializesToJsonFragments()
    {
        var logger = new RequestLogger();
        logger.Activate("test-svc", "production");
        logger.Set(new { User = new { Id = "usr_123", Plan = "premium" } });
        var entries = logger.GetEntries();
        Assert.Single(entries);
        Assert.Equal(ContextValueKind.JsonFragment, entries[0].Kind);
        Assert.Equal("", entries[0].Key);
        Assert.True(entries[0].FragmentLength > 0);
    }

    [Fact]
    public void Set_AnonymousObject_WhenInactive_IsNoOp()
    {
        var logger = new RequestLogger();
        logger.Set(new { User = new { Id = "usr_123" } });
        var entries = logger.GetEntries();
        Assert.Empty(entries);
    }
}
