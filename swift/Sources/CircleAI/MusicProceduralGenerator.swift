// MusicProceduralGenerator.swift
//
// A music bed synthesised from nothing but arithmetic.
//
// WHY THIS EXISTS AT ALL. The alternative is a neural music model, which is
// hundreds of megabytes and does not run on the phone this is built for. This
// produces a listenable bed on any device, offline, in a fraction of real time,
// and it is the DEFAULT so a caller that asks for music always gets music.
//
// It is two layers over a four-bar progression: a plucked arpeggio carrying the
// movement, and a slow triad pad underneath carrying the harmony. Everything
// else — which notes, how bright, how fast — is chosen by mood.
//
// DETERMINISTIC BY CONSTRUCTION. The only randomness is velocity jitter from a
// seeded xorshift, so the same spec produces byte-identical audio every time.
// That matters more than it sounds: a bed regenerated on a second device has to
// match, or a video assembled from the same spec has different audio on every
// machine that renders it.
//
// Ported from src/CircleAI.Music/ProceduralMusicBedGenerator.cs.

import Foundation

public enum MusicSynthesisError: Error, Equatable, CustomStringConvertible {
    case onlySixteenBit
    case onlyMonoOrStereo

    public var description: String {
        switch self {
        case .onlySixteenBit:
            return "The procedural synthesiser only produces 16-bit PCM. Supply a 16-bit "
                 + "AudioPcmFormat, or use a neural backend for other depths."
        case .onlyMonoOrStereo:
            return "The procedural synthesiser supports mono or stereo output only."
        }
    }
}

public struct ProceduralMusicBedGenerator: MusicBedGenerator, Sendable {

    private static let beatsPerBar = 4
    private static let pi2 = 2.0 * Double.pi

    public init() {}

    public var backend: MusicBedBackend { .procedural }

    public func generate(_ spec: MusicSpec) async throws -> MusicBed {
        try synthesise(spec)
    }

    /// The synchronous form. Exposed because it is genuinely synchronous —
    /// nothing here waits on anything — and a caller rendering a hundred beds
    /// should not pay for a hundred suspensions to find that out.
    public func synthesise(_ spec: MusicSpec) throws -> MusicBed {
        try spec.validate()

        let format = spec.format ?? .bedDefault
        guard format.bitsPerSample == 16 else { throw MusicSynthesisError.onlySixteenBit }
        guard (1...2).contains(format.channels) else { throw MusicSynthesisError.onlyMonoOrStereo }

        let sampleRate = format.sampleRate
        let totalSeconds = spec.duration
        let frames = Int((totalSeconds * Double(sampleRate)).rounded())
        var mono = [Float](repeating: 0, count: max(0, frames))

        let intervals = MusicTheory.intervals(spec.key.scale)
        let progression = Self.progression(for: spec.key.scale)
        let voicing = Self.voicing(for: spec.mood)

        let tonicMidi = MusicTheory.midiNote(root: spec.key.root, octave: voicing.baseOctave)
        let secondsPerBeat = 60.0 / Double(spec.tempo)
        let secondsPerBar = secondsPerBeat * Double(Self.beatsPerBar)
        let arpNoteSeconds = secondsPerBeat / Double(voicing.arpPerBeat)

        var rng = XorShift(seed: spec.effectiveSeed())

        Self.renderArpeggio(&mono, sampleRate: sampleRate, totalSeconds: totalSeconds,
                            secondsPerBar: secondsPerBar, arpNoteSeconds: arpNoteSeconds,
                            tonicMidi: tonicMidi, intervals: intervals,
                            progression: progression, pattern: voicing.arpPattern,
                            harmonics: voicing.harmonics, gain: voicing.arpGain, rng: &rng)

        // The pad sits an OCTAVE BELOW the arpeggio. In the same octave the two
        // layers fight for the same frequencies and the result is muddy rather
        // than full.
        Self.renderPad(&mono, sampleRate: sampleRate, totalSeconds: totalSeconds,
                       secondsPerBar: secondsPerBar, padTonicMidi: tonicMidi - 12,
                       intervals: intervals, progression: progression,
                       gain: voicing.padGain)

        Self.applyMaster(&mono, sampleRate: sampleRate)

        let pcm = Self.toPcm16(mono, channels: format.channels)
        return MusicBed(pcm: pcm, format: format, spec: spec,
                        backend: .procedural, duration: spec.duration)
    }

    // MARK: - Layers

