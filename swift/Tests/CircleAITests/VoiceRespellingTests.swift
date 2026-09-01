import XCTest
@testable import CircleAI

/// Making an English word sayable by an isiZulu voice.
final class VoiceRespellingTests: XCTestCase {

    // MARK: - The attested table

    func testAKnownLoanwordIsRespelt() {
        XCTAssertEqual(LoanwordRespeller.respell("internet", hostLanguage: "zu"), "inthanethi")
        XCTAssertEqual(LoanwordRespeller.respell("WhatsApp", hostLanguage: "zu"), "wotsapha")
        XCTAssertEqual(LoanwordRespeller.respell("PHONE", hostLanguage: "zu"), "foni")
    }

    // An English voice saying an English word is already correct.
    func testNothingIsRespeltForALanguageThatDoesNotNeedIt() {
        XCTAssertNil(LoanwordRespeller.respell("internet", hostLanguage: "en"))
        XCTAssertNil(LoanwordRespeller.respell("internet", hostLanguage: "fr"))
    }

    // Nguni and Sotho-Tswana share the sound system this targets.
    func testTheHostLanguageSetCoversBothGroups() {
        for tag in ["zu", "zul", "xh", "xho", "ss", "ssw", "nr", "nbl"] {
            XCTAssertTrue(LoanwordRespeller.isNguniOrSotho(tag), tag)
        }
        for tag in ["st", "sot", "nso", "tn", "tsn"] {
            XCTAssertTrue(LoanwordRespeller.isNguniOrSotho(tag), tag)
        }
        for tag in ["en", "af", "sw", "yo", ""] {
            XCTAssertFalse(LoanwordRespeller.isNguniOrSotho(tag), tag)
        }
    }

    // An attested form can ship silently; a proposed one is a suggestion
    // somebody may want to correct.
    func testAttestedAndProposedAreDistinguished() {
        XCTAssertEqual(LoanwordRespeller.source(of: "internet"), .attested)
        XCTAssertEqual(LoanwordRespeller.source(of: "doctor"), .attested)
        XCTAssertEqual(LoanwordRespeller.source(of: "WhatsApp"), .proposed)
        XCTAssertEqual(LoanwordRespeller.source(of: "CircleAI"), .proposed)
        XCTAssertNil(LoanwordRespeller.source(of: "aardvark"))
    }

    func testTheTableIsEmptyForAnUnsupportedLanguage() {
        XCTAssertTrue(LoanwordRespeller.table(hostLanguage: "en").isEmpty)
        XCTAssertFalse(LoanwordRespeller.table(hostLanguage: "zu").isEmpty)
    }

    // MARK: - Vowel epenthesis

    // Nguni syllables are OPEN, so a word-final consonant gets a vowel after
    // it, and the n/k cluster gets one between them: b a n [e] kh [e].
    func testAWordFinalConsonantGetsAVowel() {
        XCTAssertEqual(NguniRespeller.fromIpa("bank"), "banekhe")
    }

    // ...and a consonant cluster gets one pushed between its parts.
    func testAConsonantClusterIsBrokenUp() {
        let out = NguniRespeller.fromIpa("st")
        XCTAssertEqual(out, "sethe")
    }

    func testAVowelBetweenConsonantsIsLeftAlone() {
        XCTAssertEqual(NguniRespeller.fromIpa("tada"), "thada")
    }

    // Longest match first, so a diphthong is one unit rather than two vowels.
    func testADiphthongIsOneUnit() {
        XCTAssertEqual(NguniRespeller.fromIpa("a\u{026A}"), "ayi")
        XCTAssertEqual(NguniRespeller.fromIpa("e\u{026A}"), "eyi")
        XCTAssertEqual(NguniRespeller.fromIpa("a\u{028A}"), "awu")
    }

    // The tie bar is SKIPPED, not joined, so t and sh stay two consonants
    // and epenthesis puts a vowel between them. This matches the C#.

    // ...and so is an affricate.
    func testAnAffricateIsOneUnit() {
        XCTAssertEqual(NguniRespeller.fromIpa("t\u{0283}"), "tshe")
        XCTAssertEqual(NguniRespeller.fromIpa("d\u{0292}"), "je")
    }

    func testALengthMarkSelectsTheLongVowel() {
        XCTAssertEqual(NguniRespeller.fromIpa("i\u{02D0}"), "i")
        XCTAssertEqual(NguniRespeller.fromIpa("\u{0251}\u{02D0}"), "a")
    }

