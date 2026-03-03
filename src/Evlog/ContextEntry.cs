using System.Runtime.InteropServices;

namespace Evlog;

public enum ContextValueKind : byte
{
    String,
    Int,
    Long,
    Double,
    Bool,
    JsonFragment,
}

[StructLayout(LayoutKind.Auto)]
public struct ContextEntry
{
    public string Key;
    public ContextValueKind Kind;
    public string? StringValue;
    public long LongValue;
    public double DoubleValue;
    public int FragmentOffset;
    public int FragmentLength;

    public int IntValue
    {
        readonly get => (int)LongValue;
        set => LongValue = value;
    }

    public bool BoolValue
    {
        readonly get => LongValue != 0;
        set => LongValue = value ? 1 : 0;
    }

    public static ContextEntry String(string key, string value) => new()
    {
        Key = key, Kind = ContextValueKind.String, StringValue = value,
    };

    public static ContextEntry Int(string key, int value) => new()
    {
        Key = key, Kind = ContextValueKind.Int, LongValue = value,
    };

    public static ContextEntry Long(string key, long value) => new()
    {
        Key = key, Kind = ContextValueKind.Long, LongValue = value,
    };

    public static ContextEntry Double(string key, double value) => new()
    {
        Key = key, Kind = ContextValueKind.Double, DoubleValue = value,
    };

    public static ContextEntry Bool(string key, bool value) => new()
    {
        Key = key, Kind = ContextValueKind.Bool, LongValue = value ? 1 : 0,
    };

    public static ContextEntry JsonFragment(string key, int offset, int length) => new()
    {
        Key = key, Kind = ContextValueKind.JsonFragment, FragmentOffset = offset, FragmentLength = length,
    };
}
