// Music.swift
//
// Port of src/CircleAI.Music/:
//   • Mood.cs / MusicalKey.cs / MusicSpec.cs / AudioPcmFormat.cs / MusicBed.cs /
//     MusicBedBackend.cs / IMusicBedGenerator.cs → the vocabulary
//   • MusicTheory.cs  → MusicTheory (scales, MIDI, frequency, chord voicings)
//   • WavWriter.cs    → WavWriter
//   • NullMusicBedGenerator.cs / MusicBedGeneratorResolver.cs → the seam
//
// Porting notes:
//   • UNLIKE Charts and Documents, this module ports WHOLE. There is no PDFsharp
//     here - it is arithmetic, a 44-byte RIFF header and a sine bank - so the
//     Swift can do the actual job rather than describe it.
//
//   • The DEFAULT TEMPO AND KEY TABLES are carried over value for value.
//     Reflective is 66 BPM in A minor and Playful is 120 in C major pentatonic
//     because somebody chose that; a port that "simplified" the ramp would make
//     the same MusicMood sound different on iOS.

import Foundation

// MARK: - Vocabulary

/// The feel a bed is asked for.
///
/// NAMED MusicMood, NOT Mood. Swift is one module, and CircleAI.PersonalMental
/// already owns Mood for a self-reported mental-health level (veryLow ... great).
/// Two unrelated ideas sharing a word is exactly the collision C# namespaces
/// prevent and Swift does not, so the musical one takes the module's prefix -
/// the same one MusicSpec, MusicBed and MusicalKey already carry.
public enum MusicMood: Int, Sendable, Equatable, CaseIterable, Codable {
    case neutral = 0, calm, warm, reflective, uplifting
    case corporate, focus, energetic, playful, cinematic
}

public enum PitchClass: Int, Sendable, Equatable, CaseIterable, Codable {
    case c = 0, cSharp, d, dSharp, e, f, fSharp, g, gSharp, a, aSharp, b
}

public enum Scale: Int, Sendable, Equatable, CaseIterable, Codable {
    case major = 0, minor, dorian, majorPentatonic, minorPentatonic
}

public struct MusicalKey: Sendable, Equatable, Codable, CustomStringConvertible {
    public let root: PitchClass
    public let scale: Scale

    public init(root: PitchClass, scale: Scale) {
        self.root = root
        self.scale = scale
    }

    public static let cMajor = MusicalKey(root: .c, scale: .major)
    public static let aMinor = MusicalKey(root: .a, scale: .minor)
    public static let dMinor = MusicalKey(root: .d, scale: .minor)
    public static let gMajor = MusicalKey(root: .g, scale: .major)
    public static let cMajorPentatonic = MusicalKey(root: .c, scale: .majorPentatonic)

    public var description: String { "\(root) \(scale)" }
}

/// Raw PCM layout of a rendered bed.
public struct AudioPcmFormat: Sendable, Equatable, Codable {
    public let sampleRate: Int
    public let channels: Int
    public let bitsPerSample: Int

    public init(sampleRate: Int, channels: Int, bitsPerSample: Int) {
        self.sampleRate = sampleRate
        self.channels = channels
        self.bitsPerSample = bitsPerSample
    }

    public static let bedDefault = AudioPcmFormat(sampleRate: 44_100, channels: 1, bitsPerSample: 16)
    public static let compact = AudioPcmFormat(sampleRate: 22_050, channels: 1, bitsPerSample: 16)
    public static let cdStereo = AudioPcmFormat(sampleRate: 44_100, channels: 2, bitsPerSample: 16)

    public var bytesPerSample: Int { bitsPerSample / 8 }
    public var blockAlign: Int { channels * bytesPerSample }
    public var byteRate: Int { sampleRate * blockAlign }
}

/// Which engine produced a bed.
public enum MusicBedBackend: Int, Sendable, Equatable, CaseIterable, Codable {
    case procedural = 0
    case neural
}

/// What to generate.
public struct MusicSpec: Sendable, Equatable {
    public let mood: MusicMood
    public let tempo: Int
    public let duration: TimeInterval
    public let key: MusicalKey
    /// Non-zero pins the output; zero derives a seed from the rest of the spec.
    public var seed: Int
    public var format: AudioPcmFormat?

    public static let minTempo = 40
    public static let maxTempo = 240
    public static let maxDuration: TimeInterval = 5 * 60

    public init(mood: MusicMood, tempo: Int, duration: TimeInterval, key: MusicalKey,
                seed: Int = 0, format: AudioPcmFormat? = nil) {
        self.mood = mood
        self.tempo = tempo
        self.duration = duration
        self.key = key
        self.seed = seed
        self.format = format
    }

