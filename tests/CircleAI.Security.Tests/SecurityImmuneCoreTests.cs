// SecurityImmuneCoreTests.cs
//
// Tests for the CircleAI.Security immune-system extension:
//   ThreatVector, AnomalySignal, SecurityCheckpoint, SecurityResponse,
//   ISecurityWatchdog (DefaultSecurityWatchdog), UhidKeyRing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Security;
using Xunit;

namespace CircleAI.Security.Tests;

// ── AnomalySignal ─────────────────────────────────────────────────────────────

public sealed class AnomalySignalTests
{
    [Fact]
    public void Create_StampsNewGuidAndUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var s = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.8, "Module.X", "desc");
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(Guid.Empty, s.Id);
        Assert.InRange(s.DetectedAt, before, after);
    }

    [Fact]
    public void Create_ClampsConfidenceAbove1()
    {
        var s = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 1.5, "M", "d");
        Assert.Equal(1.0, s.Confidence);
    }

    [Fact]
    public void Create_ClampsConfidenceBelow0()
    {
        var s = AnomalySignal.Create(ThreatVector.MemoryAnomaly, -0.3, "M", "d");
        Assert.Equal(0.0, s.Confidence);
    }

    [Fact]
    public void Create_SetsVectorAndModule()
    {
        var s = AnomalySignal.Create(ThreatVector.BiometricSpoofAttempt, 0.9, "identity", "spoof");
        Assert.Equal(ThreatVector.BiometricSpoofAttempt, s.Vector);
        Assert.Equal("identity", s.AffectedModule);
    }

    [Fact]
    public void Create_WithEvidence_PopulatesDict()
    {
        var ev = new Dictionary<string, string> { ["hash"] = "abc123" };
        var s = AnomalySignal.Create(ThreatVector.StateCorruption, 0.7, "M", "d", ev);
        Assert.Equal("abc123", s.Evidence["hash"]);
    }
}

// ── SecurityCheckpoint ────────────────────────────────────────────────────────

public sealed class SecurityCheckpointTests
{
    [Fact]
    public void Create_ComputesCorrectHash()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var cp = SecurityCheckpoint.Create("u1", "mod", payload);

        var expected = SHA256.HashData(payload);
        Assert.Equal(expected, cp.PayloadHash);
    }

    [Fact]
    public void Verify_ReturnsTrueForUnmodifiedPayload()
    {
        var cp = SecurityCheckpoint.Create("u1", "mod", [10, 20, 30]);
        Assert.True(cp.Verify());
    }

    [Fact]
    public void Verify_ReturnsFalseAfterPayloadTampering()
    {
        var payload = new byte[] { 10, 20, 30 };
        var cp = SecurityCheckpoint.Create("u1", "mod", payload);

        // Tamper with the payload array after creation
        cp.Payload[0] = 0xFF;

        Assert.False(cp.Verify());
    }

    [Fact]
    public void Create_StampsNewGuid()
    {
        var cp1 = SecurityCheckpoint.Create("u1", "mod", [1]);
        var cp2 = SecurityCheckpoint.Create("u1", "mod", [1]);
        Assert.NotEqual(cp1.Id, cp2.Id);
    }
}

// ── SecurityResponse ──────────────────────────────────────────────────────────

public sealed class SecurityResponseTests
{
    [Fact]
    public void NoAction_SetsCorrectKind()
    {
        var r = SecurityResponse.NoAction(Guid.NewGuid(), "low confidence");
        Assert.Equal(SecurityResponseKind.NoAction, r.Kind);
        Assert.Empty(r.AppliedActions);
        Assert.Null(r.RestoredCheckpoint);
    }

    [Fact]
    public void ForKeyRotation_SetsCorrectKind()
    {
        var r = SecurityResponse.ForKeyRotation(Guid.NewGuid(), "rotating");
        Assert.Equal(SecurityResponseKind.KeyRotation, r.Kind);
    }

    [Fact]
    public void ForRollback_SetsRestoredCheckpoint()
    {
        var cp = SecurityCheckpoint.Create("u1", "mod", [1, 2]);
        var r  = SecurityResponse.ForRollback(Guid.NewGuid(), cp);
        Assert.Equal(SecurityResponseKind.StateRollback, r.Kind);
        Assert.Equal(cp, r.RestoredCheckpoint);
    }

    [Fact]
    public void Composite_ListsAllActions()
    {
        var actions = new[]
        {
            SecurityResponseKind.KeyRotation,
            SecurityResponseKind.MeshIsolationSignal,
            SecurityResponseKind.StateRollback
        };
        var r = SecurityResponse.Composite(Guid.NewGuid(), actions, "multi-action");
        Assert.Equal(SecurityResponseKind.Composite, r.Kind);
        Assert.Equal(3, r.AppliedActions.Count);
    }
}

// ── DefaultSecurityWatchdog ───────────────────────────────────────────────────

public sealed class DefaultSecurityWatchdogTests
{
    private static readonly DefaultSecurityWatchdog Watchdog = new();

