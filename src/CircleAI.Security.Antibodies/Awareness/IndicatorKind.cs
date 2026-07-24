// IndicatorKind.cs
//
// The kinds of indicator an antibody can be asked about. Every kind names
// something the USER is about to trust (a file, a URL/host) or something that is
// the user's OWN (their email/username/phone). None of them describe a third party
// to go and look up.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// The type of a <see cref="ThreatIndicator"/> or subject under assessment.
/// </summary>
public enum IndicatorKind
{
    /// <summary>A file identified by its lowercase SHA-256 hex digest.</summary>
    FileHashSha256,

    /// <summary>A full URL the user is about to open.</summary>
    Url,

    /// <summary>An IP address the user is about to connect to.</summary>
    IpAddress,

    /// <summary>A domain / host name the user is about to trust.</summary>
    DomainName,

    /// <summary>The user's own email address (hashed before any lookup).</summary>
    EmailAddress,

    /// <summary>The user's own username / handle (hashed before any lookup).</summary>
    Username,

    /// <summary>The user's own phone number (hashed before any lookup).</summary>
    PhoneNumber,
}
