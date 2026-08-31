// MusicTests.swift
//
// The WAV header and the theory maths are the parts a port gets subtly wrong:
// a byte in the wrong place makes a file nothing will open, and truncating
// instead of flooring sends a scale degree the wrong way.

import XCTest
@testable import CircleAI

final class MusicTests: XCTestCase {

    func test_pcm_format_derivations() {
        let f = AudioPcmFormat.bedDefault
        XCTAssertEqual(f.bytesPerSample, 2)
        XCTAssertEqual(f.blockAlign, 2)
        XCTAssertEqual(f.byteRate, 88_200)
        XCTAssertEqual(AudioPcmFormat.cdStereo.blockAlign, 4)
        XCTAssertEqual(AudioPcmFormat.cdStereo.byteRate, 176_400)
    }

    func test_mood_defaults_match_the_reference_tables() {
        XCTAssertEqual(MusicSpec.forMood(.reflective, duration: 10).tempo, 66)
        XCTAssertEqual(MusicSpec.forMood(.energetic, duration: 10).tempo, 128)
        XCTAssertEqual(MusicSpec.forMood(.neutral, duration: 10).tempo, 96)
        XCTAssertEqual(MusicSpec.forMood(.reflective, duration: 10).key, .aMinor)
        XCTAssertEqual(MusicSpec.forMood(.calm, duration: 10).key, .dMinor)
        XCTAssertEqual(MusicSpec.forMood(.playful, duration: 10).key, .cMajorPentatonic)
        XCTAssertEqual(MusicSpec.forMood(.uplifting, duration: 10).key, .gMajor)
        XCTAssertEqual(MusicSpec.forMood(.corporate, duration: 10).key, .cMajor)
    }

    func test_every_mood_produces_a_spec_it_accepts() {
        for m in MusicMood.allCases {
            XCTAssertNoThrow(try MusicSpec.forMood(m, duration: 10).validate(),
                             "\(m) produces a spec it then rejects")
        }
    }

    func test_validate_rejects_an_impossible_tempo() {
        XCTAssertThrowsError(try MusicSpec(mood: .calm, tempo: 39, duration: 10, key: .cMajor).validate())
        XCTAssertThrowsError(try MusicSpec(mood: .calm, tempo: 241, duration: 10, key: .cMajor).validate())
        XCTAssertNoThrow(try MusicSpec(mood: .calm, tempo: 40, duration: 10, key: .cMajor).validate())
        XCTAssertNoThrow(try MusicSpec(mood: .calm, tempo: 240, duration: 10, key: .cMajor).validate())
    }

    func test_validate_rejects_a_zero_or_overlong_duration() {
        XCTAssertThrowsError(try MusicSpec(mood: .calm, tempo: 90, duration: 0, key: .cMajor).validate())
        XCTAssertThrowsError(try MusicSpec(mood: .calm, tempo: 90, duration: 301, key: .cMajor).validate())
        XCTAssertNoThrow(try MusicSpec(mood: .calm, tempo: 90, duration: 300, key: .cMajor).validate())
    }

    func test_the_same_spec_seeds_the_same_bed_every_time() {
        // The point of the FNV departure from C#'s randomised HashCode.Combine:
        // "same spec, same audio" must hold between runs, not just within one.
        XCTAssertEqual(MusicSpec.forMood(.calm, duration: 30).effectiveSeed(),
                       MusicSpec.forMood(.calm, duration: 30).effectiveSeed())
    }

    func test_different_specs_seed_differently() {
        XCTAssertNotEqual(MusicSpec.forMood(.calm, duration: 30).effectiveSeed(),
                          MusicSpec.forMood(.energetic, duration: 30).effectiveSeed())
        XCTAssertNotEqual(MusicSpec.forMood(.calm, duration: 30).effectiveSeed(),
                          MusicSpec.forMood(.calm, duration: 31).effectiveSeed())
    }

    func test_an_explicit_seed_wins_and_is_never_zero() {
        var s = MusicSpec.forMood(.calm, duration: 30)
        s.seed = 12345
        XCTAssertEqual(s.effectiveSeed(), 12345)
        for m in MusicMood.allCases {
            XCTAssertNotEqual(MusicSpec.forMood(m, duration: 15).effectiveSeed(), 0,
                              "a zero seed collapses most PRNGs to silence")
        }
    }
}

extension MusicTests {

    // MARK: - Theory

    func test_scale_intervals() {
        XCTAssertEqual(MusicTheory.intervals(.major), [0, 2, 4, 5, 7, 9, 11])
        XCTAssertEqual(MusicTheory.intervals(.minor), [0, 2, 3, 5, 7, 8, 10])
        XCTAssertEqual(MusicTheory.intervals(.majorPentatonic), [0, 2, 4, 7, 9])
    }

