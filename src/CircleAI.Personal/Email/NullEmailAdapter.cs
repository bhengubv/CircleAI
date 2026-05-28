// NullEmailAdapter.cs

namespace CircleAI.Personal.Email;

/// <summary>
/// An email adapter that holds no messages. Reads return empty / null;
/// <see cref="DraftReplyAsync"/> returns a fresh <see cref="Guid"/> each call
/// (the "draft" is not persisted anywhere). All methods enforce the consent
/// contract before returning.
/// </summary>
public sealed class NullEmailAdapter : IEmailAdapter
{
    /// <inheritdoc />
    public Task<IReadOnlyList<EmailMessage>> ListRecentAsync(
        int count,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.EmailRead);
        return Task.FromResult<IReadOnlyList<EmailMessage>>(Array.Empty<EmailMessage>());
    }

    /// <inheritdoc />
    public Task<EmailMessage?> GetByIdAsync(
        string externalId,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.EmailRead);
        return Task.FromResult<EmailMessage?>(null);
    }

    /// <inheritdoc />
    public Task<Guid> DraftReplyAsync(
        string toExternalId,
        string bodyPlain,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.EmailDraft);
        return Task.FromResult(Guid.NewGuid());
    }
}
