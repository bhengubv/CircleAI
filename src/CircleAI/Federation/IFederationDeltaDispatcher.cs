// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Federation;

/// <summary>
/// Safe-by-default federation delta dispatcher. Verify, dedup, and submit
/// in one call so consumers cannot skip a step. Mirrors
/// <c>Bhengu.Finance.Payments.Core.Webhooks.IWebhookEventDispatcher</c>.
///
/// <para>The bare <see cref="InMemoryFederationAggregator.SubmitDeltaAsync"/>
/// path requires the caller to remember to verify signatures and check for
/// duplicate deltas. The dispatcher composes those three steps so a
/// production consumer cannot accidentally accept an unsigned or replayed
/// delta.</para>
/// </summary>
public interface IFederationDeltaDispatcher
{
    /// <summary>
    /// Verify the delta's signature, check it has not already been recorded
    /// for the round, and submit it. Returns <see cref="DeltaDispatchOutcome"/>
    /// describing what happened — no exception is thrown on rejection so the
    /// caller can branch on the outcome without try/catch.
    /// </summary>
    Task<DeltaDispatchOutcome> VerifyAndSubmitAsync(
        ModelDelta delta,
        CancellationToken ct = default);
}

/// <summary>Outcome of a <see cref="IFederationDeltaDispatcher.VerifyAndSubmitAsync"/> call.</summary>
public enum DeltaDispatchOutcome
{
    /// <summary>Delta accepted and recorded for the round.</summary>
    Accepted = 0,
    /// <summary>Signature did not verify against the contributor's UHID key.</summary>
    SignatureInvalid = 1,
    /// <summary>This delta id was already recorded for the round (replay).</summary>
    Duplicate = 2,
    /// <summary>The round id is unknown to the aggregator.</summary>
    RoundUnknown = 3,
    /// <summary>The round is not currently accepting deltas (e.g. already committed).</summary>
    RoundClosed = 4,
}
