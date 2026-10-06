// ConsentGuard.cs
//
// Shared scope-check helper used by every Personal adapter. Centralised so all
// adapters throw the same exception types and messages.

namespace CircleAI.Personal;

/// <summary>
/// Helper that validates a <see cref="UserConsentToken"/> against a required scope.
/// </summary>
public static class ConsentGuard
{
    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> when <paramref name="consent"/>
    /// does not grant <paramref name="scope"/> or has expired.
    /// </summary>
    /// <param name="consent">The consent token presented by the caller.</param>
    /// <param name="scope">The scope required by the operation.</param>
    public static void Require(UserConsentToken consent, ConsentScope scope)
    {
        ArgumentNullException.ThrowIfNull(consent);

        if (!consent.IsValidFor(scope, DateTimeOffset.UtcNow))
        {
            throw new UnauthorizedAccessException(
                $"Consent token {consent.Id} does not grant scope {scope} or has expired.");
        }
    }
}
