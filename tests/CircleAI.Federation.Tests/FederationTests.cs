// FederationTests.cs
//
// End-to-end tests for CircleAI.Federation: FederationRound, ModelDelta,
// FederatedAveraging helper, and the InMemoryFederationAggregator reference
// implementation.

using System.Buffers.Binary;
using CircleAI.Federation;
using Xunit;

namespace CircleAI.Federation.Tests;

// ── FederationRound invariants ────────────────────────────────────────────────

public sealed class FederationRoundTests
{
    [Fact]
    public async Task NewRound_StartsInOpenStatus()
    {
        var agg = new InMemoryFederationAggregator(_ => true);

        var round = await agg.OpenRoundAsync(
            modelId: "m",
            fromVersion: "1.0.0",
            toVersion: "1.1.0",
            minParticipants: 2,
            maxParticipants: 5);

        Assert.Equal(RoundStatus.Open, round.Status);
        Assert.Equal(0, round.CurrentParticipantCount);
        Assert.Null(round.CommittedAt);
        Assert.NotEqual(Guid.Empty, round.Id);
    }
}

// ── InMemoryFederationAggregator ──────────────────────────────────────────────

public sealed class InMemoryFederationAggregatorTests
{
    [Fact]
    public async Task OpenRoundAsync_AssignsUniqueIds()
    {
        var agg = new InMemoryFederationAggregator(_ => true);

        var r1 = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", 1, 5);
        var r2 = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", 1, 5);

        Assert.NotEqual(r1.Id, r2.Id);
        Assert.Equal(2, agg.RoundCount);
    }

    [Fact]
    public async Task TryCommitAsync_BelowMinParticipants_ReturnsNull()
    {
        var agg = new InMemoryFederationAggregator(_ => true);
        var round = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", minParticipants: 3, maxParticipants: 10);

        await agg.SubmitDeltaAsync(MakeDelta(round, [1.0f], samples: 10));
        await agg.SubmitDeltaAsync(MakeDelta(round, [2.0f], samples: 10));

        var payload = await agg.TryCommitAsync(round.Id);

        Assert.Null(payload);
        var snapshot = await agg.GetRoundAsync(round.Id);
        Assert.NotEqual(RoundStatus.Committed, snapshot.Status);
        Assert.Equal(2, snapshot.CurrentParticipantCount);
    }

    [Fact]
    public async Task TryCommitAsync_AtMinParticipants_ReturnsAggregatedPayload()
    {
        var agg = new InMemoryFederationAggregator(_ => true);
        var round = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", minParticipants: 2, maxParticipants: 10);

        await agg.SubmitDeltaAsync(MakeDelta(round, [1.0f], samples: 10));
        await agg.SubmitDeltaAsync(MakeDelta(round, [3.0f], samples: 10));

        var payload = await agg.TryCommitAsync(round.Id);

        Assert.NotNull(payload);
        var floats = FederatedAveraging.DecodeFloats(payload!);
        Assert.Single(floats);
        Assert.Equal(2.0f, floats[0], 5);

        var snapshot = await agg.GetRoundAsync(round.Id);
        Assert.Equal(RoundStatus.Committed, snapshot.Status);
        Assert.NotNull(snapshot.CommittedAt);
    }

    [Fact]
    public async Task SubmitDeltaAsync_EmptyPayload_IsRejected()
    {
        var agg = new InMemoryFederationAggregator(_ => true);
        var round = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", minParticipants: 1, maxParticipants: 10);

        var empty = new ModelDelta(
            Id: Guid.NewGuid(),
            RoundId: round.Id,
            ContributorUhid: "uhid-1",
            ModelId: "m",
            FromVersion: "1.0.0",
            DeltaPayload: Array.Empty<byte>(),
            SampleCount: 10,
            Signature: Array.Empty<byte>(),
            SubmittedAt: DateTimeOffset.UtcNow);

        await agg.SubmitDeltaAsync(empty);

        var snapshot = await agg.GetRoundAsync(round.Id);
        Assert.Equal(0, snapshot.CurrentParticipantCount);

        var payload = await agg.TryCommitAsync(round.Id);
        Assert.Null(payload);
    }

    [Fact]
    public async Task SignatureValidator_AlwaysFalse_IgnoresAllDeltas()
    {
        var agg = new InMemoryFederationAggregator(signatureValidator: _ => false);
        var round = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", minParticipants: 1, maxParticipants: 10);

        await agg.SubmitDeltaAsync(MakeDelta(round, [1.0f], samples: 10));
        await agg.SubmitDeltaAsync(MakeDelta(round, [2.0f], samples: 10));

        var payload = await agg.TryCommitAsync(round.Id);
        Assert.Null(payload);
    }

