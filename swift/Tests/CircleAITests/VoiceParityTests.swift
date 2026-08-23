// VoiceParityTests.swift
//
// Asserts the Swift voice port against the SAME golden files the C# reference
// generates (tools/voice-fixtures). Not "does Swift do something sensible" —
// "does Swift produce byte-identical answers to every other port".
//
// The fixtures are chosen to be adversarial. The SentencePiece vocabulary is
// built so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases
// include a multi-character token, the script-g that is U+0261 rather than
// ASCII 'g', and a phone that cannot map and must be REPORTED rather than
// quietly dropped.

import XCTest
import Foundation
@testable import CircleAI

final class VoiceParityTests: XCTestCase {

    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/  (repo root)
            .appendingPathComponent("fixtures")
    }()

    private func load(_ name: String) throws -> [String: Any] {
        let data = try Data(contentsOf: fixturesDir.appendingPathComponent(name))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    // ── X-SAMPA → IPA ───────────────────────────────────────────────────────

    func testXsampaToIpaMatchesReference() throws {
        let fixture = try load("voice_xsampa_to_ipa.json")
        let cases = fixture["cases"] as! [[String: Any]]
        XCTAssertFalse(cases.isEmpty, "fixture has no cases")

        for c in cases {
            let xsampa = c["xsampa"] as! [String]
            let expectedIpa = c["ipa"] as! [String]
            let expectedUnmapped = c["unmapped"] as! [String]
            let expectedCanSayAll = c["canSayAll"] as! Bool

            let actual = VoiceXsampaToIpa.convert(xsampa)
            XCTAssertEqual(actual, expectedIpa, "ipa for \(xsampa)")
            XCTAssertEqual(VoiceXsampaToIpa.lastUnmapped, expectedUnmapped, "unmapped for \(xsampa)")
            XCTAssertEqual(VoiceXsampaToIpa.canSayAll(xsampa), expectedCanSayAll, "canSayAll for \(xsampa)")
        }
    }

    func testXsampaKnownPhonesMatchReference() throws {
        let fixture = try load("voice_xsampa_to_ipa.json")
        let expected = Set(fixture["knownPhones"] as! [String])
        XCTAssertEqual(Set(VoiceXsampaToIpa.knownPhones), expected,
                       "the phone table itself has drifted from the reference")
    }

    func testScriptGIsU0261NotAsciiG() {
        // Called out on its own because it is invisible in a diff: the voice's
        // vocabulary carries ɡ (U+0261) and a plain ASCII 'g' silently misses.
        let ipa = VoiceXsampaToIpa.convert(["g"])
        XCTAssertEqual(ipa, ["\u{0261}"])
        XCTAssertNotEqual(ipa, ["g"], "ASCII g would be dropped by the voice")
    }

    // ── SentencePiece unigram ───────────────────────────────────────────────

    private func makeTokeniser(_ fixture: [String: Any]) -> VoiceSentencePieceUnigram {
        let vocab = fixture["vocab"] as! [String: Int]
        let rawScores = fixture["scores"] as! [String: NSNumber]
        var scores: [String: Float] = [:]
        for (k, v) in rawScores { scores[k] = v.floatValue }
        return VoiceSentencePieceUnigram(ids: vocab, scores: scores)
    }

    func testSentencePieceMatchesReference() throws {
        let fixture = try load("voice_sentencepiece_unigram.json")
        let sp = makeTokeniser(fixture)
        let cases = fixture["cases"] as! [[String: Any]]
        XCTAssertFalse(cases.isEmpty, "fixture has no cases")

        for c in cases {
            let text = c["text"] as! String
            let expected = (c["ids"] as! [NSNumber]).map(\.intValue)
            XCTAssertEqual(sp.encode(text), expected, "ids for \(text.debugDescription)")
        }
    }

    func testViterbiNotGreedy() throws {
        // The fixture vocabulary is built so the two disagree: "▁hello" scores
        // WORSE than "▁hell" + "o". Greedy picks the long piece; Viterbi does
        // not. Without this, a greedy port looks correct.
        let fixture = try load("voice_sentencepiece_unigram.json")
        let sp = makeTokeniser(fixture)
        let vocab = fixture["vocab"] as! [String: Int]

        let ids = sp.encode("hello world")
        XCTAssertEqual(ids, [vocab["▁hell"]!, vocab["o"]!, vocab["▁world"]!])
        XCTAssertNotEqual(ids, [vocab["▁hello"]!, vocab["▁world"]!],
                          "this is the greedy answer — the port is not doing Viterbi")
    }

    func testByteFallbackKeepsUtf8Order() throws {
        // é is UTF-8 C3 A9. Emitting A9 C3 does not throw — both are real pieces
        // with real ids — the model just says a different character, and only
        // outside ASCII, which is exactly the languages this catalogue serves.
        let fixture = try load("voice_sentencepiece_unigram.json")
        let sp = makeTokeniser(fixture)
        let vocab = fixture["vocab"] as! [String: Int]

        let ids = sp.encode("hé")
        XCTAssertEqual(ids.suffix(2), [vocab["<0xC3>"]!, vocab["<0xA9>"]!],
                       "byte fallback emitted UTF-8 bytes in the wrong order")
    }

    func testEmptyTextEncodesToNothing() throws {
        let fixture = try load("voice_sentencepiece_unigram.json")
        XCTAssertEqual(makeTokeniser(fixture).encode(""), [])
    }

    // ── WAV I/O ─────────────────────────────────────────────────────────────

    func testWavIoMatchesReference() throws {
        let fixture = try load("voice_wav_io.json")
        let cases = fixture["cases"] as! [[String: Any]]
        XCTAssertFalse(cases.isEmpty, "fixture has no cases")

        for c in cases {
            let name = c["name"] as! String
            let raw = Data(base64Encoded: c["wavBase64"] as! String)!
            let expected = c["expected"] as! [String: Any]
            let expectedSamples = (expected["samples"] as! [NSNumber]).map(\.floatValue)

            let wav = try VoiceWavIo.read(raw, path: name)
            let mono = wav.channels > 1 || wav.rate != VoiceWavIo.targetRate
                ? try monoFrom(raw, name: name)
                : wav.samples

            XCTAssertEqual(mono.count, expected["sampleCount"] as! Int, "sampleCount for \(name)")
            for (i, want) in expectedSamples.enumerated() {
                XCTAssertEqual(mono[i], want, accuracy: 1e-6, "sample \(i) of \(name)")
            }
        }
    }

    /// The LIST-chunk case is the one that matters: a reader that assumes data
    /// starts at byte 44 reads metadata as audio.
    func testWavIoWalksChunksRatherThanAssumingByte44() throws {
        let fixture = try load("voice_wav_io.json")
        let cases = fixture["cases"] as! [[String: Any]]
        let plain = cases.first { ($0["name"] as! String).contains("plain") }!
        let listed = cases.first { ($0["name"] as! String).contains("LIST") }!

        let a = try VoiceWavIo.read(Data(base64Encoded: plain["wavBase64"] as! String)!)
        let b = try VoiceWavIo.read(Data(base64Encoded: listed["wavBase64"] as! String)!)
        XCTAssertEqual(a.samples, b.samples,
                       "a LIST chunk before the data changed the decoded audio")
    }

    private func monoFrom(_ raw: Data, name: String) throws -> [Float] {
        let wav = try VoiceWavIo.read(raw, path: name)
        guard wav.channels > 1 else { return wav.samples }
        var mono = [Float](repeating: 0, count: wav.samples.count / wav.channels)
        for i in 0..<mono.count {
            var sum: Float = 0
            for c in 0..<wav.channels { sum += wav.samples[i * wav.channels + c] }
            mono[i] = sum / Float(wav.channels)
        }
        return mono
    }

    // ── PiperVoiceConfig ────────────────────────────────────────────────────

    func testPiperConfigMatchesReference() throws {
        let fixture = try load("voice_piper_config.json")
        let configs = fixture["configs"] as! [[String: Any]]
        XCTAssertEqual(configs.count, 2, "both pad conventions must be covered")

        for c in configs {
            let name = c["name"] as! String
            let rawMap = c["configJson"] as! [String: [NSNumber]]
            var map: [String: [Int64]] = [:]
            for (k, v) in rawMap { map[k] = v.map(\.int64Value) }
            let cfg = VoicePiperConfig(map: map, sampleRate: c["sampleRate"] as! Int)

            XCTAssertEqual(cfg.padId, Int64(c["padId"] as! Int), "padId for \(name)")
            XCTAssertEqual(cfg.hasPhonemeMap, c["hasPhonemeMap"] as! Bool)

            for one in c["cases"] as! [[String: Any]] {
                let phonemes = one["phonemes"] as! [String]
                let got = cfg.phonemesToIds(phonemes)
                XCTAssertEqual(got.ids, (one["ids"] as! [NSNumber]).map(\.int64Value),
                               "ids for \(phonemes) in \(name)")
                XCTAssertEqual(got.skipped, one["skipped"] as! Int, "skipped for \(phonemes)")
                XCTAssertEqual(got.skippedSymbols, one["skippedSymbols"] as! [String],
                               "skippedSymbols for \(phonemes)")
                XCTAssertEqual(got.approximatedSymbols, one["approximatedSymbols"] as! [String],
                               "approximatedSymbols for \(phonemes)")
            }
        }
    }

    func testPadIsReadFromTheModelNotAssumed() throws {
        // THE PAD RULE. The two fixture configs disagree — 0 in the Piper-layout
        // one, 3 in the MMS-layout one — so a port that hard-codes either fails
        // on the other. Pointing `_` at an ordinary vocabulary entry is what made
        // 42 MMS voices speak fluent nonsense.
        let fixture = try load("voice_piper_config.json")
        let configs = fixture["configs"] as! [[String: Any]]
        let pads = configs.map { $0["padId"] as! Int }
        XCTAssertEqual(Set(pads), Set([0, 3]), "the fixture must cover BOTH pad conventions")

        for c in configs {
            let rawMap = c["configJson"] as! [String: [NSNumber]]
            var map: [String: [Int64]] = [:]
            for (k, v) in rawMap { map[k] = v.map(\.int64Value) }
            XCTAssertEqual(VoicePiperConfig(map: map).padId, Int64(c["padId"] as! Int))
        }
    }

    func testThaiIsNotFoldedButTshivendaIs() throws {
        // The asymmetry is the whole point. Latin ṱ still sounds like a t with
        // the mark gone; Thai ก's marks ARE the vowels, so folding deletes the
        // word rather than approximating it.
        let fixture = try load("voice_piper_config.json")
        let c = (fixture["configs"] as! [[String: Any]])[0]
        let rawMap = c["configJson"] as! [String: [NSNumber]]
        var map: [String: [Int64]] = [:]
        for (k, v) in rawMap { map[k] = v.map(\.int64Value) }
        let cfg = VoicePiperConfig(map: map)

        XCTAssertEqual(cfg.phonemesToIds(["ṱ"]).approximatedSymbols, ["ṱ"],
                       "ṱ should fold to a Latin base and be REPORTED as approximate")
        XCTAssertEqual(cfg.phonemesToIds(["ก"]).skippedSymbols, ["ก"],
                       "Thai must be skipped, not folded")
    }

    func testSplitPhonemeStringMatchesReference() throws {
        let fixture = try load("voice_piper_config.json")
        for c in fixture["splitPhonemeString"] as! [[String: Any]] {
            XCTAssertEqual(VoicePiperConfig.splitPhonemeString(c["input"] as! String),
                           c["elements"] as! [String],
                           "grapheme clusters for \(c["input"] as! String)")
        }
    }

    // ── LexiconTokeniser ────────────────────────────────────────────────────

    private func makeLexicon(_ fixture: [String: Any]) -> VoiceLexiconTokeniser {
        let tokens = fixture["tokens"] as! [String: NSNumber]
        let lexicon = fixture["lexicon"] as! [[String: Any]]
        let newline = "\n"
        let tokensText = tokens.map { "\($0.key) \($0.value)" }.joined(separator: newline)
        let lexiconText = lexicon.map {
            "\($0["word"] as! String) \(($0["phonemes"] as! [String]).joined(separator: " "))"
        }.joined(separator: newline)
        return VoiceLexiconTokeniser.from(tokensText: tokensText, lexiconText: lexiconText)!
    }

    func testLexiconTokeniserMatchesReference() throws {
        let fixture = try load("voice_lexicon_tokeniser.json")
        let lex = makeLexicon(fixture)

        for c in fixture["cases"] as! [[String: Any]] {
            let text = c["text"] as! String
            let bare = lex.encode(text, interleaveBlank: false)
            XCTAssertEqual(bare, (c["ids"] as! [NSNumber]).map(\.int64Value), "ids for \(text)")
            XCTAssertEqual(lex.lastUnmapped, c["unmapped"] as! [String], "unmapped for \(text)")
            XCTAssertEqual(lex.encode(text, interleaveBlank: true),
                           (c["idsWithBlank"] as! [NSNumber]).map(\.int64Value),
                           "idsWithBlank for \(text)")
        }
    }

    func testLexiconTakesTheLongestMatch() throws {
        // あい, あいさつ and あいかわらず all start the same way. Taking the
        // shortest pronounces a different word.
        let fixture = try load("voice_lexicon_tokeniser.json")
        let lex = makeLexicon(fixture)
        let full = lex.encode("あいさつ", interleaveBlank: false)
        let short = lex.encode("あい", interleaveBlank: false)
        XCTAssertGreaterThan(full.count, short.count,
                             "あいさつ matched only the あい prefix — this is shortest-match")
    }

    // ── AudioFormat ─────────────────────────────────────────────────────────

    func testAudioFormatMatchesReference() throws {
        let fixture = try load("voice_audio_format.json")
        let want = fixture["pcm16Mono16k"] as! [String: Any]
        XCTAssertEqual(AudioFormat.pcm16Mono16k.sampleRate, want["sampleRate"] as! Int)
        XCTAssertEqual(AudioFormat.pcm16Mono16k.channels, want["channels"] as! Int)
        XCTAssertEqual(AudioFormat.pcm16Mono16k.bitsPerSample, want["bitsPerSample"] as! Int)
    }
}
