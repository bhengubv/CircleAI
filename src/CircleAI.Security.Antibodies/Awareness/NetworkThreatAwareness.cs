// NetworkThreatAwareness.cs
//
// "Is a URL / IP / domain the user is about to trust known-bad?" — the network
// indicator antibody (reference shape: deepdarkCTI / ipblocklist), reframed as a
// pre-connect warning for the user. Local-corpus lookup only; warn, never act.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// Assesses a network location the user is about to connect to against the device's
/// local threat set, so the user can be warned before they trust it.
/// </summary>
public interface INetworkThreatAwareness
{
    /// <summary>
    /// Assesses <paramref name="indicator"/> and returns a defensive verdict plus
    /// guidance. Performs no network connection of its own — the check is a pure
    /// local lookup.
    /// </summary>
    Task<ThreatAwarenessResult> InspectAsync(NetworkIndicator indicator, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="INetworkThreatAwareness"/>: normalizes the indicator and looks
/// it up in an <see cref="ILocalIndicatorCorpus"/>. Offline and dependency-free. With
/// the default <see cref="EmptyIndicatorCorpus"/> every location returns "no known threat".
/// </summary>
public sealed class NetworkThreatAwarenessAssessor : INetworkThreatAwareness
{
    private const string Source = "local indicator corpus";

    private readonly ILocalIndicatorCorpus _corpus;
    private readonly TimeProvider _clock;

    /// <summary>Creates the assessor over a local corpus.</summary>
    public NetworkThreatAwarenessAssessor(ILocalIndicatorCorpus corpus, TimeProvider? timeProvider = null)
    {
        _corpus = corpus ?? throw new ArgumentNullException(nameof(corpus));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<ThreatAwarenessResult> InspectAsync(NetworkIndicator indicator, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        IndicatorKind kind = indicator.Kind;

        string? key = IndicatorNormalizer.NormalizeNetwork(kind, indicator.Value);
        if (key is null)
        {
            return ThreatAwarenessResult.Inconclusive(kind, Source,
                "The network location could not be read. Do not connect to something you cannot verify.",
                _clock);
        }

        IndicatorMatch? match = await _corpus.LookupAsync(kind, key, ct).ConfigureAwait(false);

        if (match is null)
        {
            return ThreatAwarenessResult.NoKnownThreat(kind, Source,
                "This location did not match anything known-bad in your device's local threat set. Be careful " +
                "with links you did not expect — a clean check is not a guarantee.",
                _clock);
        }

        return match.Verdict switch
        {
            ThreatAwarenessVerdict.KnownBad => ThreatAwarenessResult.KnownBad(kind, match.Source,
                $"This location is flagged as known-bad in your local threat set: {match.Note}",
                $"Do not connect to it or enter any details. {match.ProtectiveGuidance}",
                _clock),

            ThreatAwarenessVerdict.Suspicious => ThreatAwarenessResult.Suspicious(kind, match.Source,
                $"This location is flagged as suspicious in your local threat set: {match.Note}",
                $"Avoid it unless you are certain it is genuine. {match.ProtectiveGuidance}",
                _clock),

            ThreatAwarenessVerdict.NoKnownThreat => ThreatAwarenessResult.NoKnownThreat(kind, match.Source,
                "This location is recorded as benign in your local set, but stay alert for anything unexpected.",
                _clock),

            _ => ThreatAwarenessResult.Inconclusive(kind, match.Source,
                "The local set has an entry for this location but no clear verdict. Treat it with caution.",
                _clock),
        };
    }
}
