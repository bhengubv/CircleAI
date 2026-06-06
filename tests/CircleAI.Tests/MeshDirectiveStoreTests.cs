using CircleAI.Aether;
using CircleAI.Security.Aether;
using Xunit;

namespace CircleAI.Tests;

/// <summary>
/// Covers MeshDirectiveStore + MeshSecurityGate. Uses a controllable clock
/// so expiry behaviour is deterministic without Task.Delay.
/// </summary>
public sealed class MeshDirectiveStoreTests
{
    private DateTimeOffset _now = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset Clock() => _now;

    private MeshDirectiveStore NewStore() => new(Clock);

    private static SecurityDirective Block(string nodeId, TimeSpan? duration = null, string reason = "test") =>
        new(SecurityDirectiveKind.QuarantineNode, nodeId, null, AetherThreatLevel.High, reason, duration, DateTimeOffset.UtcNow);

    private static SecurityDirective Avoid(string nodeId, TimeSpan? duration = null) =>
        new(SecurityDirectiveKind.AvoidNode, nodeId, null, AetherThreatLevel.Medium, "avoid", duration, DateTimeOffset.UtcNow);

    private static SecurityDirective Release(string nodeId) =>
        new(SecurityDirectiveKind.ReleaseNode, nodeId, null, AetherThreatLevel.None, "lift", null, DateTimeOffset.UtcNow);

    // ── Block / Quarantine ────────────────────────────────────────────────

    [Fact]
    public void NoDirective_IsBlocked_ReturnsFalse()
    {
        var store = NewStore();
        Assert.False(store.IsBlocked("nodeA", out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Quarantine_IsBlocked_ReturnsTrueWithReason()
    {
        var store = NewStore();
        store.OnDirective(Block("nodeA", reason: "spam detected"));
        Assert.True(store.IsBlocked("nodeA", out var reason));
        Assert.Equal("spam detected", reason);
    }

    [Fact]
    public void Avoid_IsBlocked_ReturnsTrue()
    {
        var store = NewStore();
        store.OnDirective(Avoid("nodeA"));
        Assert.True(store.IsBlocked("nodeA", out _));
    }

    [Fact]
    public void OtherNode_IsBlocked_ReturnsFalse()
    {
        var store = NewStore();
        store.OnDirective(Block("nodeA"));
        Assert.False(store.IsBlocked("nodeB", out _));
    }

    // ── Release ───────────────────────────────────────────────────────────

    [Fact]
    public void Release_RemovesAllDirectivesForNode()
    {
        var store = NewStore();
        store.OnDirective(Block("nodeA"));
        store.OnDirective(Avoid("nodeA"));
        Assert.True(store.IsBlocked("nodeA", out _));

        store.OnDirective(Release("nodeA"));

        Assert.False(store.IsBlocked("nodeA", out _));
        Assert.Empty(store.GetActiveDirectives("nodeA"));
    }

    // ── Expiry ────────────────────────────────────────────────────────────

    [Fact]
    public void Expired_IsBlocked_ReturnsFalseAndCleans()
    {
        var store = NewStore();
        // Issue with a 1-minute window
        var directive = new SecurityDirective(
            SecurityDirectiveKind.QuarantineNode, "nodeA", null,
            AetherThreatLevel.High, "transient", TimeSpan.FromMinutes(1), _now);
        store.OnDirective(directive);
        Assert.True(store.IsBlocked("nodeA", out _));

        // Advance clock past expiry
        _now = _now.AddMinutes(2);

        Assert.False(store.IsBlocked("nodeA", out _));
        Assert.Empty(store.GetActiveDirectives("nodeA"));
    }

    [Fact]
    public void Permanent_DoesNotExpire()
    {
        var store = NewStore();
        store.OnDirective(Block("nodeA", duration: null));
        _now = _now.AddYears(10);
        Assert.True(store.IsBlocked("nodeA", out _));
    }

    // ── Latest reason wins ────────────────────────────────────────────────

    [Fact]
    public void MultipleBlocks_LatestReasonReturned()
    {
        var store = NewStore();
        var older = new SecurityDirective(
            SecurityDirectiveKind.QuarantineNode, "nodeA", null,
            AetherThreatLevel.High, "old reason", null, _now.AddMinutes(-10));
        var newer = new SecurityDirective(
            SecurityDirectiveKind.QuarantineNode, "nodeA", null,
            AetherThreatLevel.High, "fresh reason", null, _now);

        store.OnDirective(older);
        store.OnDirective(newer);

        Assert.True(store.IsBlocked("nodeA", out var reason));
        Assert.Equal("fresh reason", reason);
    }

    // ── Untargeted directives are ignored by the store ────────────────────

    [Fact]
    public void NoTarget_OnDirective_DoesNotRecord()
    {
        var store = NewStore();
        var untargeted = new SecurityDirective(
            SecurityDirectiveKind.ElevateMonitoring, null, null,
            AetherThreatLevel.Low, "global", null, _now);

        store.OnDirective(untargeted);

        Assert.Equal(0, store.TrackedNodeCount);
    }

    // ── Edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void Whitespace_IsBlocked_ReturnsFalse()
    {
        var store = NewStore();
        Assert.False(store.IsBlocked("", out _));
        Assert.False(store.IsBlocked("   ", out _));
    }

    [Fact]
    public void Null_OnDirective_Throws()
    {
        var store = NewStore();
        Assert.Throws<ArgumentNullException>(() => store.OnDirective(null!));
    }

    // ── MeshSecurityGate ──────────────────────────────────────────────────

    [Fact]
    public void Gate_Decide_AllowedWhenNoBlock()
    {
        var store = NewStore();
        var gate = new MeshSecurityGate(store);

        var d = gate.Decide("nodeA");
        Assert.False(d.IsBlocked);
        Assert.Equal(string.Empty, d.Reason);
    }

    [Fact]
    public void Gate_Decide_BlockedAfterDirective()
    {
        var store = NewStore();
        store.OnDirective(Block("nodeA", reason: "abuse"));
        var gate = new MeshSecurityGate(store);

        var d = gate.Decide("nodeA");
        Assert.True(d.IsBlocked);
        Assert.Equal("abuse", d.Reason);
    }

    [Fact]
    public void Gate_Enforce_ThrowsWhenBlocked()
    {
        var store = NewStore();
        store.OnDirective(Block("nodeA", reason: "abuse"));
        var gate = new MeshSecurityGate(store);

        var ex = Assert.Throws<MeshSecurityBlockedException>(() => gate.Enforce("nodeA"));
        Assert.Equal("nodeA", ex.BlockedId);
        Assert.Contains("abuse", ex.Message);
    }

    [Fact]
    public void Gate_Enforce_DoesNotThrowWhenAllowed()
    {
        var store = NewStore();
        var gate = new MeshSecurityGate(store);

        gate.Enforce("freshNode"); // no throw
    }
}
