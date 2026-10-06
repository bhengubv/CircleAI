// LinkGrantStore.cs
//
// Where the device keeps who is allowed to use its brain.
//
// The contract, plus an in-memory implementation for tests and for a head that
// has not wired a persistent one yet. A real device head persists these (a small
// JSON file, or the same settings store the app already owns) so a link survives
// a reboot — a person should approve an app once, not every morning. Persistence
// is the head's business; the policy in LinkGate and LinkAuthorizer does not care
// which store backs it.

using System.Collections.Concurrent;

namespace CircleAI.Linking;

/// <summary>The device's record of which apps may use CircleAI, and how much.</summary>
public interface ILinkGrantStore
{
    /// <summary>The grant for this package, or null if it has never linked.</summary>
    Task<LinkGrant?> FindAsync(string callerPackage, CancellationToken ct = default);

    /// <summary>Store a grant, replacing any earlier one for the same package.</summary>
    Task SaveAsync(LinkGrant grant, CancellationToken ct = default);

    /// <summary>Remove a package's grant. The next call from it must re-link.</summary>
    Task RevokeAsync(string callerPackage, CancellationToken ct = default);

    /// <summary>Every current grant — for a "which apps use CircleAI" screen.</summary>
    Task<IReadOnlyList<LinkGrant>> ListAsync(CancellationToken ct = default);
}

/// <summary>Thread-safe in-memory <see cref="ILinkGrantStore"/> for tests and warm-up.</summary>
public sealed class InMemoryLinkGrantStore : ILinkGrantStore
{
    private readonly ConcurrentDictionary<string, LinkGrant> _grants =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<LinkGrant?> FindAsync(string callerPackage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerPackage);
        _grants.TryGetValue(callerPackage, out var grant);
        return Task.FromResult<LinkGrant?>(grant);
    }

    /// <inheritdoc />
    public Task SaveAsync(LinkGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.CallerPackage);
        _grants[grant.CallerPackage] = grant;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RevokeAsync(string callerPackage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerPackage);
        _grants.TryRemove(callerPackage, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LinkGrant>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LinkGrant>>(_grants.Values.ToList());
}
