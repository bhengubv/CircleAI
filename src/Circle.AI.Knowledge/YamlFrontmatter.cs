// YamlFrontmatter.cs
//
// Minimal YAML frontmatter parser/writer. Only supports flat key-value pairs.
// Nested structures and YAML lists are rejected. This is intentional — the
// frontmatter is metadata, not a general-purpose YAML serialisation surface.

using System.Text;

namespace Circle.AI.Knowledge;

/// <summary>
/// Parses and writes minimal YAML frontmatter blocks of the form:
/// <code>
/// ---
/// key: value
/// key2: value2
/// ---
/// (body)
/// </code>
/// Only flat string-to-string mappings are supported. Nested keys, YAML
/// flow-style structures, anchors, and lists are explicitly rejected so the
/// format stays predictable across implementations.
/// </summary>
internal static class YamlFrontmatter
{
    private const string Delimiter = "---";

    /// <summary>
    /// Renders <paramref name="frontmatter"/> into a YAML block followed by
    /// <paramref name="body"/>. If <paramref name="frontmatter"/> is empty
    /// the block is still emitted (empty delimited block) so the format
    /// stays uniform.
    /// </summary>
    public static string Write(
        IReadOnlyDictionary<string, string> frontmatter,
        string body)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        ArgumentNullException.ThrowIfNull(body);

        var sb = new StringBuilder();
        sb.Append(Delimiter).Append('\n');
        foreach (var kvp in frontmatter)
        {
            ValidateKey(kvp.Key);
            sb.Append(kvp.Key);
            sb.Append(": ");
            sb.Append(EncodeValue(kvp.Value));
            sb.Append('\n');
        }
        sb.Append(Delimiter).Append('\n');
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>
    /// Parses <paramref name="text"/> into a frontmatter dictionary and a
    /// body string. Throws <see cref="FormatException"/> if the document is
    /// malformed.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Frontmatter, string Body) Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Normalise line endings.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!text.StartsWith(Delimiter + "\n", StringComparison.Ordinal))
            throw new FormatException("Frontmatter must start with '---' on its own line.");

        // Locate the closing delimiter.
        int searchStart = Delimiter.Length + 1;
        int closingIdx = text.IndexOf("\n" + Delimiter + "\n", searchStart, StringComparison.Ordinal);
        if (closingIdx < 0)
            throw new FormatException("Missing closing '---' line for frontmatter block.");

        string yaml = text.Substring(searchStart, closingIdx - searchStart);
        string body = text.Substring(closingIdx + ("\n" + Delimiter + "\n").Length);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in yaml.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            // Reject indented lines — that would imply nesting.
            if (rawLine[0] == ' ' || rawLine[0] == '\t')
                throw new FormatException("Nested YAML is not supported.");

            // Reject list markers.
            if (rawLine.StartsWith("- ", StringComparison.Ordinal))
                throw new FormatException("YAML lists are not supported.");

            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
                throw new FormatException($"Malformed YAML line: '{rawLine}'.");

            string key = rawLine[..colon].Trim();
            string rest = colon + 1 < rawLine.Length
                ? rawLine[(colon + 1)..].TrimStart()
                : string.Empty;

            ValidateKey(key);

            // Reject obvious nested-structure starters on the value side.
            if (rest.StartsWith('{') || rest.StartsWith('['))
                throw new FormatException("Flow-style YAML structures are not supported.");

            dict[key] = DecodeValue(rest);
        }

        return (dict, body);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new FormatException("YAML key cannot be empty.");

        foreach (var ch in key)
        {
            // Allow alnum, underscore, dash, dot. Anything else is suspicious.
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
                throw new FormatException($"Invalid character '{ch}' in YAML key '{key}'.");
        }
    }

    /// <summary>
    /// Encodes a value for output. Values that contain reserved characters
    /// (colon, hash, newline, quote, backslash) are wrapped in double quotes
    /// with the standard YAML escape sequences applied.
    /// </summary>
    private static string EncodeValue(string value)
    {
        if (value is null) return string.Empty;
        if (value.Length == 0) return "\"\"";

        bool needsQuoting = false;
        foreach (var ch in value)
        {
            if (ch is ':' or '#' or '\n' or '\r' or '\t' or '"' or '\\' or '\'' or '{' or '[')
            {
                needsQuoting = true;
                break;
            }
        }

        // Leading/trailing whitespace also requires quoting.
        if (!needsQuoting && (value[0] == ' ' || value[^1] == ' '))
            needsQuoting = true;

        if (!needsQuoting) return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:   sb.Append(ch);     break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Decodes a YAML scalar from its on-disk form. Handles both bare and
    /// double-quoted forms with the matching escape set produced by
    /// <see cref="EncodeValue"/>.
    /// </summary>
    private static string DecodeValue(string raw)
    {
        if (raw.Length == 0) return string.Empty;

        // Strip a single trailing inline comment ('  # comment') — only when
        // the value is NOT quoted; quoted values keep their content verbatim.
        if (raw[0] != '"' && raw[0] != '\'')
        {
            int hashIdx = raw.IndexOf(" #", StringComparison.Ordinal);
            if (hashIdx >= 0) raw = raw[..hashIdx].TrimEnd();
            return raw;
        }

        // Single-quoted form is intentionally rejected — we never emit it.
        if (raw[0] == '\'')
            throw new FormatException("Single-quoted YAML scalars are not supported.");

        if (raw.Length < 2 || raw[^1] != '"')
            throw new FormatException("Unterminated double-quoted YAML scalar.");

        var inner = raw[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch != '\\') { sb.Append(ch); continue; }

            if (i + 1 >= inner.Length)
                throw new FormatException("Trailing backslash in YAML scalar.");

            var next = inner[++i];
            switch (next)
            {
                case '\\': sb.Append('\\'); break;
                case '"':  sb.Append('"');  break;
                case 'n':  sb.Append('\n'); break;
                case 'r':  sb.Append('\r'); break;
                case 't':  sb.Append('\t'); break;
                default:
                    throw new FormatException($"Unsupported YAML escape '\\{next}'.");
            }
        }
        return sb.ToString();
    }
}
