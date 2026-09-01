// MusicProceduralGeneratorTests.swift

import XCTest
@testable import CircleAI

final class MusicProceduralGeneratorTests: XCTestCase {

    private let gen = ProceduralMusicBedGenerator()

    private func spec(mood: MusicMood = .calm, tempo: Int = 90, duration: TimeInterval = 2,
                      seed: Int = 42, format: AudioPcmFormat? = nil) -> MusicSpec {
        MusicSpec(mood: mood, tempo: tempo, duration: duration,
                  key: MusicalKey(root: .c, scale: .major), seed: seed, format: format)
    }

    // MARK: - Determinism

    func testTheSameSpecProducesByteIdenticalAudio() throws {
        // A bed regenerated on a second device has to match, or a video
        // assembled from the same spec has different audio on every machine
        // that renders it.
        let a = try gen.synthesise(spec())
        let b = try gen.synthesise(spec())
        XCTAssertEqual(a.pcm, b.pcm)
    }

    func testADifferentSeedProducesDifferentAudio() throws {
        // Otherwise the seed is decoration and the velocity jitter is not
        // actually being applied.
        let a = try gen.synthesise(spec(seed: 1))
        let b = try gen.synthesise(spec(seed: 2))
        XCTAssertNotEqual(a.pcm, b.pcm)
        XCTAssertEqual(a.pcm.count, b.pcm.count)
    }

    func testZeroIsNotAFixedPointOfTheGenerator() {
        // Xorshift maps 0 to 0 forever, so a spec that seeded to zero would
        // render with no jitter at all and nothing would say so.
        var zero = ProceduralMusicBedGenerator.XorShift(seed: 0)
        let first = zero.nextUnit()
        let second = zero.nextUnit()
        XCTAssertNotEqual(first, 0)
        XCTAssertNotEqual(first, second)
    }

    func testTheGeneratorSequenceIsStable() {
        // Written out rather than taken from the standard library precisely so
        // this holds on every platform; a change here is a change to every
        // previously-rendered bed.
        var a = ProceduralMusicBedGenerator.XorShift(seed: 12345)
        var b = ProceduralMusicBedGenerator.XorShift(seed: 12345)
        for _ in 0..<50 { XCTAssertEqual(a.nextUnit(), b.nextUnit()) }
    }

    func testEveryDrawIsInTheUnitInterval() {
        var rng = ProceduralMusicBedGenerator.XorShift(seed: 7)
        for _ in 0..<1000 {
            let v = rng.nextUnit()
            XCTAssertGreaterThanOrEqual(v, 0)
            XCTAssertLessThanOrEqual(v, 1)
        }
    }

    // MARK: - Shape

    func testTheOutputIsTheRequestedLengthAndFormat() throws {
        let bed = try gen.synthesise(spec(duration: 3))
        let format = bed.format
        let frames = bed.pcm.count / (format.channels * 2)
        XCTAssertEqual(frames, 3 * format.sampleRate, accuracy: 1)
        XCTAssertEqual(bed.duration, 3)
        XCTAssertEqual(bed.backend, .procedural)
    }

    func testStereoIsTwiceTheBytesOfMono() throws {
        let rate = AudioPcmFormat.bedDefault.sampleRate
        let mono = try gen.synthesise(spec(format: AudioPcmFormat(
            sampleRate: rate, channels: 1, bitsPerSample: 16)))
        let stereo = try gen.synthesise(spec(format: AudioPcmFormat(
            sampleRate: rate, channels: 2, bitsPerSample: 16)))
        XCTAssertEqual(stereo.pcm.count, mono.pcm.count * 2)
    }

    func testStereoIsTheSameSignalInBothChannels() throws {
        // The synthesiser is mono internally; duplicating it must not offset
        // one channel, which would sound like a phase problem on a speaker.
        let rate = AudioPcmFormat.bedDefault.sampleRate
        let bed = try gen.synthesise(spec(format: AudioPcmFormat(
            sampleRate: rate, channels: 2, bitsPerSample: 16)))
        let bytes = [UInt8](bed.pcm)
        for i in stride(from: 0, to: min(bytes.count, 4000), by: 4) {
            XCTAssertEqual(bytes[i], bytes[i + 2])
            XCTAssertEqual(bytes[i + 1], bytes[i + 3])
        }
    }

    func testAnUnsupportedDepthIsRefusedByName() {
        let rate = AudioPcmFormat.bedDefault.sampleRate
        XCTAssertThrowsError(try gen.synthesise(spec(format: AudioPcmFormat(
            sampleRate: rate, channels: 1, bitsPerSample: 24)))) {
            XCTAssertEqual($0 as? MusicSynthesisError, .onlySixteenBit)
        }
    }

    func testMoreThanTwoChannelsIsRefusedByName() {
        let rate = AudioPcmFormat.bedDefault.sampleRate
        XCTAssertThrowsError(try gen.synthesise(spec(format: AudioPcmFormat(
            sampleRate: rate, channels: 6, bitsPerSample: 16)))) {
            XCTAssertEqual($0 as? MusicSynthesisError, .onlyMonoOrStereo)
        }
    }

    // MARK: - It is actually music

    func testTheBedIsNotSilence() throws {
        let bed = try gen.synthesise(spec())
        let bytes = [UInt8](bed.pcm)
        var peak = 0
        for i in stride(from: 0, to: bytes.count - 1, by: 2) {
            let v = abs(Int(Int16(bitPattern: UInt16(bytes[i]) | UInt16(bytes[i + 1]) << 8)))
            peak = max(peak, v)
        }
        XCTAssertGreaterThan(peak, 3000, "a bed nobody can hear is not a bed")
    }

