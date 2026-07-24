namespace CircleAI.Music;

/// <summary>
/// Describes a linear PCM audio format for a generated music bed.
/// </summary>
/// <param name="SampleRate">Samples per second per channel (e.g. 44100).</param>
/// <param name="Channels">Interleaved channel count (1 = mono, 2 = stereo).</param>
/// <param name="BitsPerSample">Bit depth of each sample. The procedural
/// synthesiser produces signed 16-bit PCM; other depths are reserved for a
/// future neural backend.</param>
/// <remarks>
/// This intentionally matches the shape of <c>CircleAI.Voice.AudioFormat</c>
/// (SampleRate / Channels / BitsPerSample) but is redeclared here so that
/// CircleAI.Music takes no project dependency — it stays a drop-in, pure-managed
/// library that runs on the lowest-end de-Googled Android device.
/// </remarks>
public sealed record AudioPcmFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>Default bed format: 44.1 kHz, mono, 16-bit — CD-rate, half the
    /// size of stereo, and universally decodable.</summary>
    public static readonly AudioPcmFormat BedDefault = new(44_100, 1, 16);

    /// <summary>Compact bed format: 22.05 kHz, mono, 16-bit — half the bytes of
    /// <see cref="BedDefault"/> for tiny clips on constrained storage.</summary>
    public static readonly AudioPcmFormat Compact = new(22_050, 1, 16);

    /// <summary>Stereo CD format: 44.1 kHz, stereo, 16-bit.</summary>
    public static readonly AudioPcmFormat CdStereo = new(44_100, 2, 16);

    /// <summary>Bytes occupied by one sample of one channel.</summary>
    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>Bytes occupied by one interleaved frame (all channels).</summary>
    public int BlockAlign => Channels * BytesPerSample;

    /// <summary>Bytes per second of audio — used for the WAV header.</summary>
    public int ByteRate => SampleRate * BlockAlign;
}