    [Fact]
    public async Task MaxParticipants_Enforced_ExtrasThrow()
    {
        var agg = new InMemoryFederationAggregator(_ => true);
        var round = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", minParticipants: 1, maxParticipants: 2);

        await agg.SubmitDeltaAsync(MakeDelta(round, [1.0f], samples: 10));
        await agg.SubmitDeltaAsync(MakeDelta(round, [2.0f], samples: 10));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agg.SubmitDeltaAsync(MakeDelta(round, [3.0f], samples: 10)));
    }

    [Fact]
    public async Task TryCommitAsync_IsIdempotent()
    {
        var agg = new InMemoryFederationAggregator(_ => true);
        var round = await agg.OpenRoundAsync("m", "1.0.0", "1.1.0", minParticipants: 2, maxParticipants: 10);

        await agg.SubmitDeltaAsync(MakeDelta(round, [1.0f], samples: 10));
        await agg.SubmitDeltaAsync(MakeDelta(round, [3.0f], samples: 10));

        var first = await agg.TryCommitAsync(round.Id);
        var second = await agg.TryCommitAsync(round.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetRoundAsync_UnknownRound_Throws()
    {
        var agg = new InMemoryFederationAggregator(_ => true);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => agg.GetRoundAsync(Guid.NewGuid()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ModelDelta MakeDelta(FederationRound round, float[] values, int samples)
    {
        return new ModelDelta(
            Id: Guid.NewGuid(),
            RoundId: round.Id,
            ContributorUhid: $"uhid-{Guid.NewGuid()}",
            ModelId: round.ModelId,
            FromVersion: round.FromVersion,
            DeltaPayload: FederatedAveraging.EncodeFloats(values),
            SampleCount: samples,
            Signature: new byte[] { 0x01, 0x02, 0x03 },
            SubmittedAt: DateTimeOffset.UtcNow);
    }
}

// ── FederatedAveraging helper ─────────────────────────────────────────────────

public sealed class FederatedAveragingTests
{
    [Fact]
    public void EqualWeights_AveragesArithmetically()
    {
        var deltas = new List<ModelDelta>
        {
            MakeRawDelta([1.0f], 10),
            MakeRawDelta([3.0f], 10),
        };

        var payload = FederatedAveraging.Average(deltas);
        var floats = FederatedAveraging.DecodeFloats(payload);

        Assert.Single(floats);
        Assert.Equal(2.0f, floats[0], 5);
    }

    [Fact]
    public void DifferentSampleCounts_WeightedAverage()
    {
        // [1.0] with 10 samples + [5.0] with 30 samples → (10*1 + 30*5) / 40 = 160/40 = 4.0
        var deltas = new List<ModelDelta>
        {
            MakeRawDelta([1.0f], 10),
            MakeRawDelta([5.0f], 30),
        };

        var payload = FederatedAveraging.Average(deltas);
        var floats = FederatedAveraging.DecodeFloats(payload);

        Assert.Single(floats);
        Assert.Equal(4.0f, floats[0], 5);
    }

    [Fact]
    public void LengthMismatch_Throws()
    {
        var deltas = new List<ModelDelta>
        {
            MakeRawDelta([1.0f, 2.0f], 10),
            MakeRawDelta([1.0f], 10),
        };

        Assert.Throws<ArgumentException>(() => FederatedAveraging.Average(deltas));
    }

    [Fact]
    public void EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => FederatedAveraging.Average(new List<ModelDelta>()));
    }

    [Fact]
    public void MultiElementArray_AveragesElementWise()
    {
        var deltas = new List<ModelDelta>
        {
            MakeRawDelta([1.0f, 2.0f, 3.0f], 10),
            MakeRawDelta([3.0f, 4.0f, 5.0f], 10),
        };

        var payload = FederatedAveraging.Average(deltas);
        var floats = FederatedAveraging.DecodeFloats(payload);

        Assert.Equal(3, floats.Length);
        Assert.Equal(2.0f, floats[0], 5);
        Assert.Equal(3.0f, floats[1], 5);
        Assert.Equal(4.0f, floats[2], 5);
    }

    [Fact]
    public void EncodeFloats_IsLittleEndian()
    {
        var encoded = FederatedAveraging.EncodeFloats([1.0f]);
        Assert.Equal(4, encoded.Length);

        // Round-trip via BinaryPrimitives to confirm endianness contract.
        var decoded = BinaryPrimitives.ReadSingleLittleEndian(encoded);
        Assert.Equal(1.0f, decoded);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ModelDelta MakeRawDelta(float[] values, int samples)
    {
        return new ModelDelta(
            Id: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            ContributorUhid: "uhid",
            ModelId: "m",
            FromVersion: "1.0.0",
            DeltaPayload: FederatedAveraging.EncodeFloats(values),
            SampleCount: samples,
            Signature: Array.Empty<byte>(),
            SubmittedAt: DateTimeOffset.UtcNow);
    }
}