    static func renderArpeggio(_ buffer: inout [Float], sampleRate: Int,
                               totalSeconds: Double, secondsPerBar: Double,
                               arpNoteSeconds: Double, tonicMidi: Int,
                               intervals: [Int], progression: [Int], pattern: [Int],
                               harmonics: Int, gain: Double, rng: inout XorShift) {
        guard arpNoteSeconds > 0, secondsPerBar > 0 else { return }

        let totalNotes = Int((totalSeconds / arpNoteSeconds).rounded(.up))
        let noteSamples = max(1, Int(arpNoteSeconds * Double(sampleRate)))

        for noteIndex in 0..<max(0, totalNotes) {
            let noteStartSeconds = Double(noteIndex) * arpNoteSeconds
            let bar = Int(noteStartSeconds / secondsPerBar)
            let degree = progression[bar % progression.count]
            let chord = MusicTheory.chordVoicing(tonicMidi: tonicMidi,
                                                 intervals: intervals, degree: degree)
            let tone = pattern[noteIndex % pattern.count]
            let frequency = MusicTheory.frequency(midiNote: chord[tone])

            // Deterministic velocity jitter so repeated notes do not sound
            // robotic. Seeded, so the same spec still renders identically.
            let velocity = 0.85 + 0.30 * rng.nextUnit()
            let start = Int(noteStartSeconds * Double(sampleRate))

            renderPluck(&buffer, start: start, length: noteSamples, sampleRate: sampleRate,
                        frequency: frequency, gain: gain * velocity, harmonics: harmonics)
        }
    }

    /// One plucked note: fast attack, exponential decay, a couple of harmonics.
    static func renderPluck(_ buffer: inout [Float], start: Int, length: Int,
                            sampleRate: Int, frequency: Double, gain: Double,
                            harmonics: Int) {
        guard length > 0, start < buffer.count else { return }

        let attackSamples = max(1.0, min(0.006 * Double(sampleRate), Double(length) * 0.25))
        let decayK = 4.5 / Double(length)          // ~e^-4.5 by the end of the note
        let phaseInc = pi2 * frequency / Double(sampleRate)
        // Normalised so adding harmonics makes a note BRIGHTER, not louder —
        // without this the energetic moods clip and the calm ones do not.
        let norm = harmonics >= 3 ? 1.53 : harmonics >= 2 ? 1.35 : 1.0

        for i in 0..<length {
            let index = start + i
            if index >= buffer.count { break }

            let envelope = Double(i) < attackSamples
                ? Double(i) / attackSamples
                : exp(-decayK * (Double(i) - attackSamples))

            let phase = phaseInc * Double(i)
            var sample = sin(phase)
            if harmonics >= 2 { sample += 0.35 * sin(2.0 * phase) }
            if harmonics >= 3 { sample += 0.18 * sin(3.0 * phase) }

            buffer[index] += Float(envelope * gain * (sample / norm))
        }
    }

    static func renderPad(_ buffer: inout [Float], sampleRate: Int, totalSeconds: Double,
                          secondsPerBar: Double, padTonicMidi: Int, intervals: [Int],
                          progression: [Int], gain: Double) {
        guard secondsPerBar > 0, gain > 0 else { return }

        let totalBars = Int((totalSeconds / secondsPerBar).rounded(.up))
        let barSamples = max(1, Int(secondsPerBar * Double(sampleRate)))

        for bar in 0..<max(0, totalBars) {
            let degree = progression[bar % progression.count]
            let chord = MusicTheory.chordVoicing(tonicMidi: padTonicMidi,
                                                 intervals: intervals, degree: degree)
            let start = Int(Double(bar) * secondsPerBar * Double(sampleRate))
            renderPadChord(&buffer, start: start, length: barSamples,
                           sampleRate: sampleRate, chord: chord, gain: gain)
        }
    }

    static func renderPadChord(_ buffer: inout [Float], start: Int, length: Int,
                               sampleRate: Int, chord: [Int], gain: Double) {
        guard length > 0, start < buffer.count else { return }

        // A TRIAD only. The fourth voice is the octave, and doubling it in a
        // sustained pad is what makes a bed sound like an organ.
        let voices = min(3, chord.count)
        guard voices > 0 else { return }

        var phaseInc = [Double](repeating: 0, count: voices)
        for v in 0..<voices {
            phaseInc[v] = pi2 * MusicTheory.frequency(midiNote: chord[v]) / Double(sampleRate)
        }

        let attack = Double(length) * 0.15
        let release = Double(length) * 0.15
        let releaseStart = Double(length) - release
        let voiceScale = 1.0 / Double(voices)

        for i in 0..<length {
            let index = start + i
            if index >= buffer.count { break }

            let d = Double(i)
            let envelope = d < attack ? d / attack
                : d > releaseStart ? (Double(length) - d) / release
                : 1.0

            var sample = 0.0
            for v in 0..<voices { sample += sin(phaseInc[v] * d) }

            buffer[index] += Float(envelope * gain * voiceScale * sample)
        }
    }

    // MARK: - Master bus

    static func applyMaster(_ buffer: inout [Float], sampleRate: Int) {
        guard !buffer.isEmpty else { return }

        // SOFT limit, not a hard clip. Two layers summing past full scale is
        // normal here, and hard clipping turns that into audible crackle where
        // tanh just squashes it.
        for i in buffer.indices { buffer[i] = Float(tanh(Double(buffer[i]))) }

        // Fades at both ends. A bed that starts at full amplitude begins with a
        // click, and every listener hears the click.
        let fadeIn = min(Int(0.03 * Double(sampleRate)), buffer.count / 2)
        let fadeOut = min(Int(0.05 * Double(sampleRate)), buffer.count / 2)

        for i in 0..<fadeIn { buffer[i] *= Float(Double(i) / Double(fadeIn)) }
        for i in 0..<fadeOut {
            buffer[buffer.count - 1 - i] *= Float(Double(i) / Double(fadeOut))
        }
    }

