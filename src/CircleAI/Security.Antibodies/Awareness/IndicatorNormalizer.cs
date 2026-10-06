// IndicatorNormalizer.cs
//
// Canonicalizes indicators before lookup so equivalent values collide, and hashes
// identity values so the breach path never handles or stores plaintext identities.
// Pure, offline, BCL-only.

using System.Security.Cryptography;
using System.Text;

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// Internal helpers that turn raw subjects into canonical corpus keys. Kept internal
/// because it is an implementation detail of the assessors, not part of the surface.
/// </summary>
internal static class IndicatorNormalizer
{
    /// <summary>Lowercase SHA-256 hex digest of the UTF-8 bytes of <paramref name="value"/>.</summary>
    public static string Sha256HexLower(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalizes a network indicator: trims, lowercases, and for domains strips a
    /// leading "www.". URLs and IPs are trimmed and lowercased. Returns <c>null</c>
    /// when the value is blank.
    /// </summary>
    public static string? NormalizeNetwork(IndicatorKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string v = value.Trim().ToLowerInvariant();

        if (kind == IndicatorKind.DomainName && v.StartsWith("www.", StringComparison.Ordinal))
            v = v[4..];

        return v;
    }

    /// <summary>
    /// Canonicalizes an identity value then hashes it, so only a digest is ever used
    /// for lookup. Emails/usernames are trimmed and lowercased; phone numbers are
    /// reduced to their leading "+" (if present) plus digits. Returns <c>null</c>
    /// when nothing usable remains.
    /// </summary>
    public static string? NormalizeIdentityToHash(IndicatorKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string canonical;
        if (kind == IndicatorKind.PhoneNumber)
        {
            var sb = new StringBuilder(value.Length);
            bool leadingPlusAllowed = true;
            foreach (char c in value.Trim())
            {
                if (char.IsDigit(c))
                {
                    sb.Append(c);
                    leadingPlusAllowed = false;
                }
                else if (c == '+' && leadingPlusAllowed && sb.Length == 0)
                {
                    sb.Append('+');
                    leadingPlusAllowed = false;
                }
            }
            canonical = sb.ToString();
        }
        else
        {
            canonical = value.Trim().ToLowerInvariant();
        }

        return canonical.Length == 0 ? null : Sha256HexLower(canonical);
    }
}
