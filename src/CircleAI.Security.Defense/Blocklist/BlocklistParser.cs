// BlocklistParser.cs
//
// Pure, stateless parsing of blocklist text into normalised indicators. No state,
// no I/O ownership (takes a TextReader) — fully unit-testable in isolation.
//
// Accepts the union of the common free-feed formats so a CC0/MIT feed can drop in
// unchanged:
//   * plain domain            malware.example
//   * plain IPv4 / IPv6       203.0.113.7   2001:db8::1
//   * IPv4 CIDR               198.51.100.0/24
//   * hosts-file style        0.0.0.0 malware.example   (sink IP ignored)
//   * comments                # ...     and inline   host  # trailing comment

using System.Net;
using System.Net.Sockets;

namespace CircleAI.Security.Defense;

/// <summary>Normalised indicator kinds emitted by <see cref="BlocklistParser"/>.</summary>
public enum IndicatorKind
{
    /// <summary>A single IPv4 address.</summary>
    Ipv4,

    /// <summary>An IPv4 CIDR range.</summary>
    Ipv4Cidr,

    /// <summary>A single IPv6 address (matched exactly).</summary>
    Ipv6,

    /// <summary>A domain (matched exactly and as a parent suffix).</summary>
    Domain,
}

/// <summary>A single parsed indicator: its kind and normalised value.</summary>
/// <param name="Kind">The indicator kind.</param>
/// <param name="Value">Normalised value (lower-cased domain / canonical IP / CIDR text).</param>
public readonly record struct ParsedIndicator(IndicatorKind Kind, string Value);

/// <summary>
/// Stateless parser turning blocklist text into <see cref="ParsedIndicator"/>s.
/// </summary>
public static class BlocklistParser
{
    private static readonly string[] SinkTokens = ["0.0.0.0", "127.0.0.1", "::", "::1"];

    /// <summary>Streams every valid indicator from <paramref name="reader"/>.</summary>
    public static IEnumerable<ParsedIndicator> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (ParseLine(line) is { } indicator)
                yield return indicator;
        }
    }

    /// <summary>
    /// Parses one raw line. Returns <c>null</c> for blank lines, comments, and
    /// tokens that are not a recognisable IP/CIDR/domain.
    /// </summary>
    public static ParsedIndicator? ParseLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;

        string line = rawLine.Trim();

        // Strip whole-line and trailing inline comments.
        int hash = line.IndexOf('#');
        if (hash == 0) return null;
        if (hash > 0) line = line[..hash].Trim();
        if (line.Length == 0) return null;

        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // Hosts-file style: "<sink-ip> <domain>" — take the domain, drop the sink IP.
        string token = parts.Length >= 2 && Array.IndexOf(SinkTokens, parts[0]) >= 0
            ? parts[1]
            : parts[0];

        return Classify(token);
    }

    /// <summary>Classifies a single already-trimmed token, or returns <c>null</c>.</summary>
    public static ParsedIndicator? Classify(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim().TrimEnd('.').ToLowerInvariant();
        if (token.Length == 0) return null;

        if (token.Contains('/', StringComparison.Ordinal))
            return Ipv4Cidr.TryParse(token, out _) ? new ParsedIndicator(IndicatorKind.Ipv4Cidr, token) : null;

        if (IPAddress.TryParse(token, out IPAddress? ip))
            return ip.AddressFamily == AddressFamily.InterNetworkV6
                ? new ParsedIndicator(IndicatorKind.Ipv6, ip.ToString())
                : new ParsedIndicator(IndicatorKind.Ipv4, token);

        return IsPlausibleDomain(token) ? new ParsedIndicator(IndicatorKind.Domain, token) : null;
    }

    private static bool IsPlausibleDomain(string s)
    {
        if (s.Length is 0 or > 253) return false;
        bool hasDot = false;
        foreach (char c in s)
        {
            if (c == '.') { hasDot = true; continue; }
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }
        return hasDot; // require at least one dot so bare words are not treated as domains
    }
}
