namespace CircleAI.Music;

/// <summary>
/// A generated music bed: the raw PCM plus everything needed to save it or hand
/// it to a media pipeline.
/// </summary>
/// <param name="Pcm">Interleaved little-endian PCM samples in <paramref name="Format"/>.</param>
/// <param name="Format">The PCM format of <paramref name="Pcm"/>.</param>
/// <param name="Spec">The spec this bed was generated from.</param>
/// <param name="Backend">Which engine produced it (procedural vs neural).</param>
/// <param name="Duration">Actual rendered duration.</param>
public sealed record MusicBed(
    ReadOnlyMemory<byte> Pcm,
    AudioPcmFormat Format,
    MusicSpec Spec,
    MusicBedBackend Backend,
    TimeSpan Duration)
{
    /// <summary>Wrap this bed's PCM in a complete WAV container.</summary>
    /// <returns>A ready-to-save <c>.wav</c> byte array.</returns>
    public byte[] ToWav() => WavWriter.ToWav(Pcm.Span, Format);

    /// <summary>
    /// Save this bed as a WAV file at <paramref name="path"/>, creating or
    /// overwriting it.
    /// </summary>
    /// <param name="path">Destination file path.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task WriteWavAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await WavWriter.WriteAsync(stream, Pcm, Format, cancellationToken).ConfigureAwait(false);
    }
}
