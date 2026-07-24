namespace CircleAI.Music;

/// <summary>
/// Chooses which <see cref="IMusicBedGenerator"/> serves a request. This is the
/// plug-in point for the real, catalogue-driven neural model: today it always
/// falls back to the procedural synthesiser, and the moment a neural generator
/// is injected it is preferred — with no change required at the call site.
/// </summary>
/// <remarks>
/// <para>
/// The neural generator is intentionally NOT built into this library: it needs a
/// downloaded model bundle (an InspireMusic / Amphion-class model whose hash we
/// do not yet have) and would drag the inference stack into every consumer. It
/// therefore lives in its own project, which references
/// <c>CircleAI.Inference</c> and consults <c>IModelSelector</c> to decide
/// whether a suitable model fits the device. That project constructs its
/// generator and passes it here as <c>neural</c>.
/// </para>
/// <para>
/// Until then, <see cref="Resolve"/> returns the procedural fallback so a bed
/// always exists offline — the direct analogue of the selector returning
/// <c>SelectionQuality.HeuristicFallback</c> when no model is catalogued.
/// </para>
/// </remarks>
public sealed class MusicBedGeneratorResolver
{
    private readonly IMusicBedGenerator _proceduralFallback;
    private readonly IMusicBedGenerator? _neural;

    /// <summary>
    /// Create a resolver over a mandatory procedural fallback and an optional
    /// neural generator.
    /// </summary>
    /// <param name="proceduralFallback">
    /// The always-available fallback, normally a <see cref="ProceduralMusicBedGenerator"/>.
    /// </param>
    /// <param name="neural">
    /// An optional neural generator, present only when a model bundle is on the
    /// device. When supplied it is preferred over the fallback.
    /// </param>
    public MusicBedGeneratorResolver(
        IMusicBedGenerator proceduralFallback,
        IMusicBedGenerator? neural = null)
    {
        ArgumentNullException.ThrowIfNull(proceduralFallback);
        _proceduralFallback = proceduralFallback;
        _neural = neural;
    }

    /// <summary>Whether a neural backend is currently available.</summary>
    public bool HasNeuralBackend => _neural is not null;

    /// <summary>
    /// The generator that will serve requests: the neural backend when present,
    /// otherwise the procedural fallback. Never <c>null</c>.
    /// </summary>
    public IMusicBedGenerator Resolve() => _neural ?? _proceduralFallback;

    /// <summary>Resolve a generator and produce a bed for <paramref name="spec"/>.</summary>
    public Task<MusicBed> GenerateAsync(MusicSpec spec, CancellationToken cancellationToken = default)
        => Resolve().GenerateAsync(spec, cancellationToken);

    /// <summary>
    /// A resolver backed only by the built-in procedural synthesiser — the
    /// zero-configuration, always-offline default.
    /// </summary>
    public static MusicBedGeneratorResolver CreateDefault()
        => new(new ProceduralMusicBedGenerator());
}
