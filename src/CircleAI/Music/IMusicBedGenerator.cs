namespace CircleAI.Music;

/// <summary>
/// Generates a short music bed from a <see cref="MusicSpec"/>.
/// </summary>
/// <remarks>
/// This is the seam that lets a real, catalogue-driven neural music model
/// (downloaded later) replace the built-in procedural synthesiser without any
/// caller change. Two implementations ship today:
/// <list type="bullet">
///   <item><see cref="ProceduralMusicBedGenerator"/> — the genuinely-working,
///   pure-managed, always-offline fallback.</item>
///   <item><see cref="NullMusicBedGenerator"/> — returns correctly-formatted
///   silence, for tests and "music disabled" modes.</item>
/// </list>
/// A future neural implementation lives in its own project (so it, not this
/// library, carries the inference dependency) and is injected through
/// <see cref="MusicBedGeneratorResolver"/>.
/// </remarks>
public interface IMusicBedGenerator
{
    /// <summary>Which backend this generator represents.</summary>
    MusicBedBackend Backend { get; }

    /// <summary>
    /// Produce a complete music bed for <paramref name="spec"/>.
    /// </summary>
    /// <param name="spec">Mood, tempo, duration and key of the desired bed.</param>
    /// <param name="cancellationToken">Cancels a long synthesis.</param>
    /// <returns>
    /// A <see cref="MusicBed"/> whose PCM buffer is ready to write as a WAV
    /// artifact. Implementations never return <c>null</c>; if they cannot honour
    /// the spec they throw rather than hand back a fake.
    /// </returns>
    Task<MusicBed> GenerateAsync(MusicSpec spec, CancellationToken cancellationToken = default);
}
