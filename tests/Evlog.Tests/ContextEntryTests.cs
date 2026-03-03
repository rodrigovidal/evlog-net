using Evlog;

namespace Evlog.Tests;

public class ContextEntryTests
{
    [Fact]
    public void CreateString_StoresStringValue()
    {
        var entry = ContextEntry.String("user.name", "alice");
        Assert.Equal("user.name", entry.Key);
        Assert.Equal(ContextValueKind.String, entry.Kind);
        Assert.Equal("alice", entry.StringValue);
    }

    [Fact]
    public void CreateInt_StoresIntWithoutBoxing()
    {
        var entry = ContextEntry.Int("order.count", 42);
        Assert.Equal("order.count", entry.Key);
        Assert.Equal(ContextValueKind.Int, entry.Kind);
        Assert.Equal(42, entry.IntValue);
    }

    [Fact]
    public void CreateLong_StoresLong()
    {
        var entry = ContextEntry.Long("timestamp", 1709500000000L);
        Assert.Equal(ContextValueKind.Long, entry.Kind);
        Assert.Equal(1709500000000L, entry.LongValue);
    }

    [Fact]
    public void CreateDouble_StoresDouble()
    {
        var entry = ContextEntry.Double("order.total", 99.99);
        Assert.Equal(ContextValueKind.Double, entry.Kind);
        Assert.Equal(99.99, entry.DoubleValue);
    }

    [Fact]
    public void CreateBool_StoresBool()
    {
        var entry = ContextEntry.Bool("user.premium", true);
        Assert.Equal(ContextValueKind.Bool, entry.Kind);
        Assert.True(entry.BoolValue);
    }

    [Fact]
    public void CreateJsonFragment_StoresOffsetAndLength()
    {
        var entry = ContextEntry.JsonFragment("user", 0, 25);
        Assert.Equal(ContextValueKind.JsonFragment, entry.Kind);
        Assert.Equal(0, entry.FragmentOffset);
        Assert.Equal(25, entry.FragmentLength);
    }
}
