// VoicePhonemizersTests.swift

import XCTest
@testable import CircleAI

final class VoicePhonemizersTests: XCTestCase {

    // MARK: - Ge'ez

    func testAmharicIsTransliteratedNotPassedThrough() {
        // The MMS Amharic model's vocabulary is 28 LATIN letters. Handing it
        // Ethiopic produced 3.2 s of noise for a 15 s paragraph on the P30,
        // because the model has never seen an Ethiopic codepoint.
        let p = GeezPhonemizer()
        let out = p.phonemize("ኣማርኛ")
        XCTAssertFalse(out.isEmpty)
        for symbol in out {
            for ch in symbol.unicodeScalars {
                XCTAssertLessThan(ch.value, 0x1200, "Ethiopic survived into the phonemes: \(symbol)")
            }
        }
    }

    func testTheTransliterationIsKeptForDiagnosis() {
        // When a voice sounds wrong the first question is whether the
        // transliteration or the model is at fault. Without this there is no
        // way to tell them apart.
        let p = GeezPhonemizer()
        _ = p.phonemize("ሰላም")
        XCTAssertFalse(p.lastRomanised.isEmpty)
        XCTAssertEqual(p.lastRomanised, GeezRomanizer.romanize("ሰላም"))
    }

    func testEmptyTextGivesNoPhonemes() {
        let p = GeezPhonemizer()
        XCTAssertTrue(p.phonemize("").isEmpty)
        XCTAssertEqual(p.lastRomanised, "")
    }

    func testLatinTextIsLeftAlone() {
        let p = GeezPhonemizer()
        XCTAssertEqual(p.phonemize("hello").joined(), "hello")
    }

    // MARK: - Lexicon

    private let lexicon = """
        你好 n i h ao 3 3 2 3
        你 n i 3 3
        好 h ao 3 3
        世界 sh ir j ie 4 4 4 4
        hello HH AH L OW
        """

    func testGreedyLongestMatchWins() {
        // "你好" must match as one entry, not as "你" then "好" — the two-syllable
        // entry carries the tone sandhi that the single characters do not.
        let p = LexiconPhonemizer.parse(lexicon)
        XCTAssertEqual(p.phonemize("你好"), ["n", "i", "h", "ao"])
        XCTAssertEqual(p.lastTones, [3, 3, 2, 3])
    }

    func testFallingBackToShorterEntriesWhenTheLongOneIsAbsent() {
        let p = LexiconPhonemizer.parse(lexicon)
        // "好你" is not an entry; each character is.
        XCTAssertEqual(p.phonemize("好你"), ["h", "ao", "n", "i"])
        XCTAssertEqual(p.lastTones, [3, 3, 3, 3])
    }

    func testATrailingRunOfIntegersIsTheToneChannel() {
        let p = LexiconPhonemizer.parse("世界 sh ir j ie 4 4 4 4")
        XCTAssertEqual(p.phonemize("世界"), ["sh", "ir", "j", "ie"])
        XCTAssertEqual(p.lastTones, [4, 4, 4, 4])
    }

    func testAnEntryWithNoTonesIsAllPhonemes() {
        // Guessing wrong here is silent in both directions: read as phonemes the
        // digits get looked up and dropped; read as tones half the pronunciation
        // disappears.
        let p = LexiconPhonemizer.parse("hello HH AH L OW")
        XCTAssertEqual(p.phonemize("hello"), ["HH", "AH", "L", "OW"])
        XCTAssertEqual(p.lastTones, [0, 0, 0, 0])
    }

    func testAnOddNumberOfFieldsIsNeverReadAsTones() {
        let p = LexiconPhonemizer.parse("x a b c")
        XCTAssertEqual(p.phonemize("x"), ["a", "b", "c"])
    }

    func testTonesArePaddedSoTheArraysNeverDrift() {
        // Without the pad the two arrays fall out of step at the first gap and
        // every syllable after it gets the wrong tone — audible, never an error.
        let p = LexiconPhonemizer.parse("你好 n i h ao 3 3 2 3\nhello HH AH L OW")
        XCTAssertEqual(p.phonemize("你好hello").count, p.lastTones.count)
        XCTAssertEqual(p.lastTones, [3, 3, 2, 3, 0, 0, 0, 0])
    }

