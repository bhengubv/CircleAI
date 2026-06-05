// StubCompanionSession.cs + StubCompanionSessionResolver.cs
//
// Minimum-viable Companion session implementation for endpoint tests.

using System.Runtime.CompilerServices;
using CircleAI.Companion;
using CircleAI.Inference.Server.Endpoints;

namespace CircleAI.Inference.Server.Tests.TestFixtures;

public sealed class StubCompanionSession : ICompanionSession
{
    public string SessionId { get; }
    public string IdentityId { get; }
    public InterfaceKind Interface => InterfaceKind.Web;

    private readonly List<CompanionTurn> _history = new();

    public StubCompanionSession(string sessionId, string identityId)
    {
        SessionId  = sessionId;
        IdentityId = identityId;
    }

    public IReadOnlyList<CompanionTurn> History => _history;

    public Task<string> SendAsync(string message, CancellationToken ct = default)
    {
        var reply = $"stub-reply({message})";
        _history.Add(new CompanionTurn("user",      message, DateTimeOffset.UtcNow));
        _history.Add(new CompanionTurn("assistant", reply,   DateTimeOffset.UtcNow));
        return Task.FromResult(reply);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var c in new[] { "stub", "-", "stream" })
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
            await Task.Yield();
        }
        _history.Add(new CompanionTurn("user",      message,       DateTimeOffset.UtcNow));
        _history.Add(new CompanionTurn("assistant", "stub-stream", DateTimeOffset.UtcNow));
    }

    public Task<string> AgentAsync(string instruction, CancellationToken ct = default) =>
        Task.FromResult($"stub-agent({instruction})");

    public CompanionContext GetContext() => new(
        IdentityId: IdentityId,
        DisplayName: "Stub Caller",
        PreferredLanguage: "en-US",
        Interface: Interface,
        PersonaHints: "",
        AffectSummary: "",
        RecentMemorySnippets: Array.Empty<string>(),
        ActiveGoals: Array.Empty<string>(),
        ContextBuiltAt: DateTimeOffset.UtcNow);

    public Task RefreshContextAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SignalFeedbackAsync(bool positive, string? note = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady;

    public ValueTask DisposeAsync()
    {
        // Suppress unused-event warning + provide a way for tests to fire it.
        _ = ProactiveMessageReady;
        return ValueTask.CompletedTask;
    }
}

public sealed class StubCompanionSessionResolver : ICompanionSessionResolver
{
    private readonly Dictionary<(string, string), StubCompanionSession> _sessions = new();

    public void Register(string sessionId, string identityId) =>
        _sessions[(sessionId, identityId)] = new StubCompanionSession(sessionId, identityId);

    public Task<ICompanionSession?> ResolveAsync(string sessionId, string identityId, CancellationToken ct)
    {
        ICompanionSession? session = _sessions.TryGetValue((sessionId, identityId), out var s) ? s : null;
        return Task.FromResult(session);
    }
}
