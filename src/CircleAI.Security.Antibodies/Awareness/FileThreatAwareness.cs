// FileThreatAwareness.cs
//
// "Is a file the user is about to open known-bad?" — the malware-awareness antibody
// (reference shape: malwoverview), reframed as a pre-open warning for the user's own
// downloads. Assess-by-hash against the local corpus; warn, never act.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// Assesses a file the user is about to open against the device's local threat set,
/// so the user can be warned before opening something known-bad.
/// </summary>
public interface IFileThreatAwareness
{
    /// <summary>
    /// Assesses <paramref name="artifact"/> by its hash and returns a defensive
    /// verdict plus guidance. Never opens, quarantines, or transmits the file.
    /// </summary>
    Task<ThreatAwarenessResult> InspectAsync(FileArtifact artifact, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IFileThreatAwareness"/>: a pure SHA-256 lookup against an
/// <see cref="ILocalIndicatorCorpus"/>. Offline, dependency-free, and safe on a
/// low-end device. With the default <see cref="EmptyIndicatorCorpus"/> every file
/// returns "no known threat".
/// </summary>
public sealed class FileThreatAwarenessAssessor : IFileThreatAwareness
{
    private const string Source = "local indicator corpus";
    private const IndicatorKind Kind = IndicatorKind.FileHashSha256;

    private readonly ILocalIndicatorCorpus _corpus;
    private readonly TimeProvider _clock;

    /// <summary>Creates the assessor over a local corpus.</summary>
    public FileThreatAwarenessAssessor(ILocalIndicatorCorpus corpus, TimeProvider? timeProvider = null)
    {
        _corpus = corpus ?? throw new ArgumentNullException(nameof(corpus));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<ThreatAwarenessResult> InspectAsync(FileArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (string.IsNullOrWhiteSpace(artifact.Sha256Hex))
        {
            return ThreatAwarenessResult.Inconclusive(Kind, Source,
                "The file had no usable SHA-256 hash to check. Treat it with caution and only open files you trust.",
                _clock);
        }

        string key = artifact.Sha256Hex.Trim().ToLowerInvariant();
        IndicatorMatch? match = await _corpus.LookupAsync(Kind, key, ct).ConfigureAwait(false);

        if (match is null)
        {
            return ThreatAwarenessResult.NoKnownThreat(Kind, Source,
                $"“{artifact.FileName}” did not match any known-bad signature in your device's local " +
                "threat set. Only open files you trust — a clean check is not a guarantee.",
                _clock);
        }

        return match.Verdict switch
        {
            ThreatAwarenessVerdict.KnownBad => ThreatAwarenessResult.KnownBad(Kind, match.Source,
                $"“{artifact.FileName}” matches a known-bad signature in your local threat set: {match.Note}",
                $"Do not open or run “{artifact.FileName}”. {match.ProtectiveGuidance}",
                _clock),

            ThreatAwarenessVerdict.Suspicious => ThreatAwarenessResult.Suspicious(Kind, match.Source,
                $"“{artifact.FileName}” matches a suspicious signature in your local threat set: {match.Note}",
                $"Be very cautious with “{artifact.FileName}”. {match.ProtectiveGuidance}",
                _clock),

            ThreatAwarenessVerdict.NoKnownThreat => ThreatAwarenessResult.NoKnownThreat(Kind, match.Source,
                $"“{artifact.FileName}” is recorded as benign in your local set, but stay cautious with files you did not expect.",
                _clock),

            _ => ThreatAwarenessResult.Inconclusive(Kind, match.Source,
                $"The local set has an entry for “{artifact.FileName}” but no clear verdict. Treat it with caution.",
                _clock),
        };
    }
}
