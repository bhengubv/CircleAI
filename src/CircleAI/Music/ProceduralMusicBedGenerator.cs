namespace CircleAI.Music;

/// <summary>
/// The genuinely-working, pure-managed fallback generator. It synthesises a
/// royalty-free chord/arpeggio bed straight to signed 16-bit PCM using only
/// managed floating-point maths — no model, no download, no third-party code —
/// so a usable artifact ALWAYS exists offline, even on the lowest-end
/// de-Googled Android device.
/// </summary>
/// <remarks>
/// <para>
/// The bed is two layers over a diatonic chord progression derived from the
/// spec's key: a plucked arpeggio (sine plus optional harmonics with a fast
/// attack / exponential decay) and a soft sustained triad pad. Every note is
/// self-contained (starts and ends near zero amplitude) and the whole bed is
/// tanh soft-limited with short fades, so the output is click-free and never
/// clips. Generation is fully deterministic: the same <see cref="MusicSpec"/>
/// always renders identical audio (see <see cref="MusicSpec.Seed"/>).
/// </para>
/// <para>
/// This is the equivalent of <c>SelectionQuality.HeuristicFallback</c> in the
/// inference model selector: reduced musical sophistication, but zero cost and
/// always available. A downloaded neural model supersedes it via
/// <see cref="MusicBedGeneratorResolver"/> when one is present.
/// </para>
/// </remarks>
public sealed class ProceduralMusicBedGenerator : IMusicBedGenerator
{
    private const int BeatsPerBar = 4;
    private const double Pi2 = 2.0 * Math.PI;

    /// <inheritdoc />
    public MusicBedBackend Backend => MusicBedBackend.Procedural;

    /// <inheritdoc />
    /// <remarks>
    /// Synthesis is CPU-bound and runs synchronously; the result is wrapped in a
    /// completed task. Callers on a UI thread should invoke this from a
    /// background task. Use <see cref="Generate"/> directly when already off the
    /// UI thread.
    /// </remarks>
    public Task<MusicBed> GenerateAsync(MusicSpec spec, CancellationToken cancellationToken = default)
        => Task.FromResult(Generate(spec, cancellationToken));

    /// <summary>
    /// Synchronously synthesise the bed described by <paramref name="spec"/>.
    /// </summary>
    /// <param name="spec">Mood, tempo, duration and key of the bed.</param>
    /// <param name="cancellationToken">Cancels a long synthesis.</param>
    /// <exception cref="NotSupportedException">
    /// The requested format is not 16-bit PCM, or has more than two channels.
    /// </exception>
    public MusicBed Generate(MusicSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();

        AudioPcmFormat format = spec.Format ?? AudioPcmFormat.BedDefault;
        if (format.BitsPerSample != 16)
        {
            throw new NotSupportedException(
                "The procedural synthesiser only produces 16-bit PCM. Supply a 16-bit AudioPcmFormat, or use a neural backend for other depths.");
        }

        if (format.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                "The procedural synthesiser supports mono or stereo output only.");
        }

        int sampleRate = format.SampleRate;
        double totalSeconds = spec.Duration.TotalSeconds;
        int frames = (int)Math.Round(totalSeconds * sampleRate);
        var mono = new float[frames];

        int[] intervals = MusicTheory.Intervals(spec.Key.Scale);
        int[] progression = ProgressionFor(spec.Key.Scale);
        var (baseOctave, arpPerBeat, arpPattern, harmonics, arpGain, padGain) = VoicingFor(spec.Mood);
        int tonicMidi = MusicTheory.MidiNote(spec.Key.Root, baseOctave);

        double secondsPerBeat = 60.0 / spec.Tempo;
        double secondsPerBar = secondsPerBeat * BeatsPerBar;
        double arpNoteSeconds = secondsPerBeat / arpPerBeat;

        var rng = new XorShift(spec.EffectiveSeed());

        RenderArpeggio(
            mono, sampleRate, totalSeconds, secondsPerBar, arpNoteSeconds,
            tonicMidi, intervals, progression, arpPattern, harmonics, arpGain,
            rng, cancellationToken);

        RenderPad(
            mono, sampleRate, totalSeconds, secondsPerBar,
            tonicMidi - 12, intervals, progression, padGain, cancellationToken);

        ApplyMaster(mono, sampleRate);