    /// A spec with the tempo and key this mood is meant to sound like.
    public static func forMood(_ mood: MusicMood, duration: TimeInterval) -> MusicSpec {
        MusicSpec(mood: mood, tempo: defaultTempo(mood), duration: duration, key: defaultKey(mood))
    }

    public enum Invalid: Error, Sendable, Equatable {
        case tempo(Int)
        case duration(TimeInterval)
    }

    /// Throws rather than rendering something nobody asked for.
    public func validate() throws {
        guard (Self.minTempo...Self.maxTempo).contains(tempo) else { throw Invalid.tempo(tempo) }
        guard duration > 0, duration <= Self.maxDuration else { throw Invalid.duration(duration) }
    }

    /// The seed actually used.
    ///
    /// NOT A HASH OF THE FIELDS THE WAY C# DOES IT. .NET randomises
    /// HashCode.Combine per process, so the C# only reproduces a bed WITHIN one
    /// run; carrying that across would have made "same spec, same audio" false
    /// between one app launch and the next. FNV-1a over the same five fields is
    /// stable everywhere, and the 0x9E3779B9 fallback for a zero result is kept.
    public func effectiveSeed() -> UInt32 {
        if seed != 0 { return UInt32(truncatingIfNeeded: seed) }
        var h: UInt32 = 2_166_136_261
        func mix(_ v: Int) {
            var x = UInt32(truncatingIfNeeded: v)
            for _ in 0..<4 {
                h = (h ^ (x & 0xFF)) &* 16_777_619
                x >>= 8
            }
        }
        mix(mood.rawValue)
        mix(tempo)
        mix(Int(duration.rounded()))
        mix(key.root.rawValue)
        mix(key.scale.rawValue)
        return h == 0 ? 0x9E37_79B9 : h
    }

    /// Reflective is 66 BPM and Energetic is 128 because somebody chose that.
    static func defaultTempo(_ mood: MusicMood) -> Int {
        switch mood {
        case .reflective: return 66
        case .cinematic:  return 70
        case .calm:       return 74
        case .warm:       return 86
        case .neutral:    return 96
        case .focus:      return 100
        case .corporate:  return 104
        case .uplifting:  return 114
        case .playful:    return 120
        case .energetic:  return 128
        }
    }

    static func defaultKey(_ mood: MusicMood) -> MusicalKey {
        switch mood {
        case .reflective, .cinematic: return .aMinor
        case .calm:                   return .dMinor
        case .playful:                return .cMajorPentatonic
        case .uplifting:              return .gMajor
        default:                      return .cMajor
        }
    }
}

/// A rendered bed: the samples, how to read them, and what produced them.
public struct MusicBed: Sendable, Equatable {
    public let pcm: Data
    public let format: AudioPcmFormat
    public let spec: MusicSpec
    public let backend: MusicBedBackend
    public let duration: TimeInterval

    public init(pcm: Data, format: AudioPcmFormat, spec: MusicSpec,
                backend: MusicBedBackend, duration: TimeInterval) {
        self.pcm = pcm
        self.format = format
        self.spec = spec
        self.backend = backend
        self.duration = duration
    }

    /// The bed as a complete .wav file.
    public func toWav() -> Data { WavWriter.toWav(pcm, format: format) }

    /// Writes the bed to `url` as a .wav.
    public func writeWav(to url: URL) throws { try toWav().write(to: url) }
}

/// Produces a `MusicBed` from a `MusicSpec`.
public protocol MusicBedGenerator: Sendable {
    var backend: MusicBedBackend { get }
    func generate(_ spec: MusicSpec) async throws -> MusicBed
}

// MARK: - Theory

/// Scales, MIDI numbers and frequencies. Internal in C#; internal here too.
enum MusicTheory {
    private static let a4Frequency = 440.0
    private static let a4MidiNote = 69

    /// Semitone offsets from the tonic.
    static func intervals(_ scale: Scale) -> [Int] {
        switch scale {
        case .major:            return [0, 2, 4, 5, 7, 9, 11]
        case .minor:            return [0, 2, 3, 5, 7, 8, 10]
        case .dorian:           return [0, 2, 3, 5, 7, 9, 10]
        case .majorPentatonic:  return [0, 2, 4, 7, 9]
        case .minorPentatonic:  return [0, 3, 5, 7, 10]
        }
    }

    static func midiNote(root: PitchClass, octave: Int) -> Int {
        ((octave + 1) * 12) + root.rawValue
    }

