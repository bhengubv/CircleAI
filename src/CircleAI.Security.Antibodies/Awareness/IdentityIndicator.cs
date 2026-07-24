// IdentityIndicator.cs
//
// The user's OWN identity — their email / username / phone — to check for breach
// exposure so THEY can rotate an exposed credential. This path only ever concerns
// the user's own identity; there is no "look up someone else". The raw value is
// hashed before any lookup (see BreachExposureAssessor) and is never persisted.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// One of the user's own identity values to check for breach exposure. The raw
/// value stays on the caller's side only long enough to be hashed; the assessor
/// looks up the hash, never the plaintext.
/// </summary>
/// <param name="Kind">One of <see cref="IndicatorKind.EmailAddress"/>, <see cref="IndicatorKind.Username"/>, or <see cref="IndicatorKind.PhoneNumber"/>.</param>
/// <param name="Value">The user's own raw identity value.</param>
public sealed record IdentityIndicator(IndicatorKind Kind, string Value)
{
    /// <summary>The user's own email address.</summary>
    public static IdentityIndicator Email(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new IdentityIndicator(IndicatorKind.EmailAddress, email);
    }

    /// <summary>The user's own username / handle.</summary>
    public static IdentityIndicator Username(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return new IdentityIndicator(IndicatorKind.Username, username);
    }

    /// <summary>The user's own phone number.</summary>
    public static IdentityIndicator Phone(string phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        return new IdentityIndicator(IndicatorKind.PhoneNumber, phone);
    }
}
