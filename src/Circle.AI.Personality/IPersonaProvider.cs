// IPersonaProvider.cs
//
// Storage contract for user-owned Persona documents. Distinct from
// IPersonaStore in Circle.AI.Memory: that one stores the AI's learned
// PersonaState; this one stores the user's declared Persona.

namespace Circle.AI.Personality;

/// <summary>
/// Persists and retrieves user-declared <see cref="Persona"/> documents.
/// Implementations may persist to local JSON files, cloud sync, or an
/// encrypted on-device store. Every implementation must support full
/// user-driven export (the user owns this data).
/// </summary>
public interface IPersonaProvider
{
    /// <summary>
    /// Loads the persona associated with <paramref name="userId"/>.
    /// Returns <c>null</c> when no persona has been saved for that user.
    /// </summary>
    /// <param name="userId">Opaque user identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Persona?> GetAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Persists <paramref name="persona"/> for <paramref name="userId"/>.
    /// Implementations must refresh <see cref="Persona.UpdatedAt"/> to the
    /// current UTC time and return the saved record.
    /// </summary>
    /// <param name="userId">Opaque user identifier.</param>
    /// <param name="persona">The persona to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Persona> SaveAsync(string userId, Persona persona, CancellationToken ct = default);

    /// <summary>
    /// Returns whether a persona is currently stored for
    /// <paramref name="userId"/>.
    /// </summary>
    /// <param name="userId">Opaque user identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Streams every persona currently stored. Used for user-driven export
    /// (GDPR / POPIA "give me everything you have on me").
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<Persona> ExportAllAsync(CancellationToken ct = default);
}
