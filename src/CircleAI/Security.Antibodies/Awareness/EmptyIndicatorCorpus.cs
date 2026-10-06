// EmptyIndicatorCorpus.cs
//
// The default corpus: it contains NOTHING and matches nothing. This is what
// "never bundled loose" looks like in code — the library ships with no indicators
// at all. A host must deliberately provide a populated corpus for any match to occur.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// An <see cref="ILocalIndicatorCorpus"/> that holds no indicators and therefore
/// matches nothing. It is the shipped default so that, out of the box, no threat
/// data is bundled and every lookup returns "no known threat". Use <see cref="Instance"/>.
/// </summary>
public sealed class EmptyIndicatorCorpus : ILocalIndicatorCorpus
{
    /// <summary>Shared singleton — this corpus is stateless and empty.</summary>
    public static EmptyIndicatorCorpus Instance { get; } = new();

    /// <summary>Prefer <see cref="Instance"/>; public only for DI containers.</summary>
    public EmptyIndicatorCorpus() { }

    /// <inheritdoc/>
    public ValueTask<IndicatorMatch?> LookupAsync(
        IndicatorKind kind,
        string normalizedValue,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IndicatorMatch?>(null);
}