    func testNothingClips() throws {
        // Two layers summing past full scale is normal here; the limiter and
        // the headroom exist so that never reaches the file as crackle.
        for mood in MusicMood.allCases {
            let bed = try gen.synthesise(spec(mood: mood, duration: 1))
            let bytes = [UInt8](bed.pcm)
            var atCeiling = 0
            for i in stride(from: 0, to: bytes.count - 1, by: 2) {
                let v = Int16(bitPattern: UInt16(bytes[i]) | UInt16(bytes[i + 1]) << 8)
                if v == Int16.max || v == Int16.min { atCeiling += 1 }
            }
            XCTAssertEqual(atCeiling, 0, "\(mood) clipped")
        }
    }

    func testItStartsAndEndsQuietly() throws {
        // A bed that starts at full amplitude begins with a click, and every
        // listener hears the click.
        let bed = try gen.synthesise(spec(duration: 2))
        let bytes = [UInt8](bed.pcm)

        func sample(_ frame: Int) -> Int {
            let i = frame * 2
            return abs(Int(Int16(bitPattern: UInt16(bytes[i]) | UInt16(bytes[i + 1]) << 8)))
        }
        let frames = bytes.count / 2
        XCTAssertLessThan(sample(0), 200)
        XCTAssertLessThan(sample(frames - 1), 200)
    }

    func testEveryMoodRendersAndTheyDoNotAllSoundTheSame() throws {
        // The voicing table is the composition; if two moods produce identical
        // audio the table is not being read.
        var rendered: [MusicMood: Data] = [:]
        for mood in MusicMood.allCases {
            rendered[mood] = try gen.synthesise(spec(mood: mood, duration: 1)).pcm
        }
        XCTAssertEqual(rendered.count, MusicMood.allCases.count)
        XCTAssertEqual(Set(rendered.values).count, MusicMood.allCases.count,
                       "two moods rendered identically")
    }

    func testTempoChangesTheAudioButNotItsLength() throws {
        let slow = try gen.synthesise(spec(tempo: 60, duration: 2))
        let fast = try gen.synthesise(spec(tempo: 140, duration: 2))
        XCTAssertEqual(slow.pcm.count, fast.pcm.count)
        XCTAssertNotEqual(slow.pcm, fast.pcm)
    }

    // MARK: - Musical selection

    func testBrightScalesGetTheBrightProgression() {
        for scale in [Scale.major, .majorPentatonic, .dorian] {
            XCTAssertEqual(ProceduralMusicBedGenerator.progression(for: scale), [0, 4, 5, 3],
                           "\(scale)")
        }
    }

    func testDarkScalesGetTheDarkProgression() {
        for scale in Scale.allCases
        where ![Scale.major, .majorPentatonic, .dorian].contains(scale) {
            XCTAssertEqual(ProceduralMusicBedGenerator.progression(for: scale), [0, 5, 2, 6],
                           "\(scale)")
        }
    }

    func testEveryProgressionDegreeIsReachableInAFiveNoteScale() {
        // Degrees are 0-based scale steps and the voicing wraps octaves, which
        // is the only reason one table serves pentatonic and diatonic alike.
        let pentatonic = MusicTheory.intervals(.majorPentatonic)
        XCTAssertEqual(pentatonic.count, 5)
        for scale in Scale.allCases {
            for degree in ProceduralMusicBedGenerator.progression(for: scale) {
                let chord = MusicTheory.chordVoicing(
                    tonicMidi: 60, intervals: MusicTheory.intervals(scale), degree: degree)
                XCTAssertEqual(chord.count, 4, "\(scale) degree \(degree)")
                XCTAssertTrue(chord.allSatisfy { $0 > 0 && $0 < 128 })
            }
        }
    }

    func testEveryMoodHasAVoicingAndTheGainsAreSane() {
        for mood in MusicMood.allCases {
            let v = ProceduralMusicBedGenerator.voicing(for: mood)
            XCTAssertGreaterThan(v.arpPerBeat, 0, "\(mood)")
            XCTAssertEqual(v.arpPattern.count, 4, "\(mood)")
            XCTAssertTrue(v.arpPattern.allSatisfy { (0...3).contains($0) }, "\(mood)")
            XCTAssertTrue((1...3).contains(v.harmonics), "\(mood)")
            // The two layers together must leave the limiter something to do
            // rather than everything.
            XCTAssertLessThan(v.arpGain + v.padGain, 1.0, "\(mood)")
            XCTAssertGreaterThan(v.arpGain, v.padGain,
                                 "\(mood): the arpeggio leads, the pad supports")
        }
    }

    func testMoreEnergeticMoodsPlayMoreNotes() {
        XCTAssertGreaterThan(ProceduralMusicBedGenerator.voicing(for: .energetic).arpPerBeat,
                             ProceduralMusicBedGenerator.voicing(for: .calm).arpPerBeat)
        XCTAssertGreaterThan(ProceduralMusicBedGenerator.voicing(for: .energetic).harmonics,
                             ProceduralMusicBedGenerator.voicing(for: .calm).harmonics)
    }

    // MARK: - Wav

    func testTheBedCanBeWrittenAsAWavFile() throws {
        let wav = try gen.synthesise(spec(duration: 1)).toWav()
        XCTAssertGreaterThan(wav.count, 44)
        XCTAssertEqual([UInt8](wav.prefix(4)), Array("RIFF".utf8))
        XCTAssertEqual([UInt8](wav[8..<12]), Array("WAVE".utf8))
    }

    // MARK: - It is the default backend

    func testItReportsTheProceduralBackend() {
        XCTAssertEqual(gen.backend, .procedural)
    }

    func testTheAsyncFormAgreesWithTheSynchronousOne() async throws {
        let sync = try gen.synthesise(spec())
        let async = try await gen.generate(spec())
        XCTAssertEqual(sync.pcm, async.pcm)
    }
}
