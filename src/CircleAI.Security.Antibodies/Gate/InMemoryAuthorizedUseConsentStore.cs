// InMemoryAuthorizedUseConsentStore.cs
//
// A real, thread-safe, in-process consent store. Starts EMPTY — so a system wired
// with this store but no recorded consent still denies everything. A host adds a
// consent only when a human explicitly authorizes a capability.

using System.Collections.Concurrent;

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// In-memory <see cref="IAuthorizedUseConsentStore"/>. Thread-safe and dependency-free
/// so it runs on any device offline. It holds exactly the consents a host records and
/// nothing else; the most recently added, still-active consent for a capability wins.
/// </summary>
public sealed class InMemoryAuthorizedUseConsentStore : IAuthorizedUseConsentStore
{
    // Keyed by capability; the value is the latest recorded consent for it.
    private readonly ConcurrentDictionary<AntibodyCapability, AuthorizedUseConsent> _consents = new();

    /// <summary>
    /// Records <paramref name="consent"/>, replacing any prior consent for the same
    /// capability. Call this only in response to an explicit human authorization.
    /// </summary>
    public void Record(AuthorizedUseConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        _consents[consent.Capability] = consent;
    }

    /// <summary>
    /// Immediately revokes any recorded consent for <paramref name="capability"/>,
    /// returning it to the deny-by-default state for that capability.
    /// </summary>
    public void Revoke(AntibodyCapability capability) => _consents.TryRemove(capability, out _);

    /// <summary>Revokes all recorded consents.</summary>
    public void RevokeAll() => _consents.Clear();

    /// <inheritdoc/>
    public ValueTask<AuthorizedUseConsent?> FindActiveConsentAsync(
        AntibodyCapability capability,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (_consents.TryGetValue(capability, out var consent) && consent.IsActiveFor(capability, now))
            return ValueTask.FromResult<AuthorizedUseConsent?>(consent);

        return ValueTask.FromResult<AuthorizedUseConsent?>(null);
    }
}
