namespace CircleAI.Music;

/// <summary>
/// The twelve equal-tempered pitch classes. The integer value is the semitone
/// offset above C, so it maps directly onto MIDI note arithmetic.
/// </summary>
public enum PitchClass
{
    /// <summary>C.</summary>
    C = 0,
    /// <summary>C sharp / D flat.</summary>
    CSharp,
    /// <summary>D.</summary>
    D,
    /// <summary>D sharp / E flat.</summary>
    DSharp,
    /// <summary>E.</summary>
    E,
    /// <summary>F.</summary>
    F,
    /// <summary>F sharp / G flat.</summary>
    FSharp,
    /// <summary>G.</summary>
    G,
    /// <summary>G sharp / A flat.</summary>
    GSharp,
    /// <summary>A.</summary>
    A,
    /// <summary>A sharp / B flat.</summary>
    ASharp,
    /// <summary>B.</summary>
    B,
}

/// <summary>
/// The scale (mode) a bed is built from. Determines which notes are consonant
/// and therefore the character of the harmony.
/// </summary>
public enum Scale
{
    /// <summary>Ionian major — bright, resolved.</summary>
    Major = 0,

    /// <summary>Natural minor (Aeolian) — darker, melancholic.</summary>
    Minor,

    /// <summary>Dorian — minor with a raised sixth, gently jazzy.</summary>
    Dorian,

    /// <summary>Five-note major pentatonic — open, never dissonant.</summary>
    MajorPentatonic,

    /// <summary>Five-note minor pentatonic — bluesy, safe.</summary>
    MinorPentatonic,
}

/// <summary>
/// A musical key: a tonic <see cref="PitchClass"/> plus a <see cref="Scale"/>.
/// </summary>
/// <param name="Root">The tonic pitch class.</param>
/// <param name="Scale">The scale built on that tonic.</param>
public sealed record MusicalKey(PitchClass Root, Scale Scale)
{
    /// <summary>C major — the neutral default.</summary>
    public static readonly MusicalKey CMajor = new(PitchClass.C, Scale.Major);

    /// <summary>A minor — relative minor of C, for reflective / cinematic moods.</summary>
    public static readonly MusicalKey AMinor = new(PitchClass.A, Scale.Minor);

    /// <summary>D minor.</summary>
    public static readonly MusicalKey DMinor = new(PitchClass.D, Scale.Minor);

    /// <summary>G major.</summary>
    public static readonly MusicalKey GMajor = new(PitchClass.G, Scale.Major);

    /// <summary>C major pentatonic — for playful beds.</summary>
    public static readonly MusicalKey CMajorPentatonic = new(PitchClass.C, Scale.MajorPentatonic);

    /// <inheritdoc />
    public override string ToString() => $"{Root} {Scale}";
}