        byte[] pcm = ToPcm16(mono, format.Channels);
        return new MusicBed(pcm, format, spec, Backend, spec.Duration);
    }

    // ── Layers ────────────────────────────────────────────────────────────

    private static void RenderArpeggio(
        float[] buffer, int sampleRate, double totalSeconds, double secondsPerBar,
        double arpNoteSeconds, int tonicMidi, int[] intervals, int[] progression,
        int[] pattern, int harmonics, double gain, XorShift rng, CancellationToken ct)
    {
        if (arpNoteSeconds <= 0.0)
        {
            return;
        }

        int totalNotes = (int)Math.Ceiling(totalSeconds / arpNoteSeconds);
        int noteSamples = Math.Max(1, (int)(arpNoteSeconds * sampleRate));

        for (int noteIndex = 0; noteIndex < totalNotes; noteIndex++)
        {
            if ((noteIndex & 63) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            double noteStartSeconds = noteIndex * arpNoteSeconds;
            int bar = (int)(noteStartSeconds / secondsPerBar);
            int degree = progression[bar % progression.Length];
            int[] chord = MusicTheory.ChordVoicing(tonicMidi, intervals, degree);

            int tone = pattern[noteIndex % pattern.Length]; // 0..3 into the chord voicing
            double frequency = MusicTheory.Frequency(chord[tone]);

            // Deterministic velocity jitter so repeated notes don't sound robotic.
            double velocity = 0.85 + (0.30 * rng.NextUnit());

            int start = (int)(noteStartSeconds * sampleRate);
            RenderPluck(buffer, start, noteSamples, sampleRate, frequency, gain * velocity, harmonics);
        }
    }

    private static void RenderPluck(
        float[] buffer, int start, int length, int sampleRate,
        double frequency, double gain, int harmonics)
    {
        if (length <= 0 || start >= buffer.Length)
        {
            return;
        }

        double attackSamples = Math.Max(1.0, Math.Min(0.006 * sampleRate, length * 0.25));
        double decayK = 4.5 / length;              // ~e^-4.5 by the end of the note
        double phaseInc = Pi2 * frequency / sampleRate;
        double norm = harmonics >= 3 ? 1.53 : harmonics >= 2 ? 1.35 : 1.0;

        for (int i = 0; i < length; i++)
        {
            int index = start + i;
            if (index >= buffer.Length)
            {
                break;
            }

            double envelope = i < attackSamples
                ? i / attackSamples
                : Math.Exp(-decayK * (i - attackSamples));

            double phase = phaseInc * i;
            double sample = Math.Sin(phase);
            if (harmonics >= 2)
            {
                sample += 0.35 * Math.Sin(2.0 * phase);
            }

            if (harmonics >= 3)
            {
                sample += 0.18 * Math.Sin(3.0 * phase);
            }

            buffer[index] += (float)(envelope * gain * (sample / norm));
        }
    }

    private static void RenderPad(
        float[] buffer, int sampleRate, double totalSeconds, double secondsPerBar,
        int padTonicMidi, int[] intervals, int[] progression, double gain, CancellationToken ct)
    {
        if (secondsPerBar <= 0.0 || gain <= 0.0)
        {
            return;
        }

        int totalBars = (int)Math.Ceiling(totalSeconds / secondsPerBar);
        int barSamples = Math.Max(1, (int)(secondsPerBar * sampleRate));

        for (int bar = 0; bar < totalBars; bar++)
        {
            ct.ThrowIfCancellationRequested();

            int degree = progression[bar % progression.Length];
            int[] chord = MusicTheory.ChordVoicing(padTonicMidi, intervals, degree);
            int start = (int)(bar * secondsPerBar * sampleRate);
            RenderPadChord(buffer, start, barSamples, sampleRate, chord, gain);
        }
    }

    private static void RenderPadChord(
        float[] buffer, int start, int length, int sampleRate, int[] chord, double gain)
    {
        if (length <= 0 || start >= buffer.Length)
        {
            return;
        }

        int voices = Math.Min(3, chord.Length); // triad only for a warm pad
        Span<double> phaseInc = stackalloc double[voices];
        for (int voice = 0; voice < voices; voice++)
        {
            phaseInc[voice] = Pi2 * MusicTheory.Frequency(chord[voice]) / sampleRate;
        }

        double attack = length * 0.15;
        double release = length * 0.15;
        double releaseStart = length - release;
        double voiceScale = 1.0 / voices;

        for (int i = 0; i < length; i++)
        {
            int index = start + i;
            if (index >= buffer.Length)
            {
                break;
            }

            double envelope =
                i < attack ? i / attack :
                i > releaseStart ? (length - i) / release :
                1.0;

            double sample = 0.0;
            for (int voice = 0; voice < voices; voice++)
            {
                sample += Math.Sin(phaseInc[voice] * i);
            }

            buffer[index] += (float)(envelope * gain * voiceScale * sample);
        }
    }

    // ── Master bus ───────────────────────────────────────────────────────

    private static void ApplyMaster(float[] buffer, int sampleRate)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)Math.Tanh(buffer[i]); // soft-limit into [-1, 1]
        }

        int fadeIn = Math.Min((int)(0.03 * sampleRate), buffer.Length / 2);
        int fadeOut = Math.Min((int)(0.05 * sampleRate), buffer.Length / 2);

        for (int i = 0; i < fadeIn; i++)
        {
            buffer[i] *= (float)(i / (double)fadeIn);
        }

        for (int i = 0; i < fadeOut; i++)
        {
            buffer[buffer.Length - 1 - i] *= (float)(i / (double)fadeOut);
        }
    }

    private static byte[] ToPcm16(float[] mono, int channels)
    {
        int frames = mono.Length;
        var pcm = new byte[frames * channels * 2];
        int p = 0;

        for (int i = 0; i < frames; i++)
        {
            double scaled = mono[i] * 32767.0 * 0.9; // 0.9 = extra headroom
            short value = (short)Math.Clamp(scaled, -32768.0, 32767.0);
            byte lo = (byte)(value & 0xFF);
            byte hi = (byte)((value >> 8) & 0xFF);

            for (int c = 0; c < channels; c++)
            {
                pcm[p++] = lo; // little-endian: low byte first
                pcm[p++] = hi;
            }
        }

        return pcm;
    }

    // ── Musical selection ────────────────────────────────────────────────

    private static int[] ProgressionFor(Scale scale)
    {
        bool bright = scale is Scale.Major or Scale.MajorPentatonic or Scale.Dorian;

        // Bright: I–V–vi–IV. Dark: i–VI–III–VII. Degrees are 0-based scale steps;
        // DegreeToMidi wraps octaves, so both are valid for 5- and 7-note scales.
        return bright ? new[] { 0, 4, 5, 3 } : new[] { 0, 5, 2, 6 };
    }

    private static (int BaseOctave, int ArpPerBeat, int[] ArpPattern, int Harmonics, double ArpGain, double PadGain)
        VoicingFor(Mood mood) => mood switch
    {
        // ArpPattern values index the 4-note chord voicing [root, third, fifth, root+8ve].
        Mood.Calm => (4, 1, new[] { 0, 1, 2, 1 }, 1, 0.42, 0.28),
        Mood.Reflective => (4, 1, new[] { 0, 2, 1, 0 }, 1, 0.40, 0.30),
        Mood.Cinematic => (5, 1, new[] { 3, 2, 1, 0 }, 2, 0.36, 0.34),
        Mood.Warm => (4, 1, new[] { 0, 1, 2, 1 }, 2, 0.44, 0.28),
        Mood.Neutral => (4, 2, new[] { 0, 1, 2, 1 }, 1, 0.46, 0.24),
        Mood.Focus => (4, 2, new[] { 0, 1, 2, 3 }, 1, 0.42, 0.24),
        Mood.Corporate => (4, 2, new[] { 0, 1, 2, 3 }, 1, 0.48, 0.22),
        Mood.Uplifting => (5, 2, new[] { 0, 1, 2, 3 }, 2, 0.48, 0.24),
        Mood.Playful => (5, 2, new[] { 0, 2, 1, 3 }, 1, 0.50, 0.20),
        Mood.Energetic => (5, 3, new[] { 0, 1, 2, 3 }, 3, 0.48, 0.20),
        _ => (4, 2, new[] { 0, 1, 2, 1 }, 1, 0.46, 0.24),
    };

    /// <summary>
    /// Deterministic xorshift32 PRNG. Used only for tiny musical variation, so a
    /// non-cryptographic generator is correct here (and avoids taking any
    /// dependency on <see cref="System.Random"/>).
    /// </summary>
    private sealed class XorShift
    {
        private uint _state;

        public XorShift(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

        /// <summary>Next pseudo-random value in the half-open range [0, 1).</summary>
        public double NextUnit()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state / (double)uint.MaxValue;
        }
    }
}
