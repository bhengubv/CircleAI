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
}