    [Fact]
    public async Task LowConfidence_ReturnsNoAction()
    {
        var signal = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.1, "M", "d");
        var response = await Watchdog.OnAnomalyDetectedAsync(signal);
        Assert.Equal(SecurityResponseKind.NoAction, response.Kind);
    }

    [Fact]
    public async Task MidConfidence_ReturnsKeyRotation()
    {
        var signal = AnomalySignal.Create(ThreatVector.AgentPatchRejected, 0.45, "M", "d");
        var response = await Watchdog.OnAnomalyDetectedAsync(signal);
        Assert.Equal(SecurityResponseKind.KeyRotation, response.Kind);
    }

    [Fact]
    public async Task HighConfidence_ReturnsComposite()
    {
        var signal = AnomalySignal.Create(ThreatVector.NetworkPivot, 0.9, "M", "pivot");
        var response = await Watchdog.OnAnomalyDetectedAsync(signal);
        Assert.Equal(SecurityResponseKind.Composite, response.Kind);
        Assert.Contains(SecurityResponseKind.MeshIsolationSignal, response.AppliedActions);
    }

    [Fact]
    public async Task HighConfidenceWithValidCheckpoint_IncludesRollback()
    {
        var signal = AnomalySignal.Create(ThreatVector.ControlFlowDrift, 0.95, "M", "cfd");
        var checkpoint = SecurityCheckpoint.Create("u1", "M", [1, 2, 3]);
        var response = await Watchdog.OnAnomalyDetectedAsync(signal, checkpoint);

        Assert.Equal(SecurityResponseKind.Composite, response.Kind);
        Assert.Contains(SecurityResponseKind.StateRollback, response.AppliedActions);
        Assert.Equal(checkpoint, response.RestoredCheckpoint);
    }

    [Fact]
    public async Task TamperedCheckpoint_NotRestored()
    {
        var signal = AnomalySignal.Create(ThreatVector.StateCorruption, 0.95, "M", "desc");
        var cp = SecurityCheckpoint.Create("u1", "M", [1, 2, 3]);
        cp.Payload[0] = 0xFF; // tamper

        var response = await Watchdog.OnAnomalyDetectedAsync(signal, cp);

        // Tampered checkpoint should not appear in response
        Assert.Null(response.RestoredCheckpoint);
    }

    [Fact]
    public async Task SignalId_MatchesResponseSignalId()
    {
        var signal = AnomalySignal.Create(ThreatVector.PrivilegeEscalation, 0.8, "M", "pe");
        var response = await Watchdog.OnAnomalyDetectedAsync(signal);
        Assert.Equal(signal.Id, response.SignalId);
    }
}

// ── UhidKeyRing ───────────────────────────────────────────────────────────────

public sealed class UhidKeyRingTests
{
    [Fact]
    public void GenerateFresh_CreatesNonEmptyPublicKey()
    {
        using var ring = UhidKeyRing.GenerateFresh("u1");
        Assert.NotEmpty(ring.PublicKeyDer);
        Assert.False(ring.IsRevoked);
    }

    [Fact]
    public void Sign_AndVerify_RoundTrip()
    {
        using var ring = UhidKeyRing.GenerateFresh("u1");
        byte[] data = [1, 2, 3, 4];
        var sig = ring.Sign(data);
        Assert.True(ring.Verify(data, sig));
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedData()
    {
        using var ring = UhidKeyRing.GenerateFresh("u1");
        byte[] data = [1, 2, 3];
        var sig = ring.Sign(data);
        Assert.False(ring.Verify([9, 9, 9], sig));
    }

    [Fact]
    public void Revoke_PreventsSign()
    {
        using var ring = UhidKeyRing.GenerateFresh("u1");
        ring.Revoke();
        Assert.True(ring.IsRevoked);
        Assert.Throws<InvalidOperationException>(() => ring.Sign([1]));
    }

    [Fact]
    public void Revoke_AllowsVerifyOfPriorSignature()
    {
        using var ring = UhidKeyRing.GenerateFresh("u1");
        byte[] data = [5, 6, 7];
        var sig = ring.Sign(data);
        ring.Revoke();
        // Prior signatures must still be verifiable after revocation
        Assert.True(ring.Verify(data, sig));
    }

    [Fact]
    public void Rotate_ReturnsNewRingWithFreshId()
    {
        using var original = UhidKeyRing.GenerateFresh("u1");
        var originalId = original.RingId;
        using var rotated = original.Rotate();

        Assert.NotEqual(originalId, rotated.RingId);
        Assert.True(original.IsRevoked);
        Assert.False(rotated.IsRevoked);
    }

    [Fact]
    public void TwoFreshRings_HaveDifferentPublicKeys()
    {
        using var r1 = UhidKeyRing.GenerateFresh("u1");
        using var r2 = UhidKeyRing.GenerateFresh("u1");
        Assert.NotEqual(r1.PublicKeyDer, r2.PublicKeyDer);
    }
}
