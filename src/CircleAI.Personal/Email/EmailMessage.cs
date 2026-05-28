// EmailMessage.cs

namespace CircleAI.Personal.Email;

/// <summary>
/// A user email message normalised across providers.
/// </summary>
/// <param name="Id">Stable identifier within Circle.</param>
/// <param name="ExternalId">Provider-native identifier (Gmail message id, Graph message id, etc.).</param>
/// <param name="From">Sender address.</param>
/// <param name="To">Primary recipient addresses.</param>
/// <param name="Cc">CC recipient addresses.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="BodyPlain">Plain-text body. HTML is intentionally not exposed through the interface — UI layers assemble rich content separately.</param>
/// <param name="ReceivedAt">UTC time the message arrived.</param>
/// <param name="IsUnread">True if the user has not yet read the message.</param>
/// <param name="Labels">Provider labels / folders / categories.</param>
public sealed record EmailMessage(
    Guid Id,
    string ExternalId,
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string BodyPlain,
    DateTimeOffset ReceivedAt,
    bool IsUnread,
    IReadOnlyList<string> Labels
);
