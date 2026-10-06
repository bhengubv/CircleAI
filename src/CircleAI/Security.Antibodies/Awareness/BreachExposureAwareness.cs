// BreachExposureAwareness.cs
//
// "Has the user's OWN identity turned up in a breach corpus?" — the breach/identity
// antibody (reference shape: findme), reframed to protect the user's own identity.
// The identity is hashed before any lookup; the raw value is never stored or moved.
// Presence in the local breach set is an exposure warning so the user can rotate a
// credential. Only ever about the user's own identity — never a third party's.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// Checks whether the user's own identity value appears in the device's local breach
/// set, so the user can rotate an exposed credential. Hashes the identity before
/// lookup; plaintext identities never enter the corpus.
/// </summary>
public interface IBreachExposureAwareness
{
    /// <summary>
    /// Assesses the user's own <paramref name="identity"/> for breach exposure and
    /// returns a defensive verdict plus guidance. The raw identity is hashed before
    /// any lookup and is never persisted.
    /// </summary>
    Task<ThreatAwarenessResult> InspectAsync(IdentityIndicator identity, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IBreachExposureAwareness"/>: hashes the user's identity
/// (SHA-256) and looks the digest up in an <see cref="ILocalIndicatorCorpus"/>.
/// Offline and dependency-free. With the default <see cref="EmptyIndicatorCorpus"/>
/// every identity returns "no known exposure".
/// </summary>
public sealed class BreachExposureAssessor : IBreachExposureAwareness
{
    private const string Source = "local breach set";

    private readonly ILocalIndicatorCorpus _corpus;
    private readonly TimeProvider _clock;

    /// <summary>Creates the assessor over a local corpus.</summary>
    public BreachExposureAssessor(ILocalIndicatorCorpus corpus, TimeProvider? timeProvider = null)
    {
        _corpus = corpus ?? throw new ArgumentNullException(nameof(corpus));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<ThreatAwarenessResult> InspectAsync(IdentityIndicator identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        IndicatorKind kind = identity.Kind;

        // Hash before lookup — the corpus only ever sees a digest, never the plaintext.
        string? hash = IndicatorNormalizer.NormalizeIdentityToHash(kind, identity.Value);
        if (hash is null)
        {
            return ThreatAwarenessResult.Inconclusive(kind, Source,
                "Your identity value could not be read, so nothing was looked up.",
                _clock);
        }

        IndicatorMatch? match = await _corpus.LookupAsync(kind, hash, ct).ConfigureAwait(false);

        if (match is null)
        {
            return ThreatAwarenessResult.NoKnownThreat(kind, Source,
                $"Your {Describe(kind)} was not found in your device's local breach set. New breaches appear over " +
                "time — keep using a unique, strong password and turn on 2-factor authentication anyway.",
                _clock);
        }

        // A match means the user's own identity is exposed. Honour the corpus verdict,
        // but always frame it as a rotate-now warning for the user.
        string rotate = $"Change the password for your {Describe(kind)} now, and anywhere you reused it, and turn " +
                        $"on 2-factor authentication. {match.ProtectiveGuidance}";

        return match.Verdict == ThreatAwarenessVerdict.Suspicious
            ? ThreatAwarenessResult.Suspicious(kind, match.Source,
                $"Your {Describe(kind)} may be exposed in a breach recorded in your local set: {match.Note}",
                rotate, _clock)
            : ThreatAwarenessResult.KnownBad(kind, match.Source,
                $"Your {Describe(kind)} appears in a known breach recorded in your local set: {match.Note}",
                rotate, _clock);
    }

    private static string Describe(IndicatorKind kind) => kind switch
    {
        IndicatorKind.EmailAddress => "email address",
        IndicatorKind.Username => "username",
        IndicatorKind.PhoneNumber => "phone number",
        _ => "identity",
    };
}
