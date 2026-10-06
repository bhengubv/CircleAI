namespace CircleAI.Music;

/// <summary>
/// The request for a music bed: <b>mood, tempo, duration and key</b>, plus
/// optional seed and output format. This is the single input to every
/// <see cref="IMusicBedGenerator"/>, procedural or neural.
/// </summary>
/// <param name="Mood">Emotional colour of the bed.</param>
/// <param name="Tempo">Tempo in beats per minute
/// (<see cref="MinTempo"/>..<see cref="MaxTempo"/>).</param>
/// <param name="Duration">How long the bed should be. Must be positive and no
/// longer than <see cref="MaxDuration"/> — beds are meant to be short.</param>
/// <param name="Key">The musical key (tonic + scale) to build the harmony on.</param>
public sealed record MusicSpec(Mood Mood, int Tempo, TimeSpan Duration, MusicalKey Key)
{
    /// <summary>Lowest accepted tempo (BPM).</summary>
    public const int MinTempo = 40;

    /// <summary>Highest accepted tempo (BPM).</summary>
    public const int MaxTempo = 240;

    /// <summary>Upper bound on bed length. Guards low-end memory: a bed is a
    /// short clip underlay, not a full track.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Deterministic variation seed. <c>0</c> (the default) means "derive a
    /// stable seed from the rest of the spec", so identical specs always yield
    /// an identical bed. Set a non-zero value to nudge the same spec into a
    /// different-but-reproducible variation.
    /// </summary>
    public int Seed { get; init; }

    /// <summary>
    /// Desired output format. <c>null</c> (default) uses
    /// <see cref="AudioPcmFormat.BedDefault"/> (44.1 kHz mono 16-bit).
    /// </summary>
    public AudioPcmFormat? Format { get; init; }

    /// <summary>
    /// Build a spec from just a mood and duration, using a mood-appropriate
    /// default tempo and key. The most convenient entry point for callers that
    /// only care about "give me something that feels like X for N seconds".
    /// </summary>
    /// <param name="mood">Desired mood.</param>
    /// <param name="duration">Desired length.</param>
    public static MusicSpec ForMood(Mood mood, TimeSpan duration) =>
        new(mood, DefaultTempo(mood), duration, DefaultKey(mood));

    /// <summary>
    /// Throw if any field is out of range. Generators call this before doing
    /// any work, so callers get a clear error instead of a broken artifact.
    /// </summary>
    /// <exception cref="ArgumentNullException"><see cref="Key"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Tempo or duration invalid.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Key);

        if (Tempo is < MinTempo or > MaxTempo)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Tempo), Tempo,
                $"Tempo must be between {MinTempo} and {MaxTempo} BPM.");
        }

        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Duration), Duration, "Duration must be positive.");
        }

        if (Duration > MaxDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Duration), Duration,
                $"Duration must not exceed {MaxDuration.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// The effective, always-non-zero seed used by procedural synthesis:
    /// the explicit <see cref="Seed"/> when set, otherwise a stable hash of the
    /// musical parameters.
    /// </summary>
    internal uint EffectiveSeed()
    {
        int raw = Seed != 0
            ? Seed
            : HashCode.Combine(Mood, Tempo, Duration, Key.Root, Key.Scale);

        // Never hand a zero seed to the PRNG (it would degenerate).
        return raw == 0 ? 0x9E3779B9u : unchecked((uint)raw);
    }

    private static int DefaultTempo(Mood mood) => mood switch
    {
        Mood.Reflective => 66,
        Mood.Cinematic => 70,
        Mood.Calm => 74,
        Mood.Warm => 86,
        Mood.Neutral => 96,
        Mood.Focus => 100,
        Mood.Corporate => 104,
        Mood.Uplifting => 114,
        Mood.Playful => 120,
        Mood.Energetic => 128,
        _ => 96,
    };

    private static MusicalKey DefaultKey(Mood mood) => mood switch
    {
        Mood.Reflective or Mood.Cinematic => MusicalKey.AMinor,
        Mood.Calm => MusicalKey.DMinor,
        Mood.Playful => MusicalKey.CMajorPentatonic,
        Mood.Uplifting => MusicalKey.GMajor,
        _ => MusicalKey.CMajor,
    };
}