    func testUnknownCharactersAreReportedNotSilentlyDropped() {
        // A voice that reads 90% of a sentence sounds broken rather than absent,
        // so a caller has to be able to learn the dictionary is the problem.
        let p = LexiconPhonemizer.parse(lexicon)
        _ = p.phonemize("你好龘龘")
        XCTAssertEqual(p.lastUnknownWords, ["龘"])   // deduplicated
    }

    func testWhitespaceIsSkippedAndIsNotAnUnknownWord() {
        let p = LexiconPhonemizer.parse(lexicon)
        XCTAssertEqual(p.phonemize(" 你 好 "), ["n", "i", "h", "ao"])
        XCTAssertTrue(p.lastUnknownWords.isEmpty)
    }

    func testLookupFallsBackToLowercase() {
        let p = LexiconPhonemizer.parse("hello HH AH L OW")
        XCTAssertEqual(p.phonemize("HELLO"), ["HH", "AH", "L", "OW"])
    }

    func testMalformedLinesAreSkippedNotFatal() {
        // A word with no pronunciation is unusable, and a lexicon of 195,828
        // lines will have some.
        let p = LexiconPhonemizer.parse("\n  \nlonely\n好 h ao 3 3\n")
        XCTAssertEqual(p.entryCount, 1)
        XCTAssertEqual(p.phonemize("好"), ["h", "ao"])
    }

    func testEmptyTextGivesEmptyEverything() {
        let p = LexiconPhonemizer.parse(lexicon)
        XCTAssertTrue(p.phonemize("").isEmpty)
        XCTAssertTrue(p.lastTones.isEmpty)
        XCTAssertTrue(p.lastUnknownWords.isEmpty)
    }

    func testAMissingLexiconFileNamesThePath() {
        // "lexicon not found" without a path sends somebody hunting through
        // three candidate directories.
        let missing = NSTemporaryDirectory() + "no-such-lexicon-\(UUID().uuidString).txt"
        XCTAssertThrowsError(try LexiconPhonemizer.load(from: missing)) { e in
            XCTAssertEqual(e as? VoiceError, .fileNotFound(missing))
        }
    }

    func testLoadReadsARealFile() throws {
        let path = NSTemporaryDirectory() + "lex-\(UUID().uuidString).txt"
        try lexicon.write(toFile: path, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(atPath: path) }

        let p = try LexiconPhonemizer.load(from: path)
        XCTAssertEqual(p.entryCount, 5)
        XCTAssertEqual(p.phonemize("你好"), ["n", "i", "h", "ao"])
    }

    func testItIsAToneSource() {
        let p: any IToneSource = LexiconPhonemizer.parse(lexicon)
        _ = (p as! LexiconPhonemizer).phonemize("你好")
        XCTAssertEqual(p.lastTones, [3, 3, 2, 3])
    }

    // MARK: - espeak output cleaning

    #if os(macOS) || os(Linux) || os(Windows)

    func testLanguageSwitchMarkersAreStripped() {
        // "(en)hello(ko)" — left in, the LETTERS inside the brackets get mapped
        // and spoken aloud.
        XCTAssertEqual(EspeakPhonemizer.clean("(en)hɛ(ko)loʊ").joined(), "hɛloʊ")
    }

    func testNestedAndUnclosedMarkersDoNotEatTheRest() {
        XCTAssertEqual(EspeakPhonemizer.clean("a(b(c)d)e").joined(), "ae")
        XCTAssertEqual(EspeakPhonemizer.clean("a(unclosed").joined(), "a")
        XCTAssertEqual(EspeakPhonemizer.clean("a)stray").joined(), "astray")
    }

    func testNewlinesFoldToOneLine() {
        XCTAssertEqual(EspeakPhonemizer.clean("hə\r\nloʊ\n").joined(), "hə loʊ")
    }

    func testEmptyEspeakOutputIsNoPhonemesNotOneEmptyOne() {
        XCTAssertTrue(EspeakPhonemizer.clean("").isEmpty)
        XCTAssertTrue(EspeakPhonemizer.clean("(en)").isEmpty)
        XCTAssertTrue(EspeakPhonemizer.clean("   \n ").isEmpty)
    }

    func testBlankInputNeverLaunchesTheProcess() {
        // Cheap, but it is also the guard that stops a whitespace-only utterance
        // spawning a subprocess per frame.
        let p = EspeakPhonemizer()
        XCTAssertTrue(p.phonemize("").isEmpty)
        XCTAssertTrue(p.phonemize("   \t ").isEmpty)
    }

    #endif
}