    static func toPcm16(_ mono: [Float], channels: Int) -> Data {
        var pcm = [UInt8](repeating: 0, count: mono.count * channels * 2)
        var p = 0
        for sample in mono {
            // 0.9 is extra headroom on top of the limiter, so a bed mixed under
            // a voice track has somewhere to go.
            let scaled = Double(sample) * 32767.0 * 0.9
            let value = Int16(max(-32768.0, min(32767.0, scaled)))
            let u = UInt16(bitPattern: value)
            let lo = UInt8(u & 0xFF), hi = UInt8(u >> 8)
            for _ in 0..<channels {
                pcm[p] = lo; pcm[p + 1] = hi          // little-endian
                p += 2
            }
        }
        return Data(pcm)
    }

    // MARK: - Musical selection

    /// Bright scales get I–V–vi–IV; dark ones get i–VI–III–VII.
    ///
    /// Degrees are 0-based scale steps and the voicing wraps octaves, so both
    /// work for five-note and seven-note scales alike — which is the only
    /// reason one table serves pentatonic and diatonic together.
    static func progression(for scale: Scale) -> [Int] {
        let bright = scale == .major || scale == .majorPentatonic || scale == .dorian
        return bright ? [0, 4, 5, 3] : [0, 5, 2, 6]
    }

    struct Voicing {
        let baseOctave: Int
        let arpPerBeat: Int
        /// Values index the four-note chord voicing [root, third, fifth, root+8ve].
        let arpPattern: [Int]
        let harmonics: Int
        let arpGain: Double
        let padGain: Double
    }

    /// What each mood actually sounds like.
    ///
    /// The numbers are the composition. Faster arpeggios and more harmonics read
    /// as energy; a higher pad relative to the arpeggio reads as space. They are
    /// in one table so a mood cannot mean two different things in two callers.
    static func voicing(for mood: MusicMood) -> Voicing {
        switch mood {
        case .calm:       return Voicing(baseOctave: 4, arpPerBeat: 1, arpPattern: [0, 1, 2, 1], harmonics: 1, arpGain: 0.42, padGain: 0.28)
        case .reflective: return Voicing(baseOctave: 4, arpPerBeat: 1, arpPattern: [0, 2, 1, 0], harmonics: 1, arpGain: 0.40, padGain: 0.30)
        case .cinematic:  return Voicing(baseOctave: 5, arpPerBeat: 1, arpPattern: [3, 2, 1, 0], harmonics: 2, arpGain: 0.36, padGain: 0.34)
        case .warm:       return Voicing(baseOctave: 4, arpPerBeat: 1, arpPattern: [0, 1, 2, 1], harmonics: 2, arpGain: 0.44, padGain: 0.28)
        case .neutral:    return Voicing(baseOctave: 4, arpPerBeat: 2, arpPattern: [0, 1, 2, 1], harmonics: 1, arpGain: 0.46, padGain: 0.24)
        case .focus:      return Voicing(baseOctave: 4, arpPerBeat: 2, arpPattern: [0, 1, 2, 3], harmonics: 1, arpGain: 0.42, padGain: 0.24)
        case .corporate:  return Voicing(baseOctave: 4, arpPerBeat: 2, arpPattern: [0, 1, 2, 3], harmonics: 1, arpGain: 0.48, padGain: 0.22)
        case .uplifting:  return Voicing(baseOctave: 5, arpPerBeat: 2, arpPattern: [0, 1, 2, 3], harmonics: 2, arpGain: 0.48, padGain: 0.24)
        case .playful:    return Voicing(baseOctave: 5, arpPerBeat: 2, arpPattern: [0, 2, 1, 3], harmonics: 1, arpGain: 0.50, padGain: 0.20)
        case .energetic:  return Voicing(baseOctave: 5, arpPerBeat: 3, arpPattern: [0, 1, 2, 3], harmonics: 3, arpGain: 0.48, padGain: 0.20)
        }
    }

    /// Xorshift32.
    ///
    /// Written out rather than taken from the standard library so the SEQUENCE
    /// is identical on every platform: a seeded generator whose algorithm can
    /// change between releases is not a seed, and the byte-identical guarantee
    /// rests on it.
    public struct XorShift {
        private var state: UInt32

        public init(seed: UInt32) {
            // Zero is a fixed point of xorshift — it produces zero forever, so
            // a spec that seeded to 0 would render with no jitter at all.
            self.state = seed == 0 ? 0x9E37_79B9 : seed
        }

        public mutating func nextUnit() -> Double {
            state ^= state << 13
            state ^= state >> 17
            state ^= state << 5
            return Double(state) / Double(UInt32.max)
        }
    }
}
