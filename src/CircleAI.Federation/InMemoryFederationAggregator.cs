// InMemoryFederationAggregator.cs
//
// Reference IFederationAggregator implementation. Stores rounds and deltas in
// process memory and performs sample-size-weighted averaging on commit.
//
// Signature verification is delegated to a caller-supplied validator so this
// package does not need to depend on UhidKeyRing directly — that keeps the
// federation API engine-agnostic and testable in isolation.

namespace CircleAI.Federation;

using System.Collections.Concurrent;

/// <summary>
/// In-process reference <see cref="IFederationAggregator"/>. Stores all round
/// and delta state in memory; not durable across process restarts. Use for
/// tests, edge devices, or as a starting point for a real implementation.
/// </summary>
public sealed class InMemoryFederationAggregator : IFederationAggregator
{
    private readonly ConcurrentDictionary<Guid, RoundState> _rounds = new();
    private readonly Func<ModelDelta, bool> _signatureValidator;

    /// <summary>
    /// Constructs the aggregator with a signature validator. Pass
    /// <c>_ =&gt; true</c> in tests where signatures are not the subject of test.
    /// </summary>
    /// <param name="signatureValidator">
    /// Returns <c>true</c> when the supplied delta's signature is valid. The
    /// aggregator drops deltas whose validator returns <c>false</c> at commit time.
    /// </param>
    public InMemoryFederationAggregator(Func<ModelDelta, bool> signatureValidator)
    {
        ArgumentNullException.ThrowIfNull(signatureValidator);
        _signatureValidator = signatureValidator;
    }

    /// <inheritdoc/>
    public Task<FederationRound> OpenRoundAsync(
        string modelId,
        string fromVersion,
        string toVersion,
        int minParticipants,
        int maxParticipants,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentException.ThrowIfNullOrEmpty(fromVersion);
        ArgumentException.ThrowIfNullOrEmpty(toVersion);
        if (minParticipants <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minParticipants), "minParticipants must be positive.");
        }
        if (maxParticipants < minParticipants)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParticipants),
                $"maxParticipants ({maxParticipants}) must be >= minParticipants ({minParticipants}).");
        }
        ct.ThrowIfCancellationRequested();

        var round = new FederationRound(
            Id: Guid.NewGuid(),
            ModelId: modelId,
            FromVersion: fromVersion,
            ToVersion: toVersion,
            MinParticipants: minParticipants,
            MaxParticipants: maxParticipants,
            CurrentParticipantCount: 0,
            Status: RoundStatus.Open,
            OpenedAt: DateTimeOffset.UtcNow,
            CommittedAt: null);

        var state = new RoundState(round);
        _rounds[round.Id] = state;
        return Task.FromResult(state.Snapshot);
    }

    /// <inheritdoc/>
    public Task SubmitDeltaAsync(ModelDelta delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ct.ThrowIfCancellationRequested();

        if (!_rounds.TryGetValue(delta.RoundId, out var state))
        {
            throw new KeyNotFoundException($"Round {delta.RoundId} is not open.");
        }

        if (delta.DeltaPayload.Length == 0)
        {
            // Treat empty payloads as invalid: do not store, do not count.
            // The aggregator does not raise — callers may legitimately submit
            // an "empty" gradient if their local data was insufficient, and we
            // want the round to remain viable.
            return Task.CompletedTask;
        }

        lock (state.Lock)
        {
            if (state.Snapshot.Status != RoundStatus.Open)
            {
                throw new InvalidOperationException(
                    $"Round {delta.RoundId} is {state.Snapshot.Status}; not accepting deltas.");
            }
            if (state.Deltas.Count >= state.Snapshot.MaxParticipants)
            {
                throw new InvalidOperationException(
                    $"Round {delta.RoundId} has reached MaxParticipants ({state.Snapshot.MaxParticipants}).");
            }

            state.Deltas.Add(delta);
            state.Snapshot = state.Snapshot with { CurrentParticipantCount = state.Deltas.Count };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<byte[]?> TryCommitAsync(Guid roundId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_rounds.TryGetValue(roundId, out var state))
        {
            throw new KeyNotFoundException($"Round {roundId} is unknown.");
        }

        lock (state.Lock)
        {
            if (state.Snapshot.Status == RoundStatus.Committed)
            {
                // Idempotent: re-return the previously committed payload.
                return Task.FromResult<byte[]?>(state.CommittedPayload);
            }
            if (state.Snapshot.Status == RoundStatus.Aborted)
            {
                return Task.FromResult<byte[]?>(null);
            }

            var validDeltas = state.Deltas.Where(_signatureValidator).ToList();
            if (validDeltas.Count < state.Snapshot.MinParticipants)
            {
                return Task.FromResult<byte[]?>(null);
            }

            state.Snapshot = state.Snapshot with { Status = RoundStatus.Aggregating };

            byte[] aggregated;
            try
            {
                aggregated = FederatedAveraging.Average(validDeltas);
            }
            catch (ArgumentException)
            {
                // Payload encoding inconsistent — fall back to the median
                // delta by SampleCount as documented in the contract.
                aggregated = FallbackMedianPayload(validDeltas);
            }

            state.CommittedPayload = aggregated;
            state.Snapshot = state.Snapshot with
            {
                Status = RoundStatus.Committed,
                CommittedAt = DateTimeOffset.UtcNow,
            };

            return Task.FromResult<byte[]?>(aggregated);
        }
    }

    /// <inheritdoc/>
    public Task<FederationRound> GetRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_rounds.TryGetValue(roundId, out var state))
        {
            throw new KeyNotFoundException($"Round {roundId} is unknown.");
        }
        lock (state.Lock)
        {
            return Task.FromResult(state.Snapshot);
        }
    }

    /// <summary>
    /// Total number of rounds currently tracked. Diagnostic only.
    /// </summary>
    public int RoundCount => _rounds.Count;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] FallbackMedianPayload(IReadOnlyList<ModelDelta> deltas)
    {
        var ordered = deltas.OrderBy(d => d.SampleCount).ToList();
        var median = ordered[ordered.Count / 2];
        var copy = new byte[median.DeltaPayload.Length];
        Buffer.BlockCopy(median.DeltaPayload, 0, copy, 0, copy.Length);
        return copy;
    }

    private sealed class RoundState
    {
        public RoundState(FederationRound initial)
        {
            Snapshot = initial;
            Deltas = new List<ModelDelta>();
            Lock = new object();
        }

        public FederationRound Snapshot { get; set; }
        public List<ModelDelta> Deltas { get; }
        public byte[]? CommittedPayload { get; set; }
        public object Lock { get; }
    }
}
