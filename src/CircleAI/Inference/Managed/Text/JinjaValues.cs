// JinjaValues.cs
//
// The values a chat template computes with, and what Python and jinja2 do with them -
// because the templates were written for, and are checked against, jinja2 running in
// Python. Numbers keep Python's two kinds (int and float, a float printed the way Python's
// repr prints it); text is measured, indexed and sliced in code points as Python does; a
// missing value is jinja2's Undefined, which prints as nothing and fails when used as an
// object; tojson is Hugging Face's json.dumps(ensure_ascii=False).
//
// The filters, tests and methods listed here are the whole subset: a template that uses
// anything else is refused when it is parsed (JinjaTemplate.Parse), never skipped when run.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CircleAI.Inference.Managed.Text;

internal static class JinjaValues
{
    /// <summary>Filters the subset reads.</summary>
    internal static readonly HashSet<string> Filters =
        ["trim", "length", "count", "tojson", "safe", "items", "string", "default", "d", "join", "first", "last", "list"];

    /// <summary>Tests the subset reads.</summary>
    internal static readonly HashSet<string> Tests =
        ["defined", "undefined", "none", "string", "mapping", "sequence", "iterable", "number", "integer", "float", "boolean", "true", "false"];

    /// <summary>Methods of strings and dicts the subset calls.</summary>
    internal static readonly HashSet<string> Methods =
        ["startswith", "endswith", "split", "strip", "lstrip", "rstrip", "replace", "join", "items", "keys", "values", "get"];

    /// <summary>Global functions the subset calls.</summary>
    internal static readonly HashSet<string> Globals = ["raise_exception", "namespace", "range"];

    /// <summary>
    /// Functions jinja2 or Hugging Face provide that the subset does not: a template calling one
    /// is refused when parsed. A call to any other unknown name fails when it runs, as in jinja2.
    /// </summary>
    internal static readonly HashSet<string> RefusedGlobals = ["strftime_now", "lipsum", "dict", "cycler", "joiner"];

    private static readonly HashSet<string> DictMethods = ["items", "keys", "values", "get"];
    private static readonly HashSet<string> StringMethods = ["startswith", "endswith", "split", "strip", "lstrip", "rstrip", "replace", "join"];

    // ---- the value kinds beyond null, bool, long, double, string, list and dict ----

    /// <summary>jinja2's Undefined: prints as nothing, is false, iterates as empty, fails as an object.</summary>
    internal sealed class Undefined(string? name)
    {
        public string? Name { get; } = name;

        public TemplateException Error() => new(Name is null ? "a value is undefined" : $"'{Name}' is undefined");
    }

    /// <summary>The result of namespace(...): attributes a loop body can assign.</summary>
    internal sealed class Namespace
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A string's or dict's method, taken as an attribute and not yet called.</summary>
    internal sealed record BoundMethod(object Target, string Name);

    /// <summary>A global function.</summary>
    internal sealed record Builtin(string Name);

    /// <summary>The loop variable of a for loop.</summary>
    internal sealed class LoopInfo(IReadOnlyList<object?> items, int index)
    {
        public object? Get(string name) => name switch
        {
            "index" => (object)((long)index + 1),
            "index0" => (object)(long)index,
            "revindex" => (object)(long)(items.Count - index),
            "revindex0" => (object)(long)(items.Count - index - 1),
            "first" => index == 0,
            "last" => index == items.Count - 1,
            "length" => (object)(long)items.Count,
            "depth" => (object)1L,
            "depth0" => (object)0L,
            "previtem" => index > 0 ? items[index - 1] : new Undefined("there is no previous item"),
            "nextitem" => index < items.Count - 1 ? items[index + 1] : new Undefined("there is no next item"),
            _ => new Undefined(name),
        };
    }

    // ---- from .NET and JSON ----