    func test_middle_c_and_concert_a() {
        XCTAssertEqual(MusicTheory.midiNote(root: .c, octave: 4), 60)
        XCTAssertEqual(MusicTheory.midiNote(root: .a, octave: 4), 69)
        XCTAssertEqual(MusicTheory.frequency(midiNote: 69), 440.0, accuracy: 1e-9)
        XCTAssertEqual(MusicTheory.frequency(midiNote: 81), 880.0, accuracy: 1e-9)
        XCTAssertEqual(MusicTheory.frequency(midiNote: 60), 261.6255653, accuracy: 1e-6)
    }

    func test_a_scale_degree_wraps_up_an_octave() {
        let maj = MusicTheory.intervals(.major)
        XCTAssertEqual(MusicTheory.degreeToMidi(tonicMidi: 60, intervals: maj, degree: 0), 60)
        XCTAssertEqual(MusicTheory.degreeToMidi(tonicMidi: 60, intervals: maj, degree: 7), 72)
    }

    func test_a_negative_degree_falls_rather_than_rising() {
        // Swift's / truncates toward zero, so degree -1 would land ABOVE the
        // tonic without an explicit floor. The C# uses Math.Floor for this.
        let below = MusicTheory.degreeToMidi(
            tonicMidi: 60, intervals: MusicTheory.intervals(.major), degree: -1)
        XCTAssertLessThan(below, 60, "degree -1 must be below the tonic")
        XCTAssertEqual(below, 59)
    }

    func test_a_major_triad_is_root_third_fifth_octave() {
        XCTAssertEqual(
            MusicTheory.chordVoicing(tonicMidi: 60,
                                     intervals: MusicTheory.intervals(.major), degree: 0),
            [60, 64, 67, 72])
    }

    // MARK: - WAV

    func test_wav_header_is_44_bytes_and_well_formed() {
        let wav = WavWriter.toWav(Data(count: 100), format: .bedDefault)
        XCTAssertEqual(wav.count, 144)

        func str(_ r: Range<Int>) -> String { String(decoding: wav[r], as: UTF8.self) }
        func u32(_ o: Int) -> UInt32 {
            wav[o..<o+4].reversed().reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        }
        func u16(_ o: Int) -> UInt16 {
            wav[o..<o+2].reversed().reduce(UInt16(0)) { ($0 << 8) | UInt16($1) }
        }

        XCTAssertEqual(str(0..<4), "RIFF")
        XCTAssertEqual(u32(4), 136, "RIFF size is everything after the first 8 bytes")
        XCTAssertEqual(str(8..<12), "WAVE")
        XCTAssertEqual(str(12..<16), "fmt ")
        XCTAssertEqual(u32(16), 16, "PCM fmt chunk is 16 bytes")
        XCTAssertEqual(u16(20), 1, "format tag 1 = PCM")
        XCTAssertEqual(u16(22), 1, "channels")
        XCTAssertEqual(u32(24), 44_100)
        XCTAssertEqual(u32(28), 88_200, "byte rate")
        XCTAssertEqual(u16(32), 2, "block align")
        XCTAssertEqual(u16(34), 16, "bits per sample")
        XCTAssertEqual(str(36..<40), "data")
        XCTAssertEqual(u32(40), 100)
    }

    func test_wav_of_no_samples_is_just_the_header() {
        XCTAssertEqual(WavWriter.toWav(Data(), format: .bedDefault).count, 44)
    }

    // MARK: - Generators

    func test_the_null_generator_returns_silence_of_the_right_length() async throws {
        let bed = try await NullMusicBedGenerator().generate(MusicSpec.forMood(.calm, duration: 2))
        XCTAssertEqual(bed.duration, 2)
        XCTAssertEqual(bed.pcm.count, 2 * 44_100 * 2, "2 s of 16-bit mono at 44.1 kHz")
        XCTAssertTrue(bed.pcm.allSatisfy { $0 == 0 })
        XCTAssertEqual(bed.toWav().count, 44 + bed.pcm.count)
    }

    func test_the_null_generator_still_validates() async {
        do {
            _ = try await NullMusicBedGenerator()
                .generate(MusicSpec(mood: .calm, tempo: 1, duration: 2, key: .cMajor))
            XCTFail("an impossible tempo should not render")
        } catch {}
    }

    func test_the_resolver_falls_back_rather_than_failing() {
        // Asking for a neural backend nobody registered must still return music.
        let r = MusicBedGeneratorResolver()
        XCTAssertEqual(r.resolve(.neural).backend, .procedural)
        XCTAssertTrue(r.available.isEmpty)
    }

    func test_the_resolver_prefers_a_registered_generator() {
        let r = MusicBedGeneratorResolver(generators: [.procedural: NullMusicBedGenerator()])
        XCTAssertEqual(r.available, [.procedural])
    }
}