    static func frequency(midiNote: Int) -> Double {
        a4Frequency * pow(2.0, Double(midiNote - a4MidiNote) / 12.0)
    }

    /// A scale degree as a MIDI note, wrapping into octaves.
    ///
    /// FLOOR DIVISION, NOT TRUNCATION - degree -1 must fall an octave, and
    /// Swift's `/` on a negative Int truncates toward zero, which would send it
    /// UP instead. The C# uses Math.Floor for the same reason.
    static func degreeToMidi(tonicMidi: Int, intervals: [Int], degree: Int) -> Int {
        let n = intervals.count
        let octaves = Int(floor(Double(degree) / Double(n)))
        let index = degree - (octaves * n)   // guaranteed 0..<n
        return tonicMidi + (octaves * 12) + intervals[index]
    }

    /// Root, third, fifth and the octave above the root.
    static func chordVoicing(tonicMidi: Int, intervals: [Int], degree: Int) -> [Int] {
        let root = degreeToMidi(tonicMidi: tonicMidi, intervals: intervals, degree: degree)
        let third = degreeToMidi(tonicMidi: tonicMidi, intervals: intervals, degree: degree + 2)
        let fifth = degreeToMidi(tonicMidi: tonicMidi, intervals: intervals, degree: degree + 4)
        return [root, third, fifth, root + 12]
    }
}

// MARK: - WAV

/// Wraps raw PCM in a 44-byte canonical RIFF header.
public enum WavWriter {
    private static let headerLength = 44
    private static let pcmFormatTag: Int16 = 1

    /// The samples as a complete .wav file.
    public static func toWav(_ pcm: Data, format: AudioPcmFormat) -> Data {
        var out = Data(capacity: headerLength + pcm.count)

        func tag(_ s: String) { out.append(contentsOf: s.utf8) }
        func u32(_ v: Int) { withUnsafeBytes(of: UInt32(truncatingIfNeeded: v).littleEndian) { out.append(contentsOf: $0) } }
        func u16(_ v: Int) { withUnsafeBytes(of: UInt16(truncatingIfNeeded: v).littleEndian) { out.append(contentsOf: $0) } }

        tag("RIFF")
        u32(36 + pcm.count)
        tag("WAVE")
        tag("fmt ")
        u32(16)                       // PCM fmt chunk size
        u16(Int(pcmFormatTag))
        u16(format.channels)
        u32(format.sampleRate)
        u32(format.byteRate)
        u16(format.blockAlign)
        u16(format.bitsPerSample)
        tag("data")
        u32(pcm.count)
        out.append(pcm)
        return out
    }

    /// Writes the samples to `url` as a .wav.
    public static func write(_ pcm: Data, format: AudioPcmFormat, to url: URL) throws {
        try toWav(pcm, format: format).write(to: url)
    }
}

// MARK: - Generators

/// A generator that produces silence of the requested length.
///
/// Not a stub: it is what a host uses when music is switched off, and it still
/// has to return a bed of the right duration and format so callers downstream
/// need no special case.
public struct NullMusicBedGenerator: MusicBedGenerator {
    public init() {}

    public var backend: MusicBedBackend { .procedural }

    public func generate(_ spec: MusicSpec) async throws -> MusicBed {
        try spec.validate()
        let format = spec.format ?? .bedDefault
        let frames = Int((spec.duration * Double(format.sampleRate)).rounded())
        let pcm = Data(count: max(0, frames) * format.blockAlign)
        return MusicBed(pcm: pcm, format: format, spec: spec,
                        backend: .procedural, duration: spec.duration)
    }
}

/// Picks a generator for a requested backend.
///
/// The C# resolver falls back to procedural when a neural backend is asked for
/// and none is registered. That fallback is the point: a host asking for music
/// it cannot run should get music, not an exception.
public struct MusicBedGeneratorResolver: Sendable {
    private let generators: [MusicBedBackend: any MusicBedGenerator]
    private let fallback: any MusicBedGenerator

    public init(generators: [MusicBedBackend: any MusicBedGenerator] = [:],
                fallback: any MusicBedGenerator = NullMusicBedGenerator()) {
        self.generators = generators
        self.fallback = fallback
    }

    /// The generator for `backend`, or the fallback when none is registered.
    public func resolve(_ backend: MusicBedBackend) -> any MusicBedGenerator {
        generators[backend] ?? generators[.procedural] ?? fallback
    }

    /// Which backends actually have a generator behind them.
    public var available: [MusicBedBackend] {
        MusicBedBackend.allCases.filter { generators[$0] != nil }
    }
}
