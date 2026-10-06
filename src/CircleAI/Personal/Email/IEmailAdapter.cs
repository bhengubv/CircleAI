// IEmailAdapter.cs

namespace CircleAI.Personal.Email;

/// <summary>
/// Contract for an email adapter. Concrete implementations bind to a specific
/// provider (Gmail, Microsoft Graph, IMAP, …) and ship in separate packages.
/// </summary>
/// <remarks>
/// This package never sends mail. Drafting is supported via
/// <see cref="DraftReplyAsync"/>, but actual send crosses a trust boundary
/// and is handled outside this contract.
/// </remarks>
public interface IEmailAdapter
{
    /// <summary>
    /// Lists the most recent <paramref name="count"/> messages in the user's inbox.
    /// Requires <see cref="ConsentScope.EmailRead"/>.
    /// </summary>
    /// <param name="count">Maximum number of messages to return.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Messages ordered most-recent-first.</returns>
    Task<IReadOnlyList<EmailMessage>> ListRecentAsync(
        int count,
        UserConsentToken consent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a single message by its provider-native id, or null if not found.
    /// Requires <see cref="ConsentScope.EmailRead"/>.
    /// </summary>
    /// <param name="externalId">Provider-native id.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message, or null.</returns>
    Task<EmailMessage?> GetByIdAsync(
        string externalId,
        UserConsentToken consent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a draft reply to the referenced message. The draft is saved in the
    /// user's drafts folder; this method DOES NOT send.
    /// Requires <see cref="ConsentScope.EmailDraft"/>.
    /// </summary>
    /// <param name="toExternalId">External id of the message being replied to.</param>
    /// <param name="bodyPlain">Plain-text body of the reply.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Circle identifier of the newly created draft.</returns>
    Task<Guid> DraftReplyAsync(
        string toExternalId,
        string bodyPlain,
        UserConsentToken consent,
        CancellationToken cancellationToken);
}
