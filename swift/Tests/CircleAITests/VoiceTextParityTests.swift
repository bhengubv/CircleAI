// VoiceTextParityTests.swift
//
// Asserts the Swift SentenceSplitter / LanguageSpanSplitter / GeezRomanizer /
// ToneShaper / NchltPhonemizer ports against the same golden files the C#
// reference generates.
//
// Every case in these fixtures is adversarial. The splitter fixture carries a
// decimal point and a domain name that must NOT split next to a danda and a CJK
// stop that must; the Ge'ez fixture carries the numerals that used to romanise
// as syllables; the tone fixture separates the biquad (bit-reproducible) from
// the coefficient derivation (pow/sin/cos, which no language guarantees to the
// last bit).

import XCTest
@testable import CircleAI

final class VoiceTextParityTests: XCTestCase {

    // WALK UP UNTIL fixtures/ IS FOUND rather than counting directories: the
    // test binary's location differs between `swift test` and Xcode.
    private func fixturesDirectory() throws -> URL {
        var dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = dir.appendingPathComponent("fixtures")
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDir),
               isDir.boolValue {
                return candidate
            }
            dir = dir.deletingLastPathComponent()
        }
        throw XCTSkip("no fixtures/ directory above \(#filePath)")
    }

    private func readFixture(_ name: String) throws -> [String: Any] {
        let url = try fixturesDirectory().appendingPathComponent(name)
        let data = try Data(contentsOf: url)
        return try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any],
            "\(name) is not a JSON object")
    }

    private func assertClose(
        _ got: Double, _ want: Double, _ tol: Double, _ what: String,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        let scale = Swift.max(1.0, Swift.abs(want))
        XCTAssertLessThanOrEqual(
            Swift.abs(got - want), tol * scale,
            "\(what): got \(got), want \(want) (tolerance \(tol))", file: file, line: line)
    }

    // ── SentenceSplitter ────────────────────────────────────────────────────

    func testSentenceSplitterMatchesReference() throws {
        let fixture = try readFixture("voice_sentence_splitter.json")
        XCTAssertEqual(
            SentenceSplitter.maxCharsPerSegment,
            try XCTUnwrap(fixture["maxCharsPerSegment"] as? Int))

        for case let c as [String: Any] in try XCTUnwrap(fixture["cases"] as? [Any]) {
            let name = c["name"] as? String ?? "?"
            let want = (c["segments"] as? [[String: Any]] ?? []).map {
                SpeechSegment(
                    text: $0["text"] as? String ?? "",
                    trailingPauseMs: $0["trailingPauseMs"] as? Int ?? -1)
            }
            XCTAssertEqual(SentenceSplitter.split(c["text"] as? String), want, name)
        }
    }

    func testSplitsScriptsThatDoNotPunctuateInLatin() throws {
        // A Latin-only terminator list under-splits for about a billion people and
        // fails silently — the paragraph simply runs together.
        let fixture = try readFixture("voice_sentence_splitter.json")
        let cases = try XCTUnwrap(fixture["cases"] as? [[String: Any]])
        for name in ["devanagari-danda", "urdu-full-stop", "cjk-no-space", "khmer-khan"] {
            let c = try XCTUnwrap(cases.first { $0["name"] as? String == name })
            XCTAssertGreaterThan(
                SentenceSplitter.split(c["text"] as? String).count, 1, "\(name) must split")
        }
    }

    func testDoesNotSplitDecimalOrDomain() throws {
        let fixture = try readFixture("voice_sentence_splitter.json")
        let cases = try XCTUnwrap(fixture["cases"] as? [[String: Any]])
        for name in ["decimal-point", "domain-name"] {
            let c = try XCTUnwrap(cases.first { $0["name"] as? String == name })
            XCTAssertEqual(SentenceSplitter.split(c["text"] as? String).count, 2, name)
        }
    }

    func testLastSegmentHasNoTrailingPause() throws {
        let fixture = try readFixture("voice_sentence_splitter.json")
        for case let c as [String: Any] in try XCTUnwrap(fixture["cases"] as? [Any]) {
            let got = SentenceSplitter.split(c["text"] as? String)
            if let last = got.last {
                XCTAssertEqual(last.trailingPauseMs, 0, c["name"] as? String ?? "?")
            }
        }
    }

    // ── LanguageSpanSplitter ────────────────────────────────────────────────

    func testLanguageSpansMatchReference() throws {
        let fixture = try readFixture("voice_language_spans.json")

        for case let c as [String: Any] in try XCTUnwrap(fixture["split"] as? [Any]) {
            let text = c["text"] as? String ?? ""
            let want = (c["spans"] as? [[String: Any]] ?? []).map {
                LanguageSpan(
                    text: $0["text"] as? String ?? "",
                    isForeign: $0["isForeign"] as? Bool ?? false)
            }
            XCTAssertEqual(LanguageSpanSplitter.split(text), want, "spans for \(text)")
        }

        for case let c as [String: Any] in try XCTUnwrap(fixture["toSpokenForm"] as? [Any]) {
            let input = c["input"] as? String ?? ""
            XCTAssertEqual(
                LanguageSpanSplitter.toSpokenForm(input), c["output"] as? String,
                "spoken form of \(input)")
        }

        for case let c as [String: Any] in try XCTUnwrap(fixture["isForeignWord"] as? [Any]) {
            let word = c["word"] as? String ?? ""
            XCTAssertEqual(
                LanguageSpanSplitter.isForeignWord(word), c["foreign"] as? Bool,
                "isForeignWord(\(word))")
        }
    }

    func testOrdinaryWordIsNeverFlaggedAsForeign() {
        // The conservatism is the contract, not an accident: guessing wrong
        // mispronounces a native word to fix a foreign one.
        XCTAssertFalse(LanguageSpanSplitter.isForeignWord("hello"))
        XCTAssertFalse(LanguageSpanSplitter.isForeignWord("Ngiyabonga"))
    }

    // ── GeezRomanizer ───────────────────────────────────────────────────────

    func testGeezRomanizerMatchesReference() throws {
        let fixture = try readFixture("voice_geez_romanizer.json")

        for case let c as [String: Any] in try XCTUnwrap(fixture["isEthiopic"] as? [Any]) {
            let text = c["text"] as? String ?? ""
            XCTAssertEqual(
                GeezRomanizer.isEthiopic(text), c["ethiopic"] as? Bool, "isEthiopic(\(text))")
        }

        for case let c as [String: Any] in try XCTUnwrap(fixture["romanize"] as? [Any]) {
            let input = c["input"] as? String ?? ""
            XCTAssertEqual(
                GeezRomanizer.romanize(input), c["output"] as? String, "romanize(\(input))")
        }
    }

    func testNumeralsAreDroppedNotSpoken() {
        // The eight-per-consonant layout stops at U+1357. Sizing the range check
        // off the consonant table swept seven numerals back into the syllabary,
        // and they came out as sound, so nothing failed.
        XCTAssertEqual(GeezRomanizer.romanize("፩፪፫"), "")
        XCTAssertEqual(
            GeezRomanizer.romanize("ፘፙፚ"), "ryamyafya",
            "the three LONE syllables are not a row of eight")
    }

    // ── ToneShaper ──────────────────────────────────────────────────────────

    func testToneShaperSettings() throws {
        // Field by field, and NOT against the whole fixture object: the shelf
        // slope is a private constant of the filter, not a settable value.
        let s = try XCTUnwrap(try readFixture("voice_tone_shaper.json")["settings"]
                              as? [String: Any])
        XCTAssertEqual(ToneShaper.warm.lowShelfHz, s["lowShelfHz"] as? Double)
        XCTAssertEqual(ToneShaper.warm.lowShelfDb, s["lowShelfDb"] as? Double)
        XCTAssertEqual(ToneShaper.warm.presenceHz, s["presenceHz"] as? Double)
        XCTAssertEqual(ToneShaper.warm.presenceDb, s["presenceDb"] as? Double)
        XCTAssertEqual(ToneShaper.warm.presenceQ, s["presenceQ"] as? Double)
        XCTAssertEqual(s["lowShelfSlope"] as? Double, 0.9)
    }

    func testToneShaperCoefficients() throws {
        // 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
        // languages, and pretending otherwise makes a flaky test, not a strict one.
        let fixture = try readFixture("voice_tone_shaper.json")
        let tol = try XCTUnwrap(fixture["coefficientTolerance"] as? Double)

        for case let c as [String: Any] in try XCTUnwrap(fixture["coefficients"] as? [Any]) {
            let rate = try XCTUnwrap(c["sampleRate"] as? Int)
            let got = [
                "lowShelf": ToneShaper.lowShelf(ToneShaper.warm, rate: rate),
                "peaking": ToneShaper.peaking(ToneShaper.warm, rate: rate),
            ]
            for name in ["lowShelf", "peaking"] {
                let want = try XCTUnwrap(c[name] as? [String: Any])
                let wb = try XCTUnwrap(want["b"] as? [Double])
                let wa = try XCTUnwrap(want["a"] as? [Double])
                for i in 0..<3 {
                    assertClose(got[name]!.b[i], wb[i], tol, "\(name) b[\(i)] at \(rate)")
                    assertClose(got[name]!.a[i], wa[i], tol, "\(name) a[\(i)] at \(rate)")
                }
            }
        }
    }

    func testToneShaperWaveform() throws {
        // The biquad is add and multiply on doubles, so THIS half is expected to
        // agree everywhere. Driving it from the fixture's own coefficients keeps
        // the transcendental functions out of the comparison.
        let fixture = try readFixture("voice_tone_shaper.json")
        let w = try XCTUnwrap(fixture["waveform"] as? [String: Any])
        let rate = try XCTUnwrap(w["sampleRate"] as? Int)
        let coeffs = try XCTUnwrap(
            (fixture["coefficients"] as? [[String: Any]])?
                .first { $0["sampleRate"] as? Int == rate })

        var x = try XCTUnwrap(w["input"] as? [Double]).map { Float($0) }
        let before = x.map { Swift.abs($0) }.max() ?? 0
        ToneShaper.biquad(&x, try coefficients(from: coeffs, key: "lowShelf"))
        ToneShaper.biquad(&x, try coefficients(from: coeffs, key: "peaking"))
        let after = x.map { Swift.abs($0) }.max() ?? 0
        if after > 0 && after > before {
            let g = before / after
            for i in x.indices { x[i] *= g }
        }

        let want = try XCTUnwrap(w["output"] as? [Double])
        let tol = try XCTUnwrap(fixture["waveformTolerance"] as? Double)
        for i in want.indices {
            assertClose(Double(x[i]), want[i], tol, "sample \(i)")
        }
    }

    func testSilenceStaysSilent() throws {
        let fixture = try readFixture("voice_tone_shaper.json")
        let want = try XCTUnwrap(fixture["silenceStaysSilent"] as? [Double])
        let rate = try XCTUnwrap(
            (fixture["waveform"] as? [String: Any])?["sampleRate"] as? Int)

        var silence = [Float](repeating: 0, count: want.count)
        ToneShaper.apply(&silence, sampleRate: rate)
        for i in want.indices {
            XCTAssertEqual(Double(silence[i]), want[i], "silence \(i)")
        }
    }

    func testBothFiltersAreApplied() throws {
        // A port that dropped the presence dip would still change the waveform, so
        // "it moved" proves nothing — the two stages must differ from each other.
        let fixture = try readFixture("voice_tone_shaper.json")
        let w = try XCTUnwrap(fixture["waveform"] as? [String: Any])
        let rate = try XCTUnwrap(w["sampleRate"] as? Int)
        let input = try XCTUnwrap(w["input"] as? [Double]).map { Float($0) }

        var both = input
        var onlyShelf = input
        ToneShaper.apply(&both, sampleRate: rate)
        ToneShaper.biquad(&onlyShelf, ToneShaper.lowShelf(ToneShaper.warm, rate: rate))

        XCTAssertTrue(
            both.indices.contains { Swift.abs(both[$0] - onlyShelf[$0]) > 1e-4 },
            "the presence dip made no difference — it was not applied")
    }

    private func coefficients(from o: [String: Any], key: String) throws -> BiquadCoefficients {
        let c = try XCTUnwrap(o[key] as? [String: Any])
        return BiquadCoefficients(
            b: try XCTUnwrap(c["b"] as? [Double]),
            a: try XCTUnwrap(c["a"] as? [Double]))
    }

    // ── NchltPhonemizer ─────────────────────────────────────────────────────

    private func makePhonemizer(_ fixture: [String: Any]) throws -> NchltPhonemizer {
        NchltPhonemizer.fromText(
            dict: try XCTUnwrap(fixture["dict"] as? String),
            rules: try XCTUnwrap(fixture["rules"] as? String),
            phoneMap: try XCTUnwrap(fixture["phoneMap"] as? String),
            graphMap: fixture["graphMap"] as? String,
            gnulls: fixture["gnulls"] as? String)
    }

    func testNchltMatchesReference() throws {
        let fixture = try readFixture("voice_nchlt_phonemizer.json")

        for case let c as [String: Any] in try XCTUnwrap(fixture["cases"] as? [Any]) {
            let name = c["name"] as? String ?? "?"
            let p = try makePhonemizer(fixture)
            XCTAssertEqual(
                p.phonemize(c["text"] as? String ?? ""), c["phones"] as? [String],
                "phones for \(name)")
            XCTAssertEqual(
                p.lastRulePredictedWords, c["rulePredictedWords"] as? Int,
                "ruleWords for \(name)")
            XCTAssertEqual(
                p.lastUnknownGraphemes, c["unknownGraphemes"] as? [String],
                "unknown for \(name)")
        }

        for case let c as [String: Any] in try XCTUnwrap(fixture["predictWord"] as? [Any]) {
            let word = c["word"] as? String ?? ""
            XCTAssertEqual(
                try makePhonemizer(fixture).predictWord(word), c["phones"] as? [String],
                "predictWord(\(word))")
        }
    }

    func testDictionaryBeatsTheRules() throws {
        // Both paths can pronounce this word. The dictionary must win, and the
        // rule counter must show it did — the counter is the only evidence of
        // which path ran, and a port that always predicted would still return
        // sensible phones.
        let p = try makePhonemizer(try readFixture("voice_nchlt_phonemizer.json"))
        _ = p.phonemize("sawubona")
        XCTAssertEqual(p.lastRulePredictedWords, 0, "a catalogued word must not be predicted")
    }

    func testUnknownGraphemeIsReported() throws {
        let p = try makePhonemizer(try readFixture("voice_nchlt_phonemizer.json"))
        _ = p.phonemize("azb")
        XCTAssertEqual(p.lastUnknownGraphemes, ["z"])
    }
}
