// ILocalIndicatorCorpus.cs
//
// The ONLY data source an antibody consults: a local, read-only indicator set the
// device already carries. No live feeds, no network, no Google resolvers. The
// default implementation (EmptyIndicatorCorpus) is empty — nothing ships loose.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// A local, offline indicator corpus. Assessors look indicators up here and nowhere
/// else — there is no network path in this library. A host supplies a corpus loaded
/// from a local (ideally signed) dataset the user carries; the shipped default is
/// <see cref="EmptyIndicatorCorpus"/>, which contains nothing.
/// </summary>
public interface ILocalIndicatorCorpus
{
    /// <summary>
    /// Looks up a normalized indicator. Returns an <see cref="IndicatorMatch"/> if the
    /// corpus flags it, or <c>null</c> if it is not present. Implementations must
    /// return <c>null</c> for "not found" and never fabricate a verdict.
    /// </summary>
    /// <param name="kind">The indicator kind.</param>
    /// <param name="normalizedValue">
    /// The canonical key: lowercased for network indicators and file hashes, a SHA-256
    /// hex digest for identity indicators.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IndicatorMatch?> LookupAsync(
        IndicatorKind kind,
        string normalizedValue,
        CancellationToken ct = default);
}
