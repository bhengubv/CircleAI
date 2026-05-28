// IContactsAdapter.cs

namespace Circle.AI.Personal.Contacts;

/// <summary>
/// Contract for a contacts adapter. Concrete implementations bind to a specific
/// provider (Google People, Microsoft Graph, iOS Contacts, …) and ship in
/// separate packages.
/// </summary>
public interface IContactsAdapter
{
    /// <summary>
    /// Searches the user's contacts by free-text query (display name, email, phone).
    /// Requires <see cref="ConsentScope.ContactsRead"/>.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching contacts.</returns>
    Task<IReadOnlyList<Contact>> SearchAsync(
        string query,
        UserConsentToken consent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a single contact by provider-native id, or null if not found.
    /// Requires <see cref="ConsentScope.ContactsRead"/>.
    /// </summary>
    /// <param name="externalId">Provider-native id.</param>
    /// <param name="consent">User consent token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The contact, or null.</returns>
    Task<Contact?> GetByExternalIdAsync(
        string externalId,
        UserConsentToken consent,
        CancellationToken cancellationToken);
}
