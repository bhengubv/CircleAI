namespace CircleAI.Music;

/// <summary>
/// A no-op <see cref="IMusicBedGenerator"/> that returns a correctly-formatted
/// buffer of silence for the requested duration. Used as a safe default when
/// music is disabled, and as a deterministic fixture in tests.
/// </summary>
/// <remarks>
/// It still validates the spec and produces a real, saveable WAV — just a silent
/// one — so callers can treat "music off" identically to "music on" without
/// null checks or special cases.
/// </remarks>
public sealed class NullMusicBedGenerator : IMusicBedGenerator
{
    /// <inheritdoc />
    public MusicBedBackend Backend => MusicBedBackend.Procedural;

    /// <inheritdoc />
    public Task<MusicBed> GenerateAsync(MusicSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        spec.Validate();

        AudioPcmFormat format = spec.Format ?? AudioPcmFormat.BedDefault;
        int frames = (int)Math.Round(spec.Duration.TotalSeconds * format.SampleRate);
        var silence = new byte[frames * format.BlockAlign]; // zero-filled = silence

        var bed = new MusicBed(silence, format, spec, MusicBedBackend.Procedural, spec.Duration);
        return Task.FromResult(bed);
    }
}
