// Contact.cs

namespace CircleAI.Personal.Contacts;

/// <summary>
/// A user contact normalised across providers.
/// </summary>
/// <param name="Id">Stable identifier within Circle.</param>
/// <param name="ExternalId">Provider-native identifier.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Emails">Known email addresses.</param>
/// <param name="PhoneNumbers">Known phone numbers in E.164 form.</param>
/// <param name="Relationship">User-tagged relationship label ("spouse", "colleague"), or null.</param>
/// <param name="LastInteractionAt">UTC time of the most recent interaction Circle has observed.</param>
public sealed record Contact(
    Guid Id,
    string ExternalId,
    string DisplayName,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> PhoneNumbers,
    string? Relationship,
    DateTimeOffset LastInteractionAt
);
