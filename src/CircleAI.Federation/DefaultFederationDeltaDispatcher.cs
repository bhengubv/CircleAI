// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.
//
// DefaultFederationDeltaDispatcher.cs
//
// The safe-by-default composer promised by CircleAI.Federation/README.md: wraps
// an IFederationAggregator plus a signature validator so a production consumer
// cannot skip verify -> dedup -> submit. No exception is thrown on rejection —
// the caller branches on the returned DeltaDispatchOutcome.

namespace CircleAI.Federation;

using System.Collections.Concurrent;

/// <summary>
/// Reference <see cref="IFederationDeltaDispatcher"/>. Composes signature
/// verification, replay de-duplication, and submission over an
/// <see cref="IFederationAggregator"/> in a single call so no step can be
/// skipped.
/// </summary>
public sealed class DefaultFederationDeltaDispatcher : IFederationDeltaDispatcher
{
    private readonly IFederationAggregator _aggregator;
    private readonly Func<ModelDelta, bool> _signatureValidator;
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();

    /// <summary>
    /// Constructs the dispatcher.
    /// </summary>
    /// <param name="aggregator">The round coordinator deltas are submitted to.</param>
    /// <param name="signatureValidator">
    /// Returns <c>true</c> when the delta's signature verifies against the
    /// contributor's UHID key. Pass <c>_ =&gt; true</c> only in tests where
    /// signatures are not the subject of test.
    /// </param>
    public DefaultFederationDeltaDispatcher(
        IFederationAggregator aggregator,
        Func<ModelDelta, bool> signatureValidator)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(signatureValidator);
        _aggregator = aggregator;
        _signatureValidator = signatureValidator;
    }

    /// <inheritdoc/>
    public async Task<DeltaDispatchOutcome> VerifyAndSubmitAsync(
        ModelDelta delta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);

        // 1. Verify the signature first — a forged or unsigned delta never touches the round.
        if (!_signatureValidator(delta))
        {
            return DeltaDispatchOutcome.SignatureInvalid;
        }

        // 2. De-duplicate: atomically claim the delta id; a replay loses the race.
        if (!_seen.TryAdd(delta.Id, 0))
        {
            return DeltaDispatchOutcome.Duplicate;
        }

        // 3. Submit, translating the aggregator's exceptions into outcomes so the
        //    caller can branch on the result without a try/catch of its own.
        try
        {
            await _aggregator.SubmitDeltaAsync(delta, ct).ConfigureAwait(false);
            return DeltaDispatchOutcome.Accepted;
        }
        catch (KeyNotFoundException)
        {
            _seen.TryRemove(delta.Id, out _);
            return DeltaDispatchOutcome.RoundUnknown;
        }
        catch (InvalidOperationException)
        {
            _seen.TryRemove(delta.Id, out _);
            return DeltaDispatchOutcome.RoundClosed;
        }
    }
}
