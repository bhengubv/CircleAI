// InMemoryIndicatorCorpus.cs
//
// A real, thread-safe corpus a host populates from a local dataset (e.g. a signed
// file the device carries). Offline and dependency-free. Identity indicators must
// be added as SHA-256 hex digests — this corpus never expects a raw identity.

using System.Collections.Concurrent;

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// In-memory <see cref="ILocalIndicatorCorpus"/>. A host loads a local dataset into
/// it at startup; lookups are exact matches on the normalized key. Thread-safe and
/// BCL-only so it runs on any device offline.
/// </summary>
public sealed class InMemoryIndicatorCorpus : ILocalIndicatorCorpus
{
    private readonly ConcurrentDictionary<(IndicatorKind Kind, string Key), IndicatorMatch> _entries = new();

    /// <summary>
    /// Adds or replaces an indicator. For identity kinds, <paramref name="normalizedKey"/>
    /// must be the SHA-256 hex digest of the canonical identity — never a raw value.
    /// </summary>
    /// <param name="kind">The indicator kind.</param>
    /// <param name="normalizedKey">The canonical lookup key (lowercased or hashed).</param>
    /// <param name="verdict">The verdict this indicator carries.</param>
    /// <param name="note">Why the indicator is flagged.</param>
    /// <param name="protectiveGuidance">What the user should do to stay safe.</param>
    /// <param name="source">The dataset this indicator came from.</param>
    public void Add(
        IndicatorKind kind,
        string normalizedKey,
        ThreatAwarenessVerdict verdict,
        string note,
        string protectiveGuidance,
        string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectiveGuidance);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        _entries[(kind, normalizedKey)] =
            new IndicatorMatch(kind, verdict, note, protectiveGuidance, source);
    }

    /// <summary>Number of indicators currently held.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public ValueTask<IndicatorMatch?> LookupAsync(
        IndicatorKind kind,
        string normalizedValue,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(normalizedValue) &&
            _entries.TryGetValue((kind, normalizedValue), out var match))
        {
            return ValueTask.FromResult<IndicatorMatch?>(match);
        }

        return ValueTask.FromResult<IndicatorMatch?>(null);
    }
}
