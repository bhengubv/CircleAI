// InMemoryCompanionSessionResolver.cs
//
// Default ICompanionSessionResolver registered by AddCircleAIInferenceServer
// so the server boots out of the box — without this, the host crashes at
// startup because the /v1/companion/turn handler can't resolve its
// resolver parameter when ASP.NET Core builds endpoint metadata.
//
// Semantics:
//   • Sessions are keyed by (sessionId, identityId).
//   • On miss, the resolver builds a REAL CircleAI.Companion.CompanionSession
//     via ICompanionSessionFactory — which resolves IAIService /
//     IEpisodicMemoryStore / IPersonaStore / IAffectStore / IGoalStore /
//     IMemorySyncService / IProactiveReasoningService from DI when they
//     exist, and gracefully degrades when they don't.
//   • Construction is single-flighted per key: a concurrent dictionary +
//     Lazy<Task<…>> ensures the factory runs at most once per (session,
//     identity) tuple even under concurrent resolution.
//   • The cache lives for the lifetime of the resolver singleton. Hosts
//     that need eviction (e.g. for memory pressure) replace this
//     registration with their own implementation — TryAdd semantics in
//     the builder respects that override.
//
// Used by /v1/companion/turn. No fake content, no canned replies — the
// returned session delegates SendAsync / StreamAsync / AgentAsync to its
// configured IAIService (the host-loaded MNN bridge in production; a stub
// in unit tests that explicitly register one).

using System.Collections.Concurrent;
using CircleAI.Companion;
using CircleAI.Inference.Server.Endpoints;

namespace CircleAI.Inference.Server.Hosting;

/// <summary>
/// In-process <see cref="ICompanionSessionResolver"/>. Caches one
/// <see cref="ICompanionSession"/> per (sessionId, identityId) pair and
/// constructs missing sessions via <see cref="ICompanionSessionFactory"/>.
/// </summary>
public sealed class InMemoryCompanionSessionResolver : ICompanionSessionResolver
{
    private readonly ICompanionSessionFactory _factory;
    private readonly InterfaceKind _defaultInterface;
    private readonly ConcurrentDictionary<(string SessionId, string IdentityId),
                                          Lazy<Task<ICompanionSession>>> _sessions
        = new();

    /// <summary>
    /// Constructs the resolver. <paramref name="defaultInterface"/> is the
    /// <see cref="InterfaceKind"/> stamped onto sessions created via this
    /// resolver — defaults to <see cref="InterfaceKind.Web"/> because the
    /// HTTP-fronted server is the canonical entry point.
    /// </summary>
    public InMemoryCompanionSessionResolver(
        ICompanionSessionFactory factory,
        InterfaceKind defaultInterface = InterfaceKind.Web)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _defaultInterface = defaultInterface;
    }

    /// <inheritdoc/>
    public async Task<ICompanionSession?> ResolveAsync(
        string sessionId, string identityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(identityId))
            return null;

        var key = (SessionId: sessionId, IdentityId: identityId);
        var lazy = _sessions.GetOrAdd(key, k => new Lazy<Task<ICompanionSession>>(
            () => _factory.CreateAsync(k.IdentityId, _defaultInterface, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var session = await lazy.Value.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            // A failed construction must not poison the cache — drop the
            // Lazy slot so the next caller can retry cleanly. We re-check
            // identity before removing so we don't kick out a different
            // racing instance.
            _sessions.TryRemove(new KeyValuePair<(string, string), Lazy<Task<ICompanionSession>>>(key, lazy));
            throw;
        }
    }

    /// <summary>
    /// Number of currently cached sessions. Diagnostics only.
    /// </summary>
    public int CachedSessionCount => _sessions.Count;
}
