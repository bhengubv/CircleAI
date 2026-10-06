namespace CircleAI.Music;

/// <summary>
/// Minimal, dependency-free music-theory maths used by the procedural
/// synthesiser: scale intervals, MIDI note numbers, equal-tempered frequencies
/// and diatonic degree/triad resolution.
/// </summary>
/// <remarks>
/// Kept internal on purpose — it is an implementation detail of
/// <see cref="ProceduralMusicBedGenerator"/>, not public surface area.
/// </remarks>
internal static class MusicTheory
{
    /// <summary>Concert-pitch reference A4 = 440 Hz at MIDI note 69.</summary>
    private const double A4Frequency = 440.0;
    private const int A4MidiNote = 69;

    /// <summary>Semitone offsets from the tonic for each supported scale.</summary>
    public static int[] Intervals(Scale scale) => scale switch
    {
        Scale.Major => [0, 2, 4, 5, 7, 9, 11],
        Scale.Minor => [0, 2, 3, 5, 7, 8, 10],
        Scale.Dorian => [0, 2, 3, 5, 7, 9, 10],
        Scale.MajorPentatonic => [0, 2, 4, 7, 9],
        Scale.MinorPentatonic => [0, 3, 5, 7, 10],
        _ => [0, 2, 4, 5, 7, 9, 11],
    };

    /// <summary>MIDI note number for a pitch class at a given octave (C4 = 60).</summary>
    public static int MidiNote(PitchClass root, int octave) =>
        ((octave + 1) * 12) + (int)root;

    /// <summary>Equal-tempered frequency (Hz) of a MIDI note number.</summary>
    public static double Frequency(int midiNote) =>
        A4Frequency * Math.Pow(2.0, (midiNote - A4MidiNote) / 12.0);

    /// <summary>
    /// Resolve a diatonic scale degree (0-based; may be negative or exceed the
    /// scale length) to an absolute MIDI note, wrapping octaves as needed.
    /// </summary>
    /// <param name="tonicMidi">MIDI note of the scale's tonic.</param>
    /// <param name="intervals">Semitone offsets from <see cref="Intervals"/>.</param>
    /// <param name="degree">Scale degree, 0 = tonic.</param>
    public static int DegreeToMidi(int tonicMidi, int[] intervals, int degree)
    {
        int n = intervals.Length;
        int octaves = (int)Math.Floor(degree / (double)n);
        int index = degree - (octaves * n); // guaranteed 0..n-1
        return tonicMidi + (octaves * 12) + intervals[index];
    }

    /// <summary>
    /// Build a four-note voicing for a chord rooted on <paramref name="degree"/>:
    /// diatonic triad (root, third, fifth) plus the root an octave up, giving an
    /// arpeggiator four ascending tones to walk.
    /// </summary>
    public static int[] ChordVoicing(int tonicMidi, int[] intervals, int degree)
    {
        int root = DegreeToMidi(tonicMidi, intervals, degree);
        int third = DegreeToMidi(tonicMidi, intervals, degree + 2);
        int fifth = DegreeToMidi(tonicMidi, intervals, degree + 4);
        return [root, third, fifth, root + 12];
    }
}
