// GgufValue.cs
//
// One metadata value, and an array value whose elements stay in the mapped file until they
// are asked for: the 2B carries 248,320 token strings and as many merges, and the tokenizer
// builds its own tables from them rather than from half a million .NET strings.

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CircleAI.Inference.Managed.Gguf;

/// <summary>One metadata value: a number, a bool, a string, or an array of one of those.</summary>
public sealed class GgufValue
{
    // Integers are held as long (UInt64 as ulong), floats as they were stored.
    private readonly object _value;

    internal GgufValue(GgufType type, object value)
    {
        Type = type;
        _value = value;
    }

    /// <summary>The value's type in the file.</summary>
    public GgufType Type { get; }

    /// <summary>Any integer value, widened to long.</summary>
    public long AsInt64() => _value switch
    {
        long l => l,
        ulong u when u <= long.MaxValue => (long)u,
        ulong u => throw new OverflowException($"{u} does not fit a long."),
        _ => throw Mismatch("an integer"),
    };

    /// <summary>A Float32 or Float64 value.</summary>
    public double AsDouble() => _value switch
    {
        float f => f,
        double d => d,
        _ => throw Mismatch("a float"),
    };

    /// <summary>A Bool value.</summary>
    public bool AsBool() => _value is bool b ? b : throw Mismatch("a bool");

    /// <summary>A String value.</summary>
    public string AsString() => _value is string s ? s : throw Mismatch("a string");

    /// <summary>An Array value.</summary>
    public GgufArray AsArray() => _value as GgufArray ?? throw Mismatch("an array");

    /// <summary>
    /// A scalar as the fork's gguf_kv_to_str writes it: integers in decimal, floats as C's %f
    /// does (the exact value rounded half to even at six decimals), bools as true or false,
    /// strings as they are. Arrays have no rendering: the fork leaves them out of a model's
    /// metadata strings.
    /// </summary>
    public string Render() => _value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        float f => PrintfF(f),
        double d => PrintfF(d),
        _ => throw Mismatch("a scalar"),
    };

    /// <inheritdoc/>
    public override string ToString() => _value is GgufArray a ? $"{a.ElementType}[{a.Count}]" : Render();

    // C's "%f": six decimals of the value's exact binary expansion, ties to even; -0 keeps its sign.
    internal static string PrintfF(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";

        var bits = BitConverter.DoubleToInt64Bits(value) & long.MaxValue;
        var exponent = (int)(bits >> 52);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        exponent -= 1075; // |value| = mantissa * 2^exponent

        BigInteger numerator = mantissa;
        var denominator = BigInteger.One;
        if (exponent > 0) numerator <<= exponent;
        else denominator <<= -exponent;
        numerator *= 1_000_000;

        var millionths = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !millionths.IsEven)) millionths += 1;

        var digits = millionths.ToString(CultureInfo.InvariantCulture).PadLeft(7, '0');
        var text = digits[..^6] + "." + digits[^6..];
        return double.IsNegative(value) ? "-" + text : text;
    }

    private InvalidOperationException Mismatch(string wanted) => new($"The value is {Type}, not {wanted}.");
}

/// <summary>
/// An array value. Numbers are read from the mapped file when asked for; strings are located
/// once, when the file is opened, and decoded only when asked for. Valid while its
/// <see cref="GgufFile"/> is open.
/// </summary>
public sealed class GgufArray
{
    private readonly GgufFile _file;
    private readonly long _start;
    private readonly long[]? _strings;

    internal GgufArray(GgufFile file, GgufType elementType, int count, long start, long[]? strings)
    {
        _file = file;
        ElementType = elementType;
        Count = count;
        _start = start;
        _strings = strings;
    }

    /// <summary>The type of every element.</summary>
    public GgufType ElementType { get; }

    /// <summary>Elements in the array.</summary>
    public int Count { get; }

    /// <summary>An integer element, widened to long.</summary>
    public long GetInt64(int index) => ElementType switch
    {
        GgufType.UInt8 => Element(index, 1)[0],
        GgufType.Int8 => (sbyte)Element(index, 1)[0],
        GgufType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(Element(index, 2)),
        GgufType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(Element(index, 2)),
        GgufType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(Element(index, 4)),
        GgufType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(Element(index, 4)),
        GgufType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(Element(index, 8)),
        GgufType.UInt64 => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(Element(index, 8))),
        _ => throw Mismatch("integers"),
    };

    /// <summary>A Float32 or Float64 element.</summary>
    public double GetDouble(int index) => ElementType switch
    {
        GgufType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(Element(index, 4)),
        GgufType.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(Element(index, 8)),
        _ => throw Mismatch("floats"),
    };

    /// <summary>A Bool element.</summary>
    public bool GetBool(int index) => ElementType == GgufType.Bool ? Element(index, 1)[0] != 0 : throw Mismatch("bools");

    /// <summary>A String element's UTF-8 bytes, as a view of the mapped file.</summary>
    public ReadOnlySpan<byte> GetUtf8(int index)
    {
        if (_strings is null) throw Mismatch("strings");
        CheckIndex(index);
        var start = _strings[index];
        var length = BinaryPrimitives.ReadUInt64LittleEndian(_file.Span(start - 8, 8));
        return _file.Span(start, (int)length);
    }

    /// <summary>A String element.</summary>
    public string GetString(int index) => Encoding.UTF8.GetString(GetUtf8(index));

    /// <summary>Every element as an int; integers only, each of which must fit.</summary>
    public int[] ToInt32Array()
    {
        var values = new int[Count];
        for (var i = 0; i < values.Length; i++) values[i] = checked((int)GetInt64(i));
        return values;
    }

    /// <summary>Every String element.</summary>
    public string[] ToStringArray()
    {
        var values = new string[Count];
        for (var i = 0; i < values.Length; i++) values[i] = GetString(i);
        return values;
    }

    private ReadOnlySpan<byte> Element(int index, int size)
    {
        CheckIndex(index);
        return _file.Span(_start + (long)index * size, size);
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"The array holds {Count}.");
    }

    private InvalidOperationException Mismatch(string wanted) => new($"The array holds {ElementType}, not {wanted}.");
}