    /// <summary>A caller's value in the forms the template computes with.</summary>
    internal static object? Normalize(object? value) => value switch
    {
        null => null,
        string or bool or long or double => value,
        int i => (long)i,
        uint u => (long)u,
        short s => (long)s,
        ushort s => (long)s,
        byte b => (long)b,
        sbyte b => (long)b,
        ulong u => checked((long)u),
        float f => (double)f,
        decimal m => (double)m,
        JsonElement e => FromJson(e),
        OrderedDictionary<string, object?> d => NormalizeDict(d),
        IReadOnlyDictionary<string, object?> d => NormalizeDict(d),
        IDictionary<string, object?> d => NormalizeDict(d),
        System.Collections.IEnumerable e => NormalizeList(e),
        _ => throw new NotSupportedException($"A template value of type {value.GetType().Name} is not supported."),
    };

    private static OrderedDictionary<string, object?> NormalizeDict(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var dict = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, v) in pairs) dict[key] = Normalize(v);
        return dict;
    }

    private static List<object?> NormalizeList(System.Collections.IEnumerable items)
    {
        var list = new List<object?>();
        foreach (var item in items) list.Add(Normalize(item));
        return list;
    }

    /// <summary>A JSON value as Python's json.loads gives it: a number with a point or an exponent is a float.</summary>
    internal static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => JsonNumber(element),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => JsonObject(element),
        _ => throw new NotSupportedException($"JSON {element.ValueKind} is not supported."),
    };

    // Boxed one by one: a conditional over long and double would make every integer a double.
    private static object JsonNumber(JsonElement element)
    {
        if (element.GetRawText().AsSpan().IndexOfAny(".eE") >= 0) return element.GetDouble();
        if (element.TryGetInt64(out var l)) return l;
        return element.GetDouble();
    }

    private static OrderedDictionary<string, object?> JsonObject(JsonElement element)
    {
        var dict = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) dict[property.Name] = FromJson(property.Value); // a repeated key: the last wins, as in Python
        return dict;
    }

    // ---- Python's truth, text and comparison ----

    // A tuple - (a, b) in a template, a pair from items() - is an object?[]; a list is a List<object?>.

    internal static bool Truthy(object? v) => v switch
    {
        null or Undefined => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        List<object?> list => list.Count > 0,
        object?[] tuple => tuple.Length > 0,
        OrderedDictionary<string, object?> dict => dict.Count > 0,
        _ => true,
    };

    /// <summary>What {{ v }} prints: Python's str(), and nothing for Undefined.</summary>
    internal static string ToStr(object? v) => v switch
    {
        string s => s,
        Undefined => "",
        null => "None",
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => PyFloat(d),
        List<object?> or object?[] or OrderedDictionary<string, object?> => Repr(v),
        Namespace ns => "<Namespace " + Repr(ToDict(ns.Values)) + ">",
        _ => throw new TemplateException($"A {Kind(v)} cannot be printed."),
    };

    /// <summary>Python's repr(), for printing lists, tuples and dicts.</summary>
    internal static string Repr(object? v) => v switch
    {
        string s => PyStrRepr(s),
        List<object?> list => "[" + string.Join(", ", list.Select(Repr)) + "]",
        object?[] { Length: 1 } one => "(" + Repr(one[0]) + ",)",
        object?[] tuple => "(" + string.Join(", ", tuple.Select(Repr)) + ")",
        OrderedDictionary<string, object?> dict => "{" + string.Join(", ", dict.Select(p => PyStrRepr(p.Key) + ": " + Repr(p.Value))) + "}",
        Undefined => "Undefined",
        _ => ToStr(v),
    };

    private static OrderedDictionary<string, object?> ToDict(Dictionary<string, object?> values)
    {
        var dict = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in values) dict[k] = v;
        return dict;
    }

    /// <summary>Python's repr of a float: the shortest digits that read back, fixed between 1e-4 and 1e16.</summary>
    internal static string PyFloat(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        if (v == 0) return double.IsNegative(v) ? "-0.0" : "0.0";

        // The shortest round-trip digits, and where the point goes: |v| = 0.digits * 10^point.
        var shortest = Math.Abs(v).ToString("R", CultureInfo.InvariantCulture);
        var e = shortest.IndexOfAny(['E', 'e']);
        var mantissa = e >= 0 ? shortest[..e] : shortest;
        var exponent = e >= 0 ? int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.');
        var digits = dot >= 0 ? mantissa.Remove(dot, 1) : mantissa;
        var point = (dot >= 0 ? dot : mantissa.Length) + exponent;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        point -= lead;
        if (digits.Length == 0) digits = "0";

        string text;
        if (point is > -4 and <= 16)
        {
            if (point <= 0) text = "0." + new string('0', -point) + digits;
            else if (point >= digits.Length) text = digits + new string('0', point - digits.Length) + ".0";
            else text = digits[..point] + "." + digits[point..];
        }
        else
        {
            var x = point - 1;
            text = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("00", CultureInfo.InvariantCulture);
        }
        return v < 0 ? "-" + text : text;
    }

    // Python's repr of a string: single quotes unless the text holds one and no double quote.
    private static string PyStrRepr(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (var rune in s.EnumerateRunes())
        {
            var c = rune.Value;
            if (c == quote || c == '\\') sb.Append('\\').Append((char)c);
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < 0x20 || c == 0x7F) sb.Append("\\x").Append(c.ToString("x2", CultureInfo.InvariantCulture));
            else if (c < 0x7F || IsPrintable(rune)) sb.Append(rune.ToString());
            else if (c <= 0xFF) sb.Append("\\x").Append(c.ToString("x2", CultureInfo.InvariantCulture));
            else if (c <= 0xFFFF) sb.Append("\\u").Append(c.ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append("\\U").Append(c.ToString("x8", CultureInfo.InvariantCulture));
        }
        return sb.Append(quote).ToString();
    }

    private static bool IsPrintable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.SpaceSeparator => false,
        _ => true,
    };

    /// <summary>Python's str.isspace for one character.</summary>
    internal static bool IsSpace(int c) => c is >= 0x1C and <= 0x1F || (c <= 0xFFFF && char.IsWhiteSpace((char)c))
        || (c > 0xFFFF && Rune.IsWhiteSpace(new Rune(c)));

    private static string Kind(object? v) => v switch
    {
        null => "None",
        Undefined => "undefined value",
        bool => "bool",
        long => "int",
        double => "float",
        string => "str",
        List<object?> => "list",
        object?[] => "tuple",
        OrderedDictionary<string, object?> => "dict",
        Namespace => "namespace",
        BoundMethod or Builtin => "function",
        LoopInfo => "loop",
        _ => v.GetType().Name,
    };

    internal static bool Eq(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is Undefined || b is Undefined) return a is Undefined && b is Undefined;
        if (IsNumber(a) && IsNumber(b))
            return a is double || b is double ? AsDouble(a) == AsDouble(b) : AsLong(a) == AsLong(b);
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is List<object?> la && b is List<object?> lb) return la.Count == lb.Count && la.Zip(lb).All(p => Eq(p.First, p.Second));
        if (a is object?[] ta && b is object?[] tb) return ta.Length == tb.Length && ta.Zip(tb).All(p => Eq(p.First, p.Second));
        if (a is OrderedDictionary<string, object?> da && b is OrderedDictionary<string, object?> db)
            return da.Count == db.Count && da.All(p => db.TryGetValue(p.Key, out var other) && Eq(p.Value, other));
        return ReferenceEquals(a, b);
    }

    /// <summary>Python's ordering of two values: numbers, strings by code point, lists item by item.</summary>
    internal static int Compare(object? a, object? b)
    {
        if (IsNumber(a) && IsNumber(b))
            return a is double || b is double ? AsDouble(a).CompareTo(AsDouble(b)) : AsLong(a).CompareTo(AsLong(b));
        if (a is string sa && b is string sb) return CodePointCompare(sa, sb);
        if ((a is List<object?> && b is List<object?>) || (a is object?[] && b is object?[]))
        {
            var la = Iterate(a).ToList();
            var lb = Iterate(b).ToList();
            for (var i = 0; i < Math.Min(la.Count, lb.Count); i++)
            {
                if (!Eq(la[i], lb[i])) return Compare(la[i], lb[i]);
            }
            return la.Count.CompareTo(lb.Count);
        }
        if (a is Undefined u) throw u.Error();
        if (b is Undefined u2) throw u2.Error();
        throw new TemplateException($"A {Kind(a)} and a {Kind(b)} cannot be ordered.");
    }

    private static int CodePointCompare(string a, string b)
    {
        var ea = a.EnumerateRunes();
        var eb = b.EnumerateRunes();
        while (true)
        {
            var hasA = ea.MoveNext();
            var hasB = eb.MoveNext();
            if (!hasA || !hasB) return hasA.CompareTo(hasB);
            var c = ea.Current.Value.CompareTo(eb.Current.Value);
            if (c != 0) return c;
        }
    }

    private static bool IsNumber(object? v) => v is bool or long or double;

    private static long AsLong(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        _ => throw new TemplateException($"A {Kind(v)} is not an int."),
    };

    private static double AsDouble(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        double d => d,
        _ => throw new TemplateException($"A {Kind(v)} is not a number."),
    };

    // ---- operators ----

    internal static object? Binary(string op, object? a, object? b)
    {
        if (op == "~") return ToStr(a) + ToStr(b); // str() of each, so Undefined joins as nothing
        if (a is Undefined ua) throw ua.Error();
        if (b is Undefined ub) throw ub.Error();
        if (IsNumber(a) && IsNumber(b))
        {
            // Each result boxed on its own: "floats ? double : long" would make every int a float.
            var floats = a is double || b is double;
            switch (op)
            {
                case "+": return floats ? (object)(AsDouble(a) + AsDouble(b)) : AsLong(a) + AsLong(b);
                case "-": return floats ? (object)(AsDouble(a) - AsDouble(b)) : AsLong(a) - AsLong(b);
                case "*": return floats ? (object)(AsDouble(a) * AsDouble(b)) : AsLong(a) * AsLong(b);
                case "/":
                    if (AsDouble(b) == 0) throw new TemplateException("Division by zero.");
                    return AsDouble(a) / AsDouble(b);
                case "//":
                    if (AsDouble(b) == 0) throw new TemplateException("Division by zero.");
                    return floats ? (object)Math.Floor(AsDouble(a) / AsDouble(b)) : FloorDiv(AsLong(a), AsLong(b));
                case "%":
                    if (AsDouble(b) == 0) throw new TemplateException("Modulo by zero.");
                    if (floats)
                    {
                        var x = AsDouble(a);
                        var y = AsDouble(b);
                        var r = x % y;
                        return r != 0 && (r < 0) != (y < 0) ? r + y : r;
                    }
                    return AsLong(a) - FloorDiv(AsLong(a), AsLong(b)) * AsLong(b);
                case "**":
                    if (!floats && AsLong(b) >= 0) return (long)BigInteger.Pow(AsLong(a), checked((int)AsLong(b)));
                    return Math.Pow(AsDouble(a), AsDouble(b));
            }
        }
        if (op == "+" && a is string s1 && b is string s2) return s1 + s2;
        if (op == "+" && a is List<object?> l1 && b is List<object?> l2) return l1.Concat(l2).ToList();
        if (op == "+" && a is object?[] t1 && b is object?[] t2) return t1.Concat(t2).ToArray();
        if (op == "*" && a is string text && b is long or bool) return string.Concat(Enumerable.Repeat(text, (int)Math.Max(0, AsLong(b))));
        throw new TemplateException($"'{op}' is not supported between a {Kind(a)} and a {Kind(b)}.");
    }

    private static long FloorDiv(long a, long b)
    {
        var q = a / b;
        return (a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q;
    }

    internal static object? Unary(string op, object? v)
    {
        if (v is Undefined u) throw u.Error();
        return op switch
        {
            "-" when v is double d => (object)(-d), // each arm boxed: mixed, they would all become double
            "-" when IsNumber(v) => (object)(-AsLong(v)),
            "+" when v is double d => (object)d,
            "+" when IsNumber(v) => (object)AsLong(v),
            _ => throw new TemplateException($"Unary '{op}' is not supported for a {Kind(v)}."),
        };
    }

    /// <summary>Python's "item in container".</summary>
    internal static bool Contains(object? container, object? item) => container switch
    {
        string s when item is string sub => s.Contains(sub, StringComparison.Ordinal),
        string => throw new TemplateException($"'in <string>' needs a string, not a {Kind(item)}."),
        List<object?> list => list.Any(x => Eq(x, item)),
        object?[] tuple => tuple.Any(x => Eq(x, item)),
        OrderedDictionary<string, object?> dict => item is string key && dict.ContainsKey(key),
        Undefined => false,
        _ => throw new TemplateException($"A {Kind(container)} cannot be searched with 'in'."),
    };

    // ---- attributes, items, slices, iteration ----

    /// <summary>jinja2's getattr: the attribute first (a method of a string or dict), then the item.</summary>
    internal static object? GetAttr(object? obj, string name) => obj switch
    {
        Undefined u => throw u.Error(),
        OrderedDictionary<string, object?> dict when DictMethods.Contains(name) => new BoundMethod(dict, name),
        OrderedDictionary<string, object?> dict => dict.TryGetValue(name, out var v) ? v : new Undefined(name),
        string s when StringMethods.Contains(name) => new BoundMethod(s, name),
        Namespace ns => ns.Values.TryGetValue(name, out var v) ? v : new Undefined(name),
        LoopInfo loop => loop.Get(name),
        _ => new Undefined(name),
    };

    /// <summary>jinja2's getitem: the item first, then (for a string key) the attribute.</summary>
    internal static object? GetItem(object? obj, object? key)
    {
        switch (obj)
        {
            case Undefined u:
                throw u.Error();
            case List<object?> list when key is long or bool:
            {
                var i = AsLong(key);
                if (i < 0) i += list.Count;
                return i >= 0 && i < list.Count ? list[(int)i] : new Undefined($"index {AsLong(key)}");
            }
            case object?[] tuple when key is long or bool:
            {
                var i = AsLong(key);
                if (i < 0) i += tuple.Length;
                return i >= 0 && i < tuple.Length ? tuple[(int)i] : new Undefined($"index {AsLong(key)}");
            }
            case string s when key is long or bool:
            {
                var runes = Runes(s);
                var i = AsLong(key);
                if (i < 0) i += runes.Length;
                return i >= 0 && i < runes.Length ? runes[(int)i].ToString() : new Undefined($"index {AsLong(key)}");
            }
            case OrderedDictionary<string, object?> dict when key is string k:
                return dict.TryGetValue(k, out var v) ? v : new Undefined(k);
        }
        return key is string name ? GetAttr(obj, name) : new Undefined(ToStr(key));
    }

    /// <summary>Python's obj[start:stop:step] on a list or a string.</summary>
    internal static object? Slice(object? obj, object? start, object? stop, object? step)
    {
        if (obj is Undefined u) throw u.Error();
        var stride = step is null or Undefined ? 1 : AsLong(step);
        if (stride == 0) throw new TemplateException("A slice step cannot be zero.");
        if (obj is List<object?> list) return Pick(list.Count, start, stop, stride).Select(i => list[i]).ToList();
        if (obj is object?[] tuple) return Pick(tuple.Length, start, stop, stride).Select(i => tuple[i]).ToArray();
        if (obj is string s)
        {
            var runes = Runes(s);
            var sb = new StringBuilder();
            foreach (var i in Pick(runes.Length, start, stop, stride)) sb.Append(runes[i].ToString());
            return sb.ToString();
        }
        throw new TemplateException($"A {Kind(obj)} cannot be sliced.");
    }

    // The indices a Python slice takes (PySlice_AdjustIndices).
    private static IEnumerable<int> Pick(int length, object? start, object? stop, long step)
    {
        long Bound(object? v, long whenNone)
        {
            if (v is null or Undefined) return whenNone;
            var x = AsLong(v);
            if (x < 0) x += length;
            return step > 0 ? Math.Clamp(x, 0, length) : Math.Clamp(x, -1, length - 1);
        }

        var from = Bound(start, step > 0 ? 0 : length - 1);
        var to = Bound(stop, step > 0 ? length : -1);
        for (var i = from; step > 0 ? i < to : i > to; i += step) yield return (int)i;
    }

    private static Rune[] Runes(string s) => s.EnumerateRunes().ToArray();

    /// <summary>Python's iteration: a list's items, a string's characters, a dict's keys; nothing for Undefined.</summary>
    internal static IEnumerable<object?> Iterate(object? v) => v switch
    {
        List<object?> list => list,
        object?[] tuple => tuple,
        string s => s.EnumerateRunes().Select(r => (object?)r.ToString()).ToList(),
        OrderedDictionary<string, object?> dict => dict.Keys.Select(k => (object?)k).ToList(),
        Undefined => [],
        _ => throw new TemplateException($"A {Kind(v)} cannot be iterated."),
    };

    /// <summary>Python's len().</summary>
    internal static long Len(object? v) => v switch
    {
        string s => s.EnumerateRunes().Count(),
        List<object?> list => list.Count,
        object?[] tuple => tuple.Length,
        OrderedDictionary<string, object?> dict => dict.Count,
        Undefined => 0,
        _ => throw new TemplateException($"A {Kind(v)} has no length."),
    };

    // ---- filters, tests, methods, globals ----

    internal static object? Filter(string name, object? value, IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> kwargs)
    {
        object? Arg(int position, string key, object? fallback) =>
            kwargs.TryGetValue(key, out var k) ? k : position < args.Count ? args[position] : fallback;

        switch (name)
        {
            case "trim":
                return PyStrip(ToStr(value), Arg(0, "chars", null), left: true, right: true);
            case "length" or "count":
                return Len(value);
            case "tojson":
                // Hugging Face's own: json.dumps(x, ensure_ascii=False, indent=None, separators=None, sort_keys=False).
                if (Arg(2, "separators", null) is not null) throw new NotSupportedException("tojson separators are not supported.");
                var indent = Arg(1, "indent", null);
                return ToJson(value, indent switch
                {
                    null => null,
                    long n => new string(' ', (int)Math.Max(0, n)),
                    string s => s,
                    _ => throw new TemplateException($"tojson indent cannot be a {Kind(indent)}."),
                }, Truthy(Arg(3, "sort_keys", false)), Truthy(Arg(0, "ensure_ascii", false)));
            case "safe" or "string":
                return ToStr(value);
            case "items":
                return value switch
                {
                    Undefined => new List<object?>(),
                    OrderedDictionary<string, object?> dict => dict.Select(p => (object?)new object?[] { p.Key, p.Value }).ToList(),
                    _ => throw new TemplateException($"Item pairs come only from a mapping, not a {Kind(value)}."),
                };
            case "default" or "d":
                return value is Undefined || (Truthy(Arg(1, "boolean", false)) && !Truthy(value)) ? Arg(0, "default_value", "") : value;
            case "join":
                return string.Join(ToStr(Arg(0, "d", "")), Iterate(value).Select(ToStr));
            case "first":
                return Iterate(value).Cast<object?>().DefaultIfEmpty(new Undefined("first of an empty sequence")).First();
            case "last":
                return Iterate(value).Cast<object?>().DefaultIfEmpty(new Undefined("last of an empty sequence")).Last();
            case "list":
                return Iterate(value).ToList();
        }
        throw new NotSupportedException($"The filter '{name}' is not in JinjaLite's subset.");
    }

    internal static bool Test(string name, object? value) => name switch
    {
        "defined" => value is not Undefined,
        "undefined" => value is Undefined,
        "none" => value is null,
        "string" => value is string,
        "mapping" => value is OrderedDictionary<string, object?>,
        // jinja2's own tests: anything with a length and items is a sequence, anything that
        // iterates is iterable - strings and dicts included, and Undefined (empty) too.
        "sequence" => value is string or List<object?> or object?[] or OrderedDictionary<string, object?> or Undefined,
        "iterable" => value is string or List<object?> or object?[] or OrderedDictionary<string, object?> or Undefined,
        "number" => value is bool or long or double,
        "integer" => value is long,
        "float" => value is double,
        "boolean" => value is bool,
        "true" => value is true,
        "false" => value is false,
        _ => throw new NotSupportedException($"The test '{name}' is not in JinjaLite's subset."),
    };

    internal static object? CallMethod(BoundMethod method, IReadOnlyList<object?> args)
    {
        object? Arg(int i, object? fallback = null) => i < args.Count ? args[i] : fallback;

        if (method.Target is string s)
        {
            switch (method.Name)
            {
                case "startswith":
                    return Affixes(Arg(0)).Any(p => s.StartsWith(p, StringComparison.Ordinal));
                case "endswith":
                    return Affixes(Arg(0)).Any(p => s.EndsWith(p, StringComparison.Ordinal));
                case "split":
                    return PySplit(s, Arg(0), Arg(1) is null ? -1 : AsLong(Arg(1)));
                case "strip":
                    return PyStrip(s, Arg(0), left: true, right: true);
                case "lstrip":
                    return PyStrip(s, Arg(0), left: true, right: false);
                case "rstrip":
                    return PyStrip(s, Arg(0), left: false, right: true);
                case "replace":
                    if (Arg(0) is not string old || Arg(1) is not string replacement) throw new TemplateException("replace() takes two strings.");
                    return PyReplace(s, old, replacement, Arg(2) is null ? -1 : AsLong(Arg(2)));
                case "join":
                    return string.Join(s, Iterate(Arg(0)).Select(item => item as string
                        ?? throw new TemplateException($"join() takes strings, not a {Kind(item)}.")));
            }
        }
        else if (method.Target is OrderedDictionary<string, object?> dict)
        {
            switch (method.Name)
            {
                case "items":
                    return dict.Select(p => (object?)new object?[] { p.Key, p.Value }).ToList();
                case "keys":
                    return dict.Keys.Select(k => (object?)k).ToList();
                case "values":
                    return dict.Values.ToList();
                case "get":
                    return Arg(0) is string key && dict.TryGetValue(key, out var v) ? v : Arg(1);
            }
        }
        throw new TemplateException($"A {Kind(method.Target)} has no method '{method.Name}'.");
    }

    private static IEnumerable<string> Affixes(object? arg) => arg switch
    {
        string s => [s],
        object?[] tuple => tuple.Select(x => x as string ?? throw new TemplateException("startswith/endswith take strings.")),
        _ => throw new TemplateException($"startswith/endswith take a string or a tuple of them, not a {Kind(arg)}."),
    };

    // Python's str.split: on a separator, or on runs of whitespace (dropping empties) when none is given.
    private static List<object?> PySplit(string s, object? separator, long maxSplit)
    {
        var parts = new List<object?>();
        if (separator is string sep)
        {
            if (sep.Length == 0) throw new TemplateException("split() needs a non-empty separator.");
            var start = 0;
            while (maxSplit < 0 || parts.Count < maxSplit)
            {
                var at = s.IndexOf(sep, start, StringComparison.Ordinal);
                if (at < 0) break;
                parts.Add(s[start..at]);
                start = at + sep.Length;
            }
            parts.Add(s[start..]);
            return parts;
        }
        if (separator is not null) throw new TemplateException($"split() takes a string, not a {Kind(separator)}.");

        var runes = Runes(s);
        var i = 0;
        while (true)
        {
            while (i < runes.Length && IsSpace(runes[i].Value)) i++;
            if (i >= runes.Length) break;
            if (maxSplit >= 0 && parts.Count == maxSplit)
            {
                var rest = string.Concat(runes[i..].Select(r => r.ToString()));
                var end = rest.Length;
                while (end > 0 && IsSpace(rest[end - 1])) end--;
                parts.Add(rest[..end]);
                break;
            }
            var word = new StringBuilder();
            while (i < runes.Length && !IsSpace(runes[i].Value)) word.Append(runes[i++].ToString());
            parts.Add(word.ToString());
        }
        return parts;
    }

    // Python's str.strip / lstrip / rstrip: whitespace, or any of the given characters.
    internal static string PyStrip(string s, object? chars, bool left, bool right)
    {
        if (chars is not (null or string)) throw new TemplateException($"strip() takes a string, not a {Kind(chars)}.");
        var set = chars is string c ? c.EnumerateRunes().Select(r => r.Value).ToHashSet() : null;
        bool Strips(int cp) => set is null ? IsSpace(cp) : set.Contains(cp);

        var runes = Runes(s);
        var from = 0;
        var to = runes.Length;
        if (left) while (from < to && Strips(runes[from].Value)) from++;
        if (right) while (to > from && Strips(runes[to - 1].Value)) to--;
        return from == 0 && to == runes.Length ? s : string.Concat(runes[from..to].Select(r => r.ToString()));
    }

    private static string PyReplace(string s, string old, string replacement, long count)
    {
        if (old.Length == 0) throw new NotSupportedException("replace() with an empty pattern is not supported.");
        if (count < 0) return s.Replace(old, replacement, StringComparison.Ordinal);
        var sb = new StringBuilder();
        var start = 0;
        for (var n = 0; n < count; n++)
        {
            var at = s.IndexOf(old, start, StringComparison.Ordinal);
            if (at < 0) break;
            sb.Append(s, start, at - start).Append(replacement);
            start = at + old.Length;
        }
        return sb.Append(s, start, s.Length - start).ToString();
    }

    internal static object? CallGlobal(Builtin function, IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> kwargs)
    {
        switch (function.Name)
        {
            case "raise_exception":
                throw new TemplateException(ToStr(args.Count > 0 ? args[0] : ""), raisedByTemplate: true);
            case "namespace":
            {
                var ns = new Namespace();
                foreach (var arg in args)
                {
                    if (arg is not OrderedDictionary<string, object?> dict) throw new TemplateException("namespace() takes a mapping or names.");
                    foreach (var (k, v) in dict) ns.Values[k] = v;
                }
                foreach (var (k, v) in kwargs) ns.Values[k] = v;
                return ns;
            }
            case "range":
            {
                var (start, stop, step) = args.Count switch
                {
                    1 => (0L, AsLong(args[0]), 1L),
                    2 => (AsLong(args[0]), AsLong(args[1]), 1L),
                    3 => (AsLong(args[0]), AsLong(args[1]), AsLong(args[2])),
                    _ => throw new TemplateException("range() takes 1 to 3 numbers."),
                };
                if (step == 0) throw new TemplateException("range() step cannot be zero.");
                var items = new List<object?>();
                for (var i = start; step > 0 ? i < stop : i > stop; i += step)
                {
                    if (items.Count == 100_000) throw new TemplateException("range() is limited to 100000 items, as in jinja2's sandbox.");
                    items.Add(i);
                }
                return items;
            }
        }
        throw new NotSupportedException($"The function '{function.Name}' is not in JinjaLite's subset.");
    }

    // ---- tojson ----

    /// <summary>Python's json.dumps: ", " and ": " separators (", " becomes "," with an indent), floats as repr.</summary>
    internal static string ToJson(object? value, string? indent, bool sortKeys, bool ensureAscii)
    {
        var sb = new StringBuilder();
        Write(value, 0);
        return sb.ToString();

        void Write(object? v, int level)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : PyFloat(d));
                    break;
                case string s:
                    JsonString(sb, s, ensureAscii);
                    break;
                case List<object?> list:
                    Container('[', ']', list.Count, level, (i, l) => Write(list[i], l));
                    break;
                case object?[] tuple:
                    Container('[', ']', tuple.Length, level, (i, l) => Write(tuple[i], l));
                    break;
                case OrderedDictionary<string, object?> dict:
                {
                    var pairs = sortKeys ? dict.OrderBy(p => p.Key, StringComparer.Ordinal).ToList() : dict.ToList();
                    Container('{', '}', pairs.Count, level, (i, l) =>
                    {
                        JsonString(sb, pairs[i].Key, ensureAscii);
                        sb.Append(": ");
                        Write(pairs[i].Value, l);
                    });
                    break;
                }
                default:
                    throw new TemplateException($"Object of type {Kind(v)} is not JSON serializable.");
            }
        }

        void Container(char open, char close, int count, int level, Action<int, int> item)
        {
            sb.Append(open);
            if (count == 0)
            {
                sb.Append(close);
                return;
            }
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(indent is null ? ", " : ",");
                if (indent is not null) sb.Append('\n').Append(string.Concat(Enumerable.Repeat(indent, level + 1)));
                item(i, level + 1);
            }
            if (indent is not null) sb.Append('\n').Append(string.Concat(Enumerable.Repeat(indent, level)));
            sb.Append(close);
        }
    }

    private static void JsonString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ensureAscii && c > 0x7E)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
