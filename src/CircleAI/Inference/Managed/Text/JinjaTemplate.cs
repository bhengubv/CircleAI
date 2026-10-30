// JinjaTemplate.cs
//
// JinjaLite: the Jinja subset the Bonsai 2 fleet's chat templates are written in, read and
// rendered the way Hugging Face renders a chat template - jinja2's sandbox with trim_blocks
// and lstrip_blocks, the loopcontrols extension, tojson as json.dumps(ensure_ascii=False),
// raise_exception. The model's own template (tokenizer.chat_template) is the prompt format,
// so this replaces Scriban on the managed path. Its ruler is
// fixtures/bonsai2/2B/templates.json: jinja2 3.1.6's own renders, matched byte for byte,
// refusals included.
//
// What it reads, structured as jinja2's lexer and parser so each rule maps to theirs:
//   text with {{ }}, {% %} and {# #}, their - and + whitespace marks, a final newline dropped;
//   if / elif / else, for (tuple targets, an if filter, else, the loop variable), set (names,
//   namespace attributes, tuples, captured blocks), macro with defaults, break, continue;
//   literals, names, attributes, items and slices, calls, filters, tests, the conditional
//   expression, and Python's operators at jinja2's precedence.
// Filters, tests, methods and functions outside JinjaValues' lists are refused when the
// template is parsed, never skipped when it runs.

using System.Text;
using System.Text.Json;
using static CircleAI.Inference.Managed.Text.JinjaValues;

namespace CircleAI.Inference.Managed.Text;

/// <summary>A template failed: it refused its input (raise_exception), or met a value it cannot use.</summary>
public sealed class TemplateException(string message, bool raisedByTemplate = false) : Exception(message)
{
    /// <summary>True when the template itself refused, through raise_exception.</summary>
    public bool RaisedByTemplate { get; } = raisedByTemplate;
}

/// <summary>A chat template in the Jinja subset the fleet's templates use, rendered as Hugging Face renders it.</summary>
public sealed class JinjaTemplate
{
    private readonly List<Node> _body;

    private JinjaTemplate(List<Node> body) => _body = body;