    // Stress marks and tie bars carry no segment.
    func testStressMarksAreIgnored() {
        XCTAssertEqual(NguniRespeller.fromIpa("\u{02C8}ta"), NguniRespeller.fromIpa("ta"))
        XCTAssertEqual(NguniRespeller.fromIpa("t\u{0361}\u{0283}"), "theshe")
    }

    // A symbol this does not model contributes nothing rather than breaking
    // the whole word.
    func testAnUnknownSymbolIsSkippedNotFatal() {
        XCTAssertEqual(NguniRespeller.fromIpa("t\u{01C0}a"), "tha")
    }

    func testEmptyIpaGivesEmptyOutput() {
        XCTAssertEqual(NguniRespeller.fromIpa(""), "")
        XCTAssertEqual(NguniRespeller.fromIpa(nil), "")
        XCTAssertEqual(NguniRespeller.fromIpa("   "), "")
    }

    // MARK: - Personal corrections

    func testWhatSomebodyTypedIsRemembered() {
        let p = PersonalRespellings()
        XCTAssertTrue(p.learn(word: "Nandi", respelling: "nandi"))
        XCTAssertEqual(p.respell("nandi"), "nandi")
        XCTAssertEqual(p.respell("NANDI"), "nandi", "lookup is case-insensitive")
        XCTAssertNil(p.respell("thabo"))
    }

    func testABlankCorrectionIsRefused() {
        let p = PersonalRespellings()
        XCTAssertFalse(p.learn(word: "  ", respelling: "x"))
        XCTAssertFalse(p.learn(word: "x", respelling: "  "))
        XCTAssertTrue(p.all.isEmpty)
    }

    func testACorrectionCanBeForgotten() {
        let p = PersonalRespellings()
        p.learn(word: "x", respelling: "y")
        XCTAssertTrue(p.forget("X"))
        XCTAssertFalse(p.forget("X"))
        XCTAssertNil(p.respell("x"))
    }

    // MARK: - Precedence

    private struct FixedPhonemizer: IPhonemizer {
        let ipa: String
        func phonemize(_ text: String) -> [String] { ipa.map(String.init) }
    }

    // They know their own words; this code does not.
    func testAPersonalCorrectionBeatsTheShippedTable() {
        let p = PersonalRespellings()
        p.learn(word: "internet", respelling: "MY-WAY")
        let r = Respeller(hostLanguage: "zu", personal: p)
        XCTAssertEqual(r.respelling(for: "internet"), "MY-WAY")
    }

    func testTheShippedTableBeatsADerivedGuess() {
        let r = Respeller(hostLanguage: "zu",
                          englishPhonemizer: FixedPhonemizer(ipa: "zzz"))
        XCTAssertEqual(r.respelling(for: "internet"), "inthanethi")
    }

    func testAnUnknownWordIsDerivedFromItsEnglishSound() {
        let r = Respeller(hostLanguage: "zu",
                          englishPhonemizer: FixedPhonemizer(ipa: "bank"))
        XCTAssertEqual(r.respelling(for: "spanner"), "banekhe",
                       "a word not in the table falls through to derivation")
    }

    // Returning nil lets the caller spell the word out rather than
    // mispronouncing it confidently.
    func testNoSourceMeansNoRespelling() {
        let bare = Respeller(hostLanguage: "zu")
        XCTAssertNil(bare.respelling(for: "aardvark"), "no phonemizer wired")

        let englishHost = Respeller(hostLanguage: "en",
                                    englishPhonemizer: FixedPhonemizer(ipa: "bank"))
        XCTAssertNil(englishHost.respelling(for: "bank"), "English needs no respelling")

        XCTAssertNil(bare.respelling(for: "   "))
    }

    // MARK: - Tracing

    func testTheTraceSinkNeverBreaksTheCaller() {
        VoiceTrace.setSink { _ in }
        XCTAssertTrue(VoiceTrace.enabled)
        VoiceTrace.write("hello")
        VoiceTrace.setSink(nil)
        XCTAssertFalse(VoiceTrace.enabled)
        VoiceTrace.write("goes nowhere")
    }

    func testPhonemeSplittingKeepsCombiningMarksAttached() {
        XCTAssertEqual(PiperPhonemes.split("abc"), ["a", "b", "c"])
        XCTAssertEqual(PiperPhonemes.split(""), [])
        // A length mark rides with its vowel rather than becoming a phoneme.
        XCTAssertEqual(PassthroughPhonemizer().phonemize("a b"), ["a", "b"])
    }
}
