// NullContactsAdapter.cs

namespace CircleAI.Personal.Contacts;

/// <summary>
/// A contacts adapter that holds no entries. Searches return empty;
/// lookups return null. Enforces the consent contract on every call.
/// </summary>
public sealed class NullContactsAdapter : IContactsAdapter
{
    /// <inheritdoc />
    public Task<IReadOnlyList<Contact>> SearchAsync(
        string query,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.ContactsRead);
        return Task.FromResult<IReadOnlyList<Contact>>(Array.Empty<Contact>());
    }

    /// <inheritdoc />
    public Task<Contact?> GetByExternalIdAsync(
        string externalId,
        UserConsentToken consent,
        CancellationToken cancellationToken)
    {
        ConsentGuard.Require(consent, ConsentScope.ContactsRead);
        return Task.FromResult<Contact?>(null);
    }
}