    /// <summary>
    /// Reads <paramref name="source"/>. A syntax error is a <see cref="TemplateException"/>; anything
    /// outside the subset is a <see cref="NotSupportedException"/>, so a template is accepted only
    /// when every part of it can be rendered.
    /// </summary>
    public static JinjaTemplate Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(Lex(source));
        return new JinjaTemplate(parser.ParseTemplate());
    }

    /// <summary>
    /// Renders with <paramref name="variables"/> - strings, numbers, bools, null, lists and
    /// string-keyed dictionaries (or <see cref="JsonElement"/>s) - as jinja2 would.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var root = new Scope(null);
        foreach (var name in JinjaValues.Globals) root.Set(name, new Builtin(name));
        foreach (var (name, value) in variables) root.Set(name, Normalize(value));
        var output = new StringBuilder();
        new Interpreter(root).Run(_body, root, output);
        return output.ToString();
    }

    /// <summary>A JSON value as the template sees it: Python's json.loads (a number with a point is a float).</summary>
    public static object? FromJson(JsonElement element) => JinjaValues.FromJson(element);

    // ======================================================================================
    // Lexer - jinja2's lexer.py: text, then tags; inside a tag, tokens until its end, which
    // counts only when brackets balance.
    // ======================================================================================

    private enum Kind { Text, VarBegin, VarEnd, BlockBegin, BlockEnd, Name, String, Integer, Float, Operator, End }

    private readonly record struct Token(Kind Kind, string Value, int Line);

    private static readonly string[] Operators =
        ["//", "**", "==", "!=", "<=", ">=", "+", "-", "/", "*", "%", "~", "(", ")", "[", "]", "{", "}", ",", ".", ":", "|", "=", "<", ">", ";"];

    private static List<Token> Lex(string source)
    {
        source = source.Replace("\r\n", "\n").Replace('\r', '\n');
        if (source.EndsWith('\n')) source = source[..^1]; // keep_trailing_newline is off

        var tokens = new List<Token>();
        var pos = 0;
        var line = 1;
        var lineStarting = true;
        while (true)
        {
            var tag = NextTag(source, pos);
            var text = source[pos..(tag < 0 ? source.Length : tag)];
            if (tag < 0)
            {
                if (text.Length > 0) tokens.Add(new Token(Kind.Text, text, line));
                break;
            }

            var kind = source[tag + 1];
            var sign = tag + 2 < source.Length && source[tag + 2] is '-' or '+' ? source[tag + 2] : '\0';
            if (sign == '-')
            {
                var end = text.Length; // all whitespace between the text and the tag (Python's rstrip)
                while (end > 0 && IsSpace(text[end - 1])) end--;
                text = text[..end];
            }
            else if (sign != '+' && kind != '{')
            {
                // lstrip_blocks: a block or comment alone on its line takes the line's indent with it.
                var lineStart = text.LastIndexOf('\n') + 1;
                if ((lineStart > 0 || lineStarting) && lineStart < text.Length && text[lineStart..].All(c => IsSpace(c)))
                    text = text[..lineStart];
            }
            if (text.Length > 0) tokens.Add(new Token(Kind.Text, text, line));
            line += source.AsSpan(pos, tag - pos).Count('\n');
            pos = tag + 2 + (sign == '\0' ? 0 : 1);

            if (kind == '#')
            {
                var close = source.IndexOf("#}", pos, StringComparison.Ordinal);
                if (close < 0) throw Syntax("a comment is never closed", line);
                line += source.AsSpan(pos, close - pos).Count('\n');
                var strip = close > pos && source[close - 1] == '-';
                pos = close + 2;
                lineStarting = AfterTagEnd(source, ref pos, ref line, strip, trimBlocks: true);
                continue;
            }

            var isVariable = kind == '{';
            tokens.Add(new Token(isVariable ? Kind.VarBegin : Kind.BlockBegin, "", line));
            var closer = isVariable ? "}}" : "%}";
            var brackets = new Stack<char>();
            while (true)
            {
                while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                {
                    if (source[pos] == '\n') line++;
                    pos++;
                }
                if (pos >= source.Length) throw Syntax(isVariable ? "a {{ is never closed" : "a {% is never closed", line);

                if (brackets.Count == 0)
                {
                    var strip = source[pos] == '-' && string.CompareOrdinal(source, pos + 1, closer, 0, 2) == 0;
                    if (strip || string.CompareOrdinal(source, pos, closer, 0, 2) == 0)
                    {
                        tokens.Add(new Token(isVariable ? Kind.VarEnd : Kind.BlockEnd, "", line));
                        pos += strip ? 3 : 2;
                        lineStarting = AfterTagEnd(source, ref pos, ref line, strip, trimBlocks: !isVariable);
                        break;
                    }
                }

                var c = source[pos];
                if (c is '\'' or '"')
                {
                    tokens.Add(new Token(Kind.String, ReadString(source, ref pos, ref line), line));
                }
                else if (char.IsAsciiDigit(c))
                {
                    tokens.Add(ReadNumber(source, ref pos, line));
                }
                else if (char.IsAsciiLetter(c) || c == '_')
                {
                    var start = pos;
                    while (pos < source.Length && (char.IsAsciiLetterOrDigit(source[pos]) || source[pos] == '_')) pos++;
                    tokens.Add(new Token(Kind.Name, source[start..pos], line));
                }
                else
                {
                    var op = Operators.FirstOrDefault(o => string.CompareOrdinal(source, pos, o, 0, o.Length) == 0)
                        ?? throw Syntax($"unexpected character '{c}'", line);
                    if (op is "(" or "[" or "{") brackets.Push(op == "(" ? ')' : op == "[" ? ']' : '}');
                    else if (op is ")" or "]" or "}")
                    {
                        if (brackets.Count == 0 || brackets.Pop() != op[0]) throw Syntax($"unexpected '{op}'", line);
                    }
                    tokens.Add(new Token(Kind.Operator, op, line));
                    pos += op.Length;
                }
            }
        }
        tokens.Add(new Token(Kind.End, "", line));
        return tokens;
    }

    private static int NextTag(string source, int from)
    {
        for (var i = source.IndexOf('{', from); i >= 0 && i + 1 < source.Length; i = source.IndexOf('{', i + 1))
        {
            if (source[i + 1] is '{' or '%' or '#') return i;
        }
        return -1;
    }

    // After a tag's end: "-" strips every following whitespace; otherwise trim_blocks takes one
    // newline after a block or comment. Says whether the next text starts a line.
    private static bool AfterTagEnd(string source, ref int pos, ref int line, bool strip, bool trimBlocks)
    {
        if (strip)
        {
            var start = pos;
            while (pos < source.Length && IsSpace(source[pos]))
            {
                if (source[pos] == '\n') line++;
                pos++;
            }
            return pos > start && source[pos - 1] == '\n';
        }
        if (trimBlocks && pos < source.Length && source[pos] == '\n')
        {
            pos++;
            line++;
            return true;
        }
        return false;
    }

    // A string literal, its escapes decoded as Python's unicode-escape decodes them.
    private static string ReadString(string source, ref int pos, ref int line)
    {
        var quote = source[pos++];
        var sb = new StringBuilder();
        while (true)
        {
            if (pos >= source.Length) throw Syntax("a string is never closed", line);
            var c = source[pos];
            if (c == quote)
            {
                pos++;
                return sb.ToString();
            }
            if (c == '\n') line++;
            if (c != '\\' || pos + 1 >= source.Length)
            {
                sb.Append(c);
                pos++;
                continue;
            }

            var e = source[pos + 1];
            pos += 2;
            switch (e)
            {
                case '\n': line++; break; // a backslash at the end of a line joins it to the next
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'x': sb.Append(char.ConvertFromUtf32(Hex(source, ref pos, 2, line))); break;
                case 'u': sb.Append(char.ConvertFromUtf32(Hex(source, ref pos, 4, line))); break;
                case 'U': sb.Append(char.ConvertFromUtf32(Hex(source, ref pos, 8, line))); break;
                case >= '0' and <= '7':
                {
                    var value = e - '0';
                    for (var n = 0; n < 2 && pos < source.Length && source[pos] is >= '0' and <= '7'; n++) value = value * 8 + (source[pos++] - '0');
                    sb.Append((char)value);
                    break;
                }
                case 'N':
                    throw new NotSupportedException($"\\N{{...}} escapes (line {line}) are not in JinjaLite's subset.");
                default:
                    sb.Append('\\').Append(e); // Python keeps an escape it does not know
                    break;
            }
        }
    }

    private static int Hex(string source, ref int pos, int digits, int line)
    {
        if (pos + digits > source.Length || !int.TryParse(source.AsSpan(pos, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var value))
            throw Syntax($"a \\x, \\u or \\U escape needs {digits} hex digits", line);
        pos += digits;
        return value;
    }

    private static Token ReadNumber(string source, ref int pos, int line)
    {
        var start = pos;
        Digits(source, ref pos);
        var isFloat = false;
        if (pos + 1 < source.Length && source[pos] == '.' && char.IsAsciiDigit(source[pos + 1]))
        {
            pos++;
            Digits(source, ref pos);
            isFloat = true;
        }
        if (pos < source.Length && source[pos] is 'e' or 'E')
        {
            var at = pos + 1;
            if (at < source.Length && source[at] is '+' or '-') at++;
            if (at < source.Length && char.IsAsciiDigit(source[at]))
            {
                pos = at;
                Digits(source, ref pos);
                isFloat = true;
            }
        }
        return new Token(isFloat ? Kind.Float : Kind.Integer, source[start..pos].Replace("_", ""), line);
    }

    private static void Digits(string source, ref int pos)
    {
        while (pos < source.Length && (char.IsAsciiDigit(source[pos]) || (source[pos] == '_' && pos + 1 < source.Length && char.IsAsciiDigit(source[pos + 1])))) pos++;
    }

    private static TemplateException Syntax(string what, int line) => new($"Template syntax error on line {line}: {what}.");

    // ======================================================================================
    // Syntax tree
    // ======================================================================================

    private abstract record Node;
    private sealed record TextNode(string Text) : Node;
    private sealed record OutputNode(Expr Value) : Node;
    private sealed record IfNode(List<(Expr Test, List<Node> Body)> Branches, List<Node>? Else) : Node;
    private sealed record ForNode(Target Target, Expr Iterable, Expr? Filter, List<Node> Body, List<Node>? Else) : Node;
    private sealed record SetNode(Target Target, Expr? Value, List<Node>? Block) : Node;
    private sealed record MacroNode(string Name, string[] Parameters, Expr?[] Defaults, List<Node> Body) : Node;
    private sealed record BreakNode : Node;
    private sealed record ContinueNode : Node;

    private abstract record Target;
    private sealed record NameTarget(string Name) : Target;
    private sealed record AttributeTarget(string Namespace, string Attribute) : Target;
    private sealed record TupleTarget(Target[] Items) : Target;

    private abstract record Expr;
    private sealed record Const(object? Value) : Expr;
    private sealed record NameRef(string Name) : Expr;
    private sealed record AttrExpr(Expr Target, string Name) : Expr;
    private sealed record ItemExpr(Expr Target, Expr Key) : Expr;
    private sealed record SliceExpr(Expr Target, Expr? Start, Expr? Stop, Expr? Step) : Expr;
    private sealed record CallExpr(Expr Callee, Expr[] Args, (string Name, Expr Value)[] Kwargs) : Expr;
    private sealed record FilterExpr(Expr Value, string Name, Expr[] Args, (string Name, Expr Value)[] Kwargs) : Expr;
    private sealed record TestExpr(Expr Value, string Name, bool Negated) : Expr;
    private sealed record UnaryExpr(string Op, Expr Operand) : Expr;
    private sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;
    private sealed record CompareExpr(Expr First, (string Op, Expr Right)[] Rest) : Expr;
    private sealed record CondExpr(Expr Test, Expr Then, Expr? Else) : Expr;
    private sealed record ListExpr(Expr[] Items, bool IsTuple) : Expr;
    private sealed record DictExpr((Expr Key, Expr Value)[] Pairs) : Expr;

    // ======================================================================================
    // Parser - jinja2's parser.py, method for method, for the subset.
    // ======================================================================================

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;
        private readonly HashSet<string> _macros = new(StringComparer.Ordinal);
        private readonly List<(string Name, int Line)> _calledNames = [];

        private Token Current => tokens[_at];

        private Token Peek => tokens[Math.Min(_at + 1, tokens.Count - 1)];

        private bool IsName(string value) => Current.Kind == Kind.Name && Current.Value == value;

        private bool IsOp(string value) => Current.Kind == Kind.Operator && Current.Value == value;

        private bool SkipName(string value)
        {
            if (!IsName(value)) return false;
            _at++;
            return true;
        }

        private bool SkipOp(string value)
        {
            if (!IsOp(value)) return false;
            _at++;
            return true;
        }

        private Token Expect(Kind kind, string? value = null)
        {
            var t = Current;
            if (t.Kind != kind || (value is not null && t.Value != value))
                throw Syntax($"expected {value ?? kind.ToString()}, found {(t.Value.Length > 0 ? t.Value : t.Kind.ToString())}", t.Line);
            _at++;
            return t;
        }

        public List<Node> ParseTemplate()
        {
            var body = ParseBody();
            Expect(Kind.End);
            foreach (var (name, line) in _calledNames)
            {
                if (JinjaValues.RefusedGlobals.Contains(name) && !_macros.Contains(name))
                    throw new NotSupportedException($"The function '{name}' (line {line}) is not in JinjaLite's subset.");
            }
            return body;
        }

        // Nodes until a block tag named in endTags, which is left for the caller.
        private List<Node> ParseBody(params string[] endTags)
        {
            var nodes = new List<Node>();
            while (true)
            {
                var t = Current;
                switch (t.Kind)
                {
                    case Kind.End:
                        if (endTags.Length > 0) throw Syntax($"the template ends where {string.Join(" or ", endTags)} was expected", t.Line);
                        return nodes;
                    case Kind.Text:
                        nodes.Add(new TextNode(t.Value));
                        _at++;
                        break;
                    case Kind.VarBegin:
                        _at++;
                        nodes.Add(new OutputNode(ParseTuple()));
                        Expect(Kind.VarEnd);
                        break;
                    case Kind.BlockBegin:
                        if (Peek.Kind == Kind.Name && endTags.Contains(Peek.Value)) return nodes;
                        _at++;
                        nodes.Add(ParseStatement());
                        break;
                    default:
                        throw Syntax($"unexpected {t.Kind}", t.Line);
                }
            }
        }

        private void ExpectEndTag(string name)
        {
            Expect(Kind.BlockBegin);
            Expect(Kind.Name, name);
            Expect(Kind.BlockEnd);
        }

        private Node ParseStatement()
        {
            var keyword = Expect(Kind.Name);
            switch (keyword.Value)
            {
                case "if":
                    return ParseIf();
                case "for":
                    return ParseFor();
                case "set":
                    return ParseSet();
                case "macro":
                    return ParseMacro();
                case "break":
                    Expect(Kind.BlockEnd);
                    return new BreakNode();
                case "continue":
                    Expect(Kind.BlockEnd);
                    return new ContinueNode();
                case "elif" or "else" or "endif" or "endfor" or "endset" or "endmacro":
                    throw Syntax($"'{keyword.Value}' with nothing open to close", keyword.Line);
                default:
                    throw new NotSupportedException($"The statement '{keyword.Value}' (line {keyword.Line}) is not in JinjaLite's subset.");
            }
        }

        private IfNode ParseIf()
        {
            var branches = new List<(Expr, List<Node>)>();
            var test = ParseTuple(withCondExpr: false);
            Expect(Kind.BlockEnd);
            while (true)
            {
                branches.Add((test, ParseBody("elif", "else", "endif")));
                Expect(Kind.BlockBegin);
                var tag = Expect(Kind.Name).Value;
                if (tag == "elif")
                {
                    test = ParseTuple(withCondExpr: false);
                    Expect(Kind.BlockEnd);
                    continue;
                }
                List<Node>? elseBody = null;
                if (tag == "else")
                {
                    Expect(Kind.BlockEnd);
                    elseBody = ParseBody("endif");
                    ExpectEndTag("endif");
                }
                else
                {
                    Expect(Kind.BlockEnd);
                }
                return new IfNode(branches, elseBody);
            }
        }

        private ForNode ParseFor()
        {
            var target = ParseAssignTarget(withNamespace: false, extraEnd: "in");
            Expect(Kind.Name, "in");
            var iterable = ParseTuple(withCondExpr: false, extraEnd: "recursive");
            var filter = SkipName("if") ? ParseExpression() : null;
            if (IsName("recursive")) throw new NotSupportedException($"Recursive loops (line {Current.Line}) are not in JinjaLite's subset.");
            Expect(Kind.BlockEnd);
            var body = ParseBody("endfor", "else");
            List<Node>? elseBody = null;
            Expect(Kind.BlockBegin);
            if (Expect(Kind.Name).Value == "else")
            {
                Expect(Kind.BlockEnd);
                elseBody = ParseBody("endfor");
                ExpectEndTag("endfor");
            }
            else
            {
                Expect(Kind.BlockEnd);
            }
            return new ForNode(target, iterable, filter, body, elseBody);
        }

        private SetNode ParseSet()
        {
            var target = ParseAssignTarget(withNamespace: true, extraEnd: null);
            if (SkipOp("="))
            {
                var value = ParseTuple();
                Expect(Kind.BlockEnd);
                return new SetNode(target, value, null);
            }
            if (IsOp("|")) throw new NotSupportedException($"A filtered set block (line {Current.Line}) is not in JinjaLite's subset.");
            Expect(Kind.BlockEnd);
            var block = ParseBody("endset");
            ExpectEndTag("endset");
            return new SetNode(target, null, block);
        }

        private MacroNode ParseMacro()
        {
            var name = Expect(Kind.Name).Value;
            var parameters = new List<string>();
            var defaults = new List<Expr?>();
            Expect(Kind.Operator, "(");
            while (!IsOp(")"))
            {
                if (parameters.Count > 0) Expect(Kind.Operator, ",");
                parameters.Add(Expect(Kind.Name).Value);
                if (SkipOp("=")) defaults.Add(ParseExpression());
                else if (defaults.Any(d => d is not null)) throw Syntax("a parameter without a default follows one with a default", Current.Line);
                else defaults.Add(null);
            }
            Expect(Kind.Operator, ")");
            Expect(Kind.BlockEnd);
            var body = ParseBody("endmacro");
            ExpectEndTag("endmacro");
            _macros.Add(name);
            return new MacroNode(name, [.. parameters], [.. defaults], body);
        }

        private Target ParseAssignTarget(bool withNamespace, string? extraEnd)
        {
            if (withNamespace && Current.Kind == Kind.Name && Peek.Kind == Kind.Operator && Peek.Value == ".")
            {
                var ns = Expect(Kind.Name).Value;
                _at++; // the dot
                return new AttributeTarget(ns, Expect(Kind.Name).Value);
            }
            var names = new List<Target>();
            var isTuple = false;
            while (true)
            {
                if (names.Count > 0) Expect(Kind.Operator, ",");
                if (IsTupleEnd(extraEnd)) break;
                names.Add(new NameTarget(Expect(Kind.Name).Value));
                if (IsOp(",")) isTuple = true;
                else break;
            }
            if (names.Count == 0) throw Syntax("expected a name to assign to", Current.Line);
            return isTuple ? new TupleTarget([.. names]) : names[0];
        }

        private bool IsTupleEnd(string? extraEnd) =>
            Current.Kind is Kind.VarEnd or Kind.BlockEnd or Kind.End || IsOp(")") || (extraEnd is not null && IsName(extraEnd));

        private Expr ParseTuple(bool withCondExpr = true, string? extraEnd = null, bool explicitParentheses = false)
        {
            var items = new List<Expr>();
            var isTuple = false;
            while (true)
            {
                if (items.Count > 0) Expect(Kind.Operator, ",");
                if (IsTupleEnd(extraEnd)) break;
                items.Add(withCondExpr ? ParseExpression() : ParseOr());
                if (IsOp(",")) isTuple = true;
                else break;
            }
            if (!isTuple)
            {
                if (items.Count > 0) return items[0];
                if (!explicitParentheses) throw Syntax($"expected an expression, found {Current.Kind}", Current.Line);
            }
            return new ListExpr([.. items], IsTuple: true);
        }

        private Expr ParseExpression()
        {
            var expr = ParseOr();
            while (SkipName("if"))
            {
                var test = ParseOr();
                var otherwise = SkipName("else") ? ParseExpression() : null;
                expr = new CondExpr(test, expr, otherwise);
            }
            return expr;
        }

        private Expr ParseOr()
        {
            var left = ParseAnd();
            while (SkipName("or")) left = new BinaryExpr("or", left, ParseAnd());
            return left;
        }

        private Expr ParseAnd()
        {
            var left = ParseNot();
            while (SkipName("and")) left = new BinaryExpr("and", left, ParseNot());
            return left;
        }

        private Expr ParseNot() => SkipName("not") ? new UnaryExpr("not", ParseNot()) : ParseCompare();

        private Expr ParseCompare()
        {
            var first = ParseMath1();
            var rest = new List<(string, Expr)>();
            while (true)
            {
                if (Current.Kind == Kind.Operator && Current.Value is "==" or "!=" or "<" or "<=" or ">" or ">=")
                {
                    var op = Current.Value;
                    _at++;
                    rest.Add((op, ParseMath1()));
                }
                else if (SkipName("in"))
                {
                    rest.Add(("in", ParseMath1()));
                }
                else if (IsName("not") && Peek.Kind == Kind.Name && Peek.Value == "in")
                {
                    _at += 2;
                    rest.Add(("not in", ParseMath1()));
                }
                else
                {
                    break;
                }
            }
            return rest.Count == 0 ? first : new CompareExpr(first, [.. rest]);
        }

        private Expr ParseMath1()
        {
            var left = ParseConcat();
            while (Current.Kind == Kind.Operator && Current.Value is "+" or "-")
            {
                var op = Current.Value;
                _at++;
                left = new BinaryExpr(op, left, ParseConcat());
            }
            return left;
        }

        private Expr ParseConcat()
        {
            var left = ParseMath2();
            while (SkipOp("~")) left = new BinaryExpr("~", left, ParseMath2());
            return left;
        }

        private Expr ParseMath2()
        {
            var left = ParsePow();
            while (Current.Kind == Kind.Operator && Current.Value is "*" or "/" or "//" or "%")
            {
                var op = Current.Value;
                _at++;
                left = new BinaryExpr(op, left, ParsePow());
            }
            return left;
        }

        private Expr ParsePow()
        {
            var left = ParseUnary();
            while (SkipOp("**")) left = new BinaryExpr("**", left, ParseUnary());
            return left;
        }

        private Expr ParseUnary(bool withFilter = true)
        {
            Expr node;
            if (SkipOp("-")) node = new UnaryExpr("-", ParseUnary(false));
            else if (SkipOp("+")) node = new UnaryExpr("+", ParseUnary(false));
            else node = ParsePrimary();
            node = ParsePostfix(node);
            return withFilter ? ParseFilterExpr(node) : node;
        }

        private Expr ParsePrimary()
        {
            var t = Current;
            switch (t.Kind)
            {
                case Kind.Name:
                    _at++;
                    return t.Value switch
                    {
                        "true" or "True" => new Const(true),
                        "false" or "False" => new Const(false),
                        "none" or "None" => new Const(null),
                        _ => new NameRef(t.Value),
                    };
                case Kind.String:
                {
                    var sb = new StringBuilder();
                    while (Current.Kind == Kind.String) sb.Append(tokens[_at++].Value); // adjacent literals join
                    return new Const(sb.ToString());
                }
                case Kind.Integer:
                    _at++;
                    return new Const(long.Parse(t.Value, System.Globalization.CultureInfo.InvariantCulture));
                case Kind.Float:
                    _at++;
                    return new Const(double.Parse(t.Value, System.Globalization.CultureInfo.InvariantCulture));
                case Kind.Operator when t.Value == "(":
                {
                    _at++;
                    var inner = ParseTuple(explicitParentheses: true);
                    Expect(Kind.Operator, ")");
                    return inner;
                }
                case Kind.Operator when t.Value == "[":
                {
                    _at++;
                    var items = new List<Expr>();
                    while (!IsOp("]"))
                    {
                        if (items.Count > 0) Expect(Kind.Operator, ",");
                        if (IsOp("]")) break;
                        items.Add(ParseExpression());
                    }
                    Expect(Kind.Operator, "]");
                    return new ListExpr([.. items], IsTuple: false);
                }
                case Kind.Operator when t.Value == "{":
                {
                    _at++;
                    var pairs = new List<(Expr, Expr)>();
                    while (!IsOp("}"))
                    {
                        if (pairs.Count > 0) Expect(Kind.Operator, ",");
                        if (IsOp("}")) break;
                        var key = ParseExpression();
                        Expect(Kind.Operator, ":");
                        pairs.Add((key, ParseExpression()));
                    }
                    Expect(Kind.Operator, "}");
                    return new DictExpr([.. pairs]);
                }
                default:
                    throw Syntax($"unexpected {(t.Value.Length > 0 ? t.Value : t.Kind.ToString())}", t.Line);
            }
        }

        private Expr ParsePostfix(Expr node)
        {
            while (true)
            {
                if (IsOp(".") || IsOp("[")) node = ParseSubscript(node);
                else if (IsOp("(")) node = ParseCall(node);
                else return node;
            }
        }

        private Expr ParseFilterExpr(Expr node)
        {
            while (true)
            {
                if (IsOp("|")) node = ParseFilter(node);
                else if (IsName("is")) node = ParseTest(node);
                else if (IsOp("(")) node = ParseCall(node);
                else return node;
            }
        }

        private Expr ParseSubscript(Expr node)
        {
            var t = tokens[_at++];
            if (t.Value == ".")
            {
                var attr = tokens[_at++];
                if (attr.Kind == Kind.Name) return new AttrExpr(node, attr.Value);
                if (attr.Kind == Kind.Integer) return new ItemExpr(node, new Const(long.Parse(attr.Value, System.Globalization.CultureInfo.InvariantCulture)));
                throw Syntax("expected a name or a number after '.'", attr.Line);
            }

            var args = new List<Expr>();
            while (!IsOp("]"))
            {
                if (args.Count > 0) Expect(Kind.Operator, ",");
                args.Add(ParseSubscribed(node));
            }
            Expect(Kind.Operator, "]");
            if (args.Count != 1) throw new NotSupportedException($"Tuple subscripts (line {t.Line}) are not in JinjaLite's subset.");
            return args[0] is SliceExpr ? args[0] : new ItemExpr(node, args[0]);
        }

        // One subscript: an expression, or a slice start:stop:step with any part left out.
        private Expr ParseSubscribed(Expr target)
        {
            Expr? start = null;
            if (!IsOp(":"))
            {
                var node = ParseExpression();
                if (!IsOp(":")) return node;
                start = node;
            }
            _at++; // the first colon
            Expr? stop = IsOp(":") || IsOp("]") || IsOp(",") ? null : ParseExpression();
            Expr? step = null;
            if (SkipOp(":") && !(IsOp("]") || IsOp(","))) step = ParseExpression();
            return new SliceExpr(target, start, stop, step);
        }

        private CallExpr ParseCall(Expr callee)
        {
            var (args, kwargs) = ParseCallArgs();
            switch (callee)
            {
                case NameRef name:
                    _calledNames.Add((name.Name, Current.Line));
                    break;
                case AttrExpr attr when !JinjaValues.Methods.Contains(attr.Name):
                    throw new NotSupportedException($"The method '{attr.Name}' (line {Current.Line}) is not in JinjaLite's subset.");
            }
            return new CallExpr(callee, args, kwargs);
        }

        private (Expr[] Args, (string, Expr)[] Kwargs) ParseCallArgs()
        {
            Expect(Kind.Operator, "(");
            var args = new List<Expr>();
            var kwargs = new List<(string, Expr)>();
            var needComma = false;
            while (!IsOp(")"))
            {
                if (needComma)
                {
                    Expect(Kind.Operator, ",");
                    if (IsOp(")")) break; // a trailing comma
                }
                if (IsOp("*") || IsOp("**")) throw new NotSupportedException($"*args and **kwargs (line {Current.Line}) are not in JinjaLite's subset.");
                if (Current.Kind == Kind.Name && Peek.Kind == Kind.Operator && Peek.Value == "=")
                {
                    var key = Current.Value;
                    _at += 2;
                    kwargs.Add((key, ParseExpression()));
                }
                else
                {
                    if (kwargs.Count > 0) throw Syntax("a positional argument follows a keyword argument", Current.Line);
                    args.Add(ParseExpression());
                }
                needComma = true;
            }
            Expect(Kind.Operator, ")");
            return ([.. args], [.. kwargs]);
        }

        private Expr ParseFilter(Expr node)
        {
            while (SkipOp("|"))
            {
                var name = Expect(Kind.Name);
                if (!JinjaValues.Filters.Contains(name.Value))
                    throw new NotSupportedException($"The filter '{name.Value}' (line {name.Line}) is not in JinjaLite's subset.");
                var (args, kwargs) = IsOp("(") ? ParseCallArgs() : ([], []);
                node = new FilterExpr(node, name.Value, args, kwargs);
            }
            return node;
        }

        private Expr ParseTest(Expr node)
        {
            _at++; // "is"
            var negated = SkipName("not");
            var name = Expect(Kind.Name);
            if (!JinjaValues.Tests.Contains(name.Value))
                throw new NotSupportedException($"The test '{name.Value}' (line {name.Line}) is not in JinjaLite's subset.");
            // A test that takes an argument (is divisibleby 3, is sameas x) is not in the subset;
            // jinja2 would read one here, so anything that could start one is refused.
            var startsArgument = IsOp("(") || IsOp("[") || IsOp("{")
                || Current.Kind is Kind.String or Kind.Integer or Kind.Float
                || (Current.Kind == Kind.Name && Current.Value is not ("else" or "or" or "and"));
            if (startsArgument) throw new NotSupportedException($"A test with an argument (line {name.Line}) is not in JinjaLite's subset.");
            return new TestExpr(node, name.Value, negated);
        }
    }

    // ======================================================================================
    // Interpreter - scopes as jinja2 has them: a for loop's body and a macro's body are their
    // own; if blocks are not. A macro sees the template's top level, never its caller's locals.
    // ======================================================================================

    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public bool TryGet(string name, out object? value)
        {
            for (var s = this; s is not null; s = s.Parent)
            {
                if (s._values.TryGetValue(name, out value)) return true;
            }
            value = null;
            return false;
        }

        public void Set(string name, object? value) => _values[name] = value;

        public Scope? Parent { get; } = parent;
    }

    private sealed record Macro(MacroNode Node);

    private enum Flow { Normal, Break, Continue }

    private sealed class Interpreter(Scope root)
    {
        public void Run(List<Node> body, Scope scope, StringBuilder output)
        {
            if (Exec(body, scope, output) != Flow.Normal) throw new TemplateException("break or continue outside a loop.");
        }

        private Flow Exec(List<Node> nodes, Scope scope, StringBuilder output)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case TextNode text:
                        output.Append(text.Text);
                        break;
                    case OutputNode o:
                        output.Append(ToStr(Eval(o.Value, scope)));
                        break;
                    case IfNode branch:
                    {
                        var taken = false;
                        foreach (var (test, body) in branch.Branches)
                        {
                            if (!Truthy(Eval(test, scope))) continue;
                            var flow = Exec(body, scope, output);
                            if (flow != Flow.Normal) return flow;
                            taken = true;
                            break;
                        }
                        if (!taken && branch.Else is not null)
                        {
                            var flow = Exec(branch.Else, scope, output);
                            if (flow != Flow.Normal) return flow;
                        }
                        break;
                    }
                    case ForNode loop:
                        ExecFor(loop, scope, output);
                        break;
                    case SetNode set:
                    {
                        object? value;
                        if (set.Value is not null)
                        {
                            value = Eval(set.Value, scope);
                        }
                        else
                        {
                            var captured = new StringBuilder();
                            Run(set.Block!, scope, captured);
                            value = captured.ToString();
                        }
                        Assign(set.Target, value, scope);
                        break;
                    }
                    case MacroNode macro:
                        scope.Set(macro.Name, new Macro(macro));
                        break;
                    case BreakNode:
                        return Flow.Break;
                    case ContinueNode:
                        return Flow.Continue;
                }
            }
            return Flow.Normal;
        }

        private void ExecFor(ForNode loop, Scope scope, StringBuilder output)
        {
            var items = Iterate(Eval(loop.Iterable, scope)).ToList();
            if (loop.Filter is not null)
            {
                items = items.Where(item =>
                {
                    var test = new Scope(scope);
                    Assign(loop.Target, item, test);
                    return Truthy(Eval(loop.Filter, test));
                }).ToList();
            }

            if (items.Count == 0)
            {
                if (loop.Else is not null) Run(loop.Else, scope, output);
                return;
            }
            for (var i = 0; i < items.Count; i++)
            {
                var body = new Scope(scope);
                Assign(loop.Target, items[i], body);
                body.Set("loop", new LoopInfo(items, i));
                if (Exec(loop.Body, body, output) == Flow.Break) break;
            }
        }

        private static void Assign(Target target, object? value, Scope scope)
        {
            switch (target)
            {
                case NameTarget name:
                    scope.Set(name.Name, value);
                    break;
                case AttributeTarget attr:
                    if (!scope.TryGet(attr.Namespace, out var ns) || ns is not Namespace space)
                        throw new TemplateException($"'{attr.Namespace}' is not a namespace; only a namespace's attributes can be assigned.");
                    space.Values[attr.Attribute] = value;
                    break;
                case TupleTarget tuple:
                {
                    var items = value is List<object?> list ? list : Iterate(value).ToList();
                    if (items.Count != tuple.Items.Length)
                        throw new TemplateException($"Cannot unpack {items.Count} values into {tuple.Items.Length} names.");
                    for (var i = 0; i < items.Count; i++) Assign(tuple.Items[i], items[i], scope);
                    break;
                }
            }
        }

        private object? Eval(Expr expr, Scope scope)
        {
            switch (expr)
            {
                case Const c:
                    return c.Value;
                case NameRef name:
                    return scope.TryGet(name.Name, out var value) ? value : new Undefined(name.Name);
                case AttrExpr attr:
                    return GetAttr(Eval(attr.Target, scope), attr.Name);
                case ItemExpr item:
                    return GetItem(Eval(item.Target, scope), Eval(item.Key, scope));
                case SliceExpr slice:
                    return Slice(Eval(slice.Target, scope),
                        slice.Start is null ? null : Eval(slice.Start, scope),
                        slice.Stop is null ? null : Eval(slice.Stop, scope),
                        slice.Step is null ? null : Eval(slice.Step, scope));
                case CallExpr call:
                    return Call(Eval(call.Callee, scope), call.Args.Select(a => Eval(a, scope)).ToList(),
                        call.Kwargs.ToDictionary(k => k.Name, k => Eval(k.Value, scope), StringComparer.Ordinal));
                case FilterExpr filter:
                    return Filter(filter.Name, Eval(filter.Value, scope), filter.Args.Select(a => Eval(a, scope)).ToList(),
                        filter.Kwargs.ToDictionary(k => k.Name, k => Eval(k.Value, scope), StringComparer.Ordinal));
                case TestExpr test:
                    return Test(test.Name, Eval(test.Value, scope)) != test.Negated;
                case UnaryExpr unary:
                    return unary.Op == "not" ? !Truthy(Eval(unary.Operand, scope)) : Unary(unary.Op, Eval(unary.Operand, scope));
                case BinaryExpr binary when binary.Op == "and":
                {
                    var left = Eval(binary.Left, scope);
                    return Truthy(left) ? Eval(binary.Right, scope) : left;
                }
                case BinaryExpr binary when binary.Op == "or":
                {
                    var left = Eval(binary.Left, scope);
                    return Truthy(left) ? left : Eval(binary.Right, scope);
                }
                case BinaryExpr binary:
                    return Binary(binary.Op, Eval(binary.Left, scope), Eval(binary.Right, scope));
                case CompareExpr compare:
                {
                    // Python's chained comparison: a < b < c is a < b and b < c, b evaluated once.
                    var left = Eval(compare.First, scope);
                    foreach (var (op, rightExpr) in compare.Rest)
                    {
                        var right = Eval(rightExpr, scope);
                        var holds = op switch
                        {
                            "==" => Eq(left, right),
                            "!=" => !Eq(left, right),
                            "<" => JinjaValues.Compare(left, right) < 0,
                            "<=" => JinjaValues.Compare(left, right) <= 0,
                            ">" => JinjaValues.Compare(left, right) > 0,
                            ">=" => JinjaValues.Compare(left, right) >= 0,
                            "in" => Contains(right, left),
                            _ => !Contains(right, left),
                        };
                        if (!holds) return false;
                        left = right;
                    }
                    return true;
                }
                case CondExpr cond:
                    return Truthy(Eval(cond.Test, scope))
                        ? Eval(cond.Then, scope)
                        : cond.Else is null ? new Undefined("the else of a conditional expression") : Eval(cond.Else, scope);
                case ListExpr list when list.IsTuple:
                    return list.Items.Select(i => Eval(i, scope)).ToArray();
                case ListExpr list:
                    return list.Items.Select(i => Eval(i, scope)).ToList();
                case DictExpr dict:
                {
                    var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (k, v) in dict.Pairs)
                    {
                        if (Eval(k, scope) is not string key) throw new NotSupportedException("Dictionary keys other than strings are not in JinjaLite's subset.");
                        result[key] = Eval(v, scope);
                    }
                    return result;
                }
            }
            throw new TemplateException($"Cannot evaluate {expr.GetType().Name}.");
        }

        private object? Call(object? callee, IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> kwargs)
        {
            switch (callee)
            {
                case Macro macro:
                    return Invoke(macro.Node, args, kwargs);
                case BoundMethod method:
                    if (kwargs.Count > 0) throw new NotSupportedException($"Keyword arguments to '{method.Name}' are not in JinjaLite's subset.");
                    return CallMethod(method, args);
                case Builtin function:
                    return CallGlobal(function, args, kwargs);
                case Undefined u:
                    throw u.Error();
                default:
                    throw new TemplateException("That value cannot be called.");
            }
        }

        // A macro runs in its own scope over the template's top level; its output is its value.
        private string Invoke(MacroNode macro, IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> kwargs)
        {
            if (args.Count > macro.Parameters.Length)
                throw new TemplateException($"Macro '{macro.Name}' takes {macro.Parameters.Length} arguments, not {args.Count}.");
            var unknown = kwargs.Keys.FirstOrDefault(k => !macro.Parameters.Contains(k));
            if (unknown is not null) throw new TemplateException($"Macro '{macro.Name}' has no parameter '{unknown}'.");

            var scope = new Scope(root);
            for (var i = 0; i < macro.Parameters.Length; i++)
            {
                var name = macro.Parameters[i];
                object? value;
                if (i < args.Count) value = args[i];
                else if (kwargs.TryGetValue(name, out var given)) value = given;
                else if (macro.Defaults[i] is { } fallback) value = Eval(fallback, scope);
                else value = new Undefined(name);
                scope.Set(name, value);
            }
            var output = new StringBuilder();
            Run(macro.Body, scope, output);
            return output.ToString();
        }
    }
}
