// VoiceProsodyTests.swift
//
// Japanese is a FOURTH ONNX voice family: this model takes PROSODY, not
// phonemes. Feed it bare phonemes and it speaks — flatly and wrongly, with no
// error anywhere. So these tests are about the brackets.

import XCTest
@testable import CircleAI

final class VoiceProsodyTests: XCTestCase {

    /// A full-context label with the fields this tokeniser reads.
    ///
    /// a3 defaults to 2, not 1. a3 == 1 with a2 == 1 on the NEXT label is the
    /// phrase-border condition, so a helper that defaults a3 to 1 emits a "#"
    /// after every single label, and then every expectation below has to be
    /// written around a boundary the test never meant to describe.
    private func label(_ phoneme: String,
                       a1: Int = 0, a2: Int = 1, a3: Int = 2,
                       f1: Int = 1, e3: Int = 0) -> String {
        "xx^xx-\(phoneme)+r=x/A:\(a1)+\(a2)+\(a3)/B:xx-xx_xx/C:xx/D:xx/E:xx_xx!\(e3)_xx/F:\(f1)_2/G:xx"
    }

    // MARK: - The vocabulary is the model's, not ours

    func testTheVocabularyIsIndexedNotSorted() {
        // The ids ARE the indices, so this list cannot be reordered or tidied.
        XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: 0), "<blank>")
        XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: 1), "<unk>")
        XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: 2), "a")
        XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: 46), "<sos/eos>")
        XCTAssertEqual(OpenJTalkProsodyTokeniser.blankId, 0)
        XCTAssertEqual(OpenJTalkProsodyTokeniser.unkId, 1)
    }

    func testAnIdOutsideTheVocabularyIsNamedNotACrash() {
        XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: -1), "<oob>")
        XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: 999), "<oob>")
    }

    // MARK: - Boundaries

    func testLeadingSilenceBecomesTheStartMarker() {
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [label("sil"), label("a"), label("sil")])
        XCTAssertEqual(t.lastSymbols.first, "^")
    }

    func testTrailingSilenceIsAStatementByDefault() {
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [label("sil"), label("a"), label("sil", e3: 0)])
        XCTAssertEqual(t.lastSymbols.last, "$")
    }

    func testTrailingSilenceWithE3OneIsAQuestion() {
        // The difference between a flat and a rising final contour, and it is
        // written in a field nobody would think to read.
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [label("sil"), label("a"), label("sil", e3: 1)])
        XCTAssertEqual(t.lastSymbols.last, "?")
    }

    func testAPauseBecomesTheUnderscore() {
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [label("a"), label("pau"), label("o")])
        XCTAssertEqual(t.lastSymbols, ["a", "_", "o"])
    }

    func testSilenceInTheMiddleIsNeitherStartNorEnd() {
        // Only the FIRST and LAST labels carry sentence structure; a sil
        // anywhere else contributes nothing and must not emit a marker.
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [label("a"), label("sil"), label("o")])
        XCTAssertEqual(t.lastSymbols, ["a", "o"])
    }

    // MARK: - Devoiced vowels

    func testDevoicedVowelsAreFoldedToPlainOnes() {
        // Open JTalk writes them as capitals and they are NOT in this
        // vocabulary — the model was trained with them folded. Without this,
        // every devoiced vowel becomes <unk>, and that is most sentence-final
        // -masu and -desu.
        let t = OpenJTalkProsodyTokeniser()
        let ids = t.encode(labels: [label("m"), label("A"), label("s"), label("U")])
        XCTAssertEqual(t.lastSymbols, ["m", "a", "s", "u"])
        XCTAssertFalse(ids.contains(OpenJTalkProsodyTokeniser.unkId))
        XCTAssertTrue(t.lastUnknown.isEmpty)
    }

    func testAllFiveDevoicedVowelsFold() {
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: ["A", "E", "I", "O", "U"].map { label($0) })
        XCTAssertEqual(t.lastSymbols, ["a", "e", "i", "o", "u"])
    }

    // MARK: - Accent structure

    func testAPhraseBorderEmitsHash() {
        // a3 == 1 on this mora and a2 == 1 on the next: the accent phrase ends
        // here and a new one starts.
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [
            label("a", a1: 1, a2: 2, a3: 1, f1: 2),
            label("o", a1: 0, a2: 1, a3: 2, f1: 2),
        ])
        XCTAssertTrue(t.lastSymbols.contains("#"))
    }

    func testAPitchFallEmitsCloseBracket() {
        // a1 == 0 means this mora IS the accent nucleus, and the phrase carries
        // on past it — so the pitch drops after it.
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [
            label("a", a1: 0, a2: 2, a3: 3, f1: 4),
            label("o", a1: 1, a2: 3, a3: 2, f1: 4),
        ])
        XCTAssertTrue(t.lastSymbols.contains("]"))
    }

    func testAPitchRiseEmitsOpenBracket() {
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [
            label("a", a1: -1, a2: 1, a3: 3, f1: 3),
            label("o", a1: 0, a2: 2, a3: 2, f1: 3),
        ])
        XCTAssertTrue(t.lastSymbols.contains("["))
    }

    func testOnlyAMoraCarrierGetsAPhraseBorder() {
        // A consonant is mid-mora. Emitting a border on it would put the phrase
        // break inside a syllable.
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: [
            label("k", a1: 1, a2: 2, a3: 1, f1: 2),
            label("o", a1: 0, a2: 1, a3: 2, f1: 2),
        ])
        XCTAssertFalse(t.lastSymbols.contains("#"))
    }

    func testTheGeminateAndMoraicNDoCarryABorder() {
        for carrier in ["cl", "N"] {
            let t = OpenJTalkProsodyTokeniser()
            _ = t.encode(labels: [
                label(carrier, a1: 1, a2: 2, a3: 1, f1: 2),
                label("o", a1: 0, a2: 1, a3: 2, f1: 2),
            ])
            XCTAssertTrue(t.lastSymbols.contains("#"), "\(carrier) should carry a border")
        }
    }

    // MARK: - Absent fields

    func testAnAbsentFieldNeverCompaesEqualToARealOne() {
        // 0 and -1 are both legitimate values here, so "not present" has to sit
        // far away from every real answer or a missing field starts emitting
        // brackets of its own.
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: ["garbage-a+x=y", "garbage-o+x=y"])
        XCTAssertEqual(t.lastSymbols, ["a", "o"])   // no brackets from missing fields
    }

    func testALabelWithNoPhonemeFieldIsSkipped() {
        let t = OpenJTalkProsodyTokeniser()
        _ = t.encode(labels: ["not a label at all", label("a")])
        XCTAssertEqual(t.lastSymbols, ["a"])
    }

    // MARK: - Unknown symbols

    func testUnknownSymbolsBecomeUnkAndAreReported() {
        // Each one is a silent flat spot in the prosody.
        let t = OpenJTalkProsodyTokeniser()
        let ids = t.encode(labels: [label("a"), label("zzz")])
        XCTAssertEqual(ids.count, 2)
        XCTAssertEqual(ids[1], OpenJTalkProsodyTokeniser.unkId)
        XCTAssertEqual(t.lastUnknown, ["zzz"])
    }

    // MARK: - String form

    func testTheStringOverloadSplitsAndTrims() {
        let t = OpenJTalkProsodyTokeniser()
        let joined = "\n  \(label("a"))  \n\(label("o"))\n\n"
        XCTAssertEqual(t.encode(labels: joined), t.encode(labels: [label("a"), label("o")]))
        XCTAssertEqual(t.lastSymbols, ["a", "o"])
    }

    func testEmptyInputIsEmptyOutput() {
        let t = OpenJTalkProsodyTokeniser()
        XCTAssertTrue(t.encode(labels: "").isEmpty)
        XCTAssertTrue(t.encode(labels: [String]()).isEmpty)
        XCTAssertTrue(t.lastSymbols.isEmpty)
        XCTAssertTrue(t.lastUnknown.isEmpty)
    }

    func testEveryEmittedSymbolMapsToAnIdOfTheRightSize() {
        let t = OpenJTalkProsodyTokeniser()
        let ids = t.encode(labels: [label("sil"), label("k"), label("o"), label("sil")])
        XCTAssertEqual(ids.count, t.lastSymbols.count)
        for (i, id) in ids.enumerated() {
            XCTAssertEqual(OpenJTalkProsodyTokeniser.symbol(for: id), t.lastSymbols[i])
        }
    }
}
