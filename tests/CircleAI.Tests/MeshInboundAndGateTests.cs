// MeshInboundAndGateTests.cs
//
// Items 1 + 2 audit follow-up:
//   1. AetherNetInboundDirectiveBridge — mesh-side directive → CircleAI store
//   2. MeshGatedCompanionSession — chat path consults the gate

using CircleAI.Aether;
using CircleAI.AetherNet;
using CircleAI.Companion;
using CircleAI.Security.AetherNet;
using Xunit;
using MeshDirective = AetherNet.Extensibility.SecurityDirective;
using MeshDirectiveKind = AetherNet.Extensibility.SecurityDirectiveKind;
using MeshThreatLevel = AetherNet.Extensibility.Events.AetherNetThreatLevel;

namespace CircleAI.Tests;

public sealed class MeshInboundAndGateTests
{
    // ══════════════════════════════════════════════════════════════════════
    // Item 1 — AetherNetInboundDirectiveBridge
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void InboundBridge_MeshQuarantine_AppearsInStore_AsBlock()
    {
        var store = new MeshDirectiveStore();
        var bridge = new AetherNetInboundDirectiveBridge(store);

        var meshDirective = new MeshDirective(
            Kind: MeshDirectiveKind.QuarantineNode,
            TargetNodeId: "nodeBad",
            TrustScoreOverride: null,
            ThreatLevel: MeshThreatLevel.High,
            Reason: "spammer from peer",
            Duration: null,
            IssuedAt: DateTimeOffset.UtcNow);

        bridge.OnDirective(meshDirective);

        Assert.True(store.IsBlocked("nodeBad", out var reason));
        Assert.Equal("spammer from peer", reason);
    }

    [Fact]
    public void InboundBridge_MeshRelease_RemovesFromStore()
    {
        var store = new MeshDirectiveStore();
        var bridge = new AetherNetInboundDirectiveBridge(store);

        bridge.OnDirective(new MeshDirective(
            MeshDirectiveKind.QuarantineNode, "nodeX", null,
            MeshThreatLevel.High, "block", null, DateTimeOffset.UtcNow));
        Assert.True(store.IsBlocked("nodeX", out _));

        bridge.OnDirective(new MeshDirective(
            MeshDirectiveKind.ReleaseNode, "nodeX", null,
            MeshThreatLevel.None, "lift", null, DateTimeOffset.UtcNow));
        Assert.False(store.IsBlocked("nodeX", out _));
    }

    [Fact]
    public void InboundBridge_NullDirective_Throws()
    {
        var store = new MeshDirectiveStore();
        var bridge = new AetherNetInboundDirectiveBridge(store);
        Assert.Throws<ArgumentNullException>(() => bridge.OnDirective(null!));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Item 2 — MeshGatedCompanionSession
    // ══════════════════════════════════════════════════════════════════════

    private sealed class FakeCompanionSession : ICompanionSession
    {
        public int SendCallCount { get; private set; }
        public int StreamCallCount { get; private set; }
        public int AgentCallCount { get; private set; }

        public string SessionId { get; init; } = "session-1";
        public string IdentityId { get; init; } = "user-1";
        public InterfaceKind Interface => InterfaceKind.Mobile;
        public IReadOnlyList<CompanionTurn> History { get; } = Array.Empty<CompanionTurn>();

        public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady;

        public Task<string> SendAsync(string message, CancellationToken ct = default)
        {
            SendCallCount++;
            return Task.FromResult($"reply:{message}");
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string message,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCallCount++;
            await Task.Yield();
            yield return $"stream:{message}";
        }

        public Task<string> AgentAsync(string instruction, CancellationToken ct = default)
        {
            AgentCallCount++;
            return Task.FromResult($"agent:{instruction}");
        }

        public CompanionContext GetContext() => new CompanionContext(
            IdentityId: IdentityId,
            DisplayName: IdentityId,
            PreferredLanguage: null,
            Interface: Interface,
            PersonaHints: "",
            AffectSummary: "",
            RecentMemorySnippets: Array.Empty<string>(),
            ActiveGoals: Array.Empty<string>(),
            UserFacts: Array.Empty<string>(),
            ContextBuiltAt: DateTimeOffset.UtcNow);

        public Task RefreshContextAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SignalFeedbackAsync(bool positive, string? note = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // Suppress "unused event" warning.
        public void RaiseProactive() => ProactiveMessageReady?.Invoke(this, new CompanionProactiveEvent(
            SessionId, IdentityId, Interface, "test message", "test-trigger", DateTimeOffset.UtcNow));
    }

    private static (MeshGatedCompanionSession gated, FakeCompanionSession inner,
                    MeshDirectiveStore store, MeshSecurityGate gate)
        Wire(string identityId = "user-1")
    {
        var inner = new FakeCompanionSession { IdentityId = identityId };
        var store = new MeshDirectiveStore();
        var gate = new MeshSecurityGate(store);
        var gated = new MeshGatedCompanionSession(inner, gate);
        return (gated, inner, store, gate);
    }

    private static SecurityDirective Quarantine(string id, string reason = "blocked") =>
        new(SecurityDirectiveKind.QuarantineNode, id, null,
            AetherThreatLevel.High, reason, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task GatedSession_SendAsync_PassesWhenNotBlocked()
    {
        var (gated, inner, _, _) = Wire();
        var r = await gated.SendAsync("hello");
        Assert.Equal("reply:hello", r);
        Assert.Equal(1, inner.SendCallCount);
    }

    [Fact]
    public async Task GatedSession_SendAsync_ThrowsWhenBlocked()
    {
        var (gated, inner, store, _) = Wire();
        store.OnDirective(Quarantine("user-1", "spam"));

        var ex = await Assert.ThrowsAsync<MeshSecurityBlockedException>(
            () => gated.SendAsync("hello"));
        Assert.Equal("user-1", ex.BlockedId);
        Assert.Contains("spam", ex.Message);
        Assert.Equal(0, inner.SendCallCount);
    }

    [Fact]
    public async Task GatedSession_StreamAsync_ThrowsImmediatelyWhenBlocked()
    {
        var (gated, inner, store, _) = Wire();
        store.OnDirective(Quarantine("user-1"));

        await Assert.ThrowsAsync<MeshSecurityBlockedException>(async () =>
        {
            await foreach (var _ in gated.StreamAsync("hello"))
            {
                // shouldn't reach here
            }
        });
        Assert.Equal(0, inner.StreamCallCount);
    }

    [Fact]
    public async Task GatedSession_AgentAsync_ThrowsWhenBlocked()
    {
        var (gated, inner, store, _) = Wire();
        store.OnDirective(Quarantine("user-1"));

        await Assert.ThrowsAsync<MeshSecurityBlockedException>(() => gated.AgentAsync("do x"));
        Assert.Equal(0, inner.AgentCallCount);
    }

    [Fact]
    public void GatedSession_GetContext_NotGated()
    {
        // Diagnostic surfaces stay reachable even when blocked — the gate
        // stops chat, not visibility into one's own state.
        var (gated, _, store, _) = Wire();
        store.OnDirective(Quarantine("user-1"));
        var ctx = gated.GetContext();
        Assert.Equal("user-1", ctx.IdentityId);
    }

    [Fact]
    public async Task GatedSession_SignalFeedback_NotGated()
    {
        var (gated, _, store, _) = Wire();
        store.OnDirective(Quarantine("user-1"));
        await gated.SignalFeedbackAsync(positive: true); // must not throw
    }
}
