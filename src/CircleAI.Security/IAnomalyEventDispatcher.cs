// IAnomalyEventDispatcher.cs
//
// Safe-by-default composer around ISecurityWatchdog. Mirrors
// Bhengu.Finance.Payments.Core.Webhooks.IWebhookEventDispatcher.
//
// The bare ISecurityWatchdog.OnAnomalyDetectedAsync path requires the
// caller to verify the signal (origin trust, schema, threshold gate) and
// dedupe (by id, by composite hash) themselves. The dispatcher folds
// verify -> dedup -> invoke into one call so a production consumer cannot
// accidentally accept an unverified or replayed signal.

namespace CircleAI.Security;

/// <summary>
/// Verify, dedup, and dispatch an <see cref="AnomalySignal"/> in a single
/// call. Returns a <see cref="AnomalyDispatchOutcome"/> describing what
/// happened — no exception is thrown on rejection so the caller can branch
/// on the outcome without try/catch.
/// </summary>
public interface IAnomalyEventDispatcher
{
    /// <summary>
    /// Runs the verification pipeline configured on this dispatcher
    /// (origin trust, optional signature check, confidence threshold) and,
    /// when all gates pass, hands the signal to the wrapped
    /// <see cref="ISecurityWatchdog"/>. Returns the dispatch outcome along
    /// with the watchdog response if invocation was reached.
    /// </summary>
    Task<AnomalyDispatchResult> VerifyAndDispatchAsync(
        AnomalySignal signal,
        SecurityCheckpoint? checkpoint = null,
        CancellationToken ct = default);
}

/// <summary>Outcome of a <see cref="IAnomalyEventDispatcher.VerifyAndDispatchAsync"/> call.</summary>
public enum AnomalyDispatchOutcome
{
    /// <summary>Signal accepted; watchdog was invoked.</summary>
    Dispatched = 0,
    /// <summary>Signal id was already seen — deduped silently.</summary>
    Duplicate = 1,
    /// <summary>Confidence was below the configured threshold — ignored.</summary>
    BelowThreshold = 2,
    /// <summary>Signal failed the origin/signature verification step.</summary>
    Unverified = 3,
    /// <summary>Cancellation token tripped before dispatch.</summary>
    Cancelled = 4,
}

/// <summary>Result of a dispatch attempt.</summary>
/// <param name="Outcome">What the dispatcher did with the signal.</param>
/// <param name="Response">
/// The watchdog response, when <see cref="Outcome"/> is
/// <see cref="AnomalyDispatchOutcome.Dispatched"/>. <c>null</c> otherwise.
/// </param>
public sealed record AnomalyDispatchResult(
    AnomalyDispatchOutcome Outcome,
    SecurityResponse? Response);

/// <summary>
/// Default in-process dispatcher. Threshold-gated, id-deduped, no signature
/// verification (configure your own by composing this with a
/// <c>SignatureVerifyingDispatcher</c> wrapper when running over an untrusted
/// transport).
/// </summary>
public sealed class DefaultAnomalyEventDispatcher : IAnomalyEventDispatcher
{
    private readonly ISecurityWatchdog _watchdog;
    private readonly double _minimumConfidence;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _seen = new();

    /// <summary>
    /// Creates the dispatcher.
    /// </summary>
    /// <param name="watchdog">The watchdog to forward verified signals to.</param>
    /// <param name="minimumConfidence">
    /// Drop signals whose <see cref="AnomalySignal.Confidence"/> is below
    /// this value. Default 0.30 — matches the default watchdog rotation
    /// threshold so signals that would have been no-ops aren't even
    /// dispatched.
    /// </param>
    public DefaultAnomalyEventDispatcher(ISecurityWatchdog watchdog, double minimumConfidence = 0.30)
    {
        ArgumentNullException.ThrowIfNull(watchdog);
        _watchdog = watchdog;
        _minimumConfidence = Math.Clamp(minimumConfidence, 0.0, 1.0);
    }

    /// <inheritdoc/>
    public async Task<AnomalyDispatchResult> VerifyAndDispatchAsync(
        AnomalySignal signal,
        SecurityCheckpoint? checkpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (ct.IsCancellationRequested)
            return new AnomalyDispatchResult(AnomalyDispatchOutcome.Cancelled, null);

        if (signal.Confidence < _minimumConfidence)
            return new AnomalyDispatchResult(AnomalyDispatchOutcome.BelowThreshold, null);

        if (!_seen.TryAdd(signal.Id, 0))
            return new AnomalyDispatchResult(AnomalyDispatchOutcome.Duplicate, null);

        var response = await _watchdog.OnAnomalyDetectedAsync(signal, checkpoint, ct)
                                       .ConfigureAwait(false);
        return new AnomalyDispatchResult(AnomalyDispatchOutcome.Dispatched, response);
    }
}
