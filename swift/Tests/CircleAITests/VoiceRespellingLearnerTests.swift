// VoiceRespellingLearnerTests.swift
//
// The learn-by-listening half of PersonalRespellings: five hearings that agree,
// then a check that puts the new spelling into use and reads the NEXT hearing as
// the test of it.

import XCTest
@testable import CircleAI

final class VoiceRespellingLearnerTests: XCTestCase {

    private var dir: String!

    override func setUpWithError() throws {
        dir = NSTemporaryDirectory() + "respell-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(atPath: dir)
    }

    // MARK: - The five-hearings rule

    func testOneHearingChangesNothing() {
        // One hearing is a mishearing.
        let p = PersonalRespellings()
        XCTAssertFalse(p.observe(word: "Nandi", heard: "Nandhi"))
        XCTAssertNil(p.respell("Nandi"))
    }

    func testFiveHearingsThatAgreeAdoptTheSpelling() {
        let p = PersonalRespellings()
        for i in 1...5 {
            let changed = p.observe(word: "Nandi", heard: "Nandhi")
            XCTAssertEqual(changed, i == PersonalRespellings.adoptAfter,
                           "only the fifth hearing changes anything")
        }
        XCTAssertEqual(p.respell("Nandi"), "Nandhi")
        XCTAssertEqual(p.all.first?.state, .adopted)
    }

    func testFourHearingsAreNotEnough() {
        let p = PersonalRespellings()
        for _ in 1...4 { p.observe(word: "Nandi", heard: "Nandhi") }
        XCTAssertNil(p.respell("Nandi"))
        XCTAssertEqual(p.all.first?.state, .listening)
    }

    func testDisagreeingHearingsDoNotAccumulateTogether() {
        // Five hearings that each say something DIFFERENT is not five hearings
        // that agree.
        let p = PersonalRespellings()
        for h in ["Nandhi", "Nandee", "Nandi2", "Nandhee", "Nandey"] {
            p.observe(word: "Nandi", heard: h)
        }
        XCTAssertNil(p.respell("Nandi"))
    }

    // MARK: - The check

    func testTheSixthHearingConfirmsAnAdoptedSpelling() {
        // Agreeing five times only proves the ASR is consistent. Putting the
        // spelling INTO USE and hearing it back is the actual test.
        let p = PersonalRespellings()
        for _ in 1...5 { p.observe(word: "Nandi", heard: "Nandhi") }
        XCTAssertEqual(p.all.first?.state, .adopted)

        XCTAssertFalse(p.observe(word: "Nandi", heard: "Nandhi"),
                       "confirming changes state, not pronunciation")
        XCTAssertEqual(p.all.first?.state, .confirmed)
        XCTAssertEqual(p.respell("Nandi"), "Nandhi")
    }

    func testAFailedCheckUndoesTheAdoptionAndStrikesTheCandidate() {
        // We were wrong. The candidate that got us here is struck out so the
        // evidence cannot simply rebuild to the same wrong answer.
        let p = PersonalRespellings()
        for _ in 1...5 { p.observe(word: "Nandi", heard: "Nandhi") }

        p.observe(word: "Nandi", heard: "Nandee")

        XCTAssertNil(p.respell("Nandi"), "the adoption is undone")
        XCTAssertEqual(p.all.first?.state, .listening)
        XCTAssertNil(p.all.first?.candidates["Nandhi"], "the wrong candidate is struck out")
        XCTAssertEqual(p.all.first?.candidates["Nandee"], 1, "and this hearing still counts")
    }

    // MARK: - What is not evidence

    func testAHearingTooFarFromTheWordIsIgnoredEntirely() {
        // The speaker was saying something else. Checked BEFORE the entry is
        // created, so a rejected hearing leaves no trace — otherwise every
        // unrelated word in earshot litters a "words your CircleAI knows" view.
        let p = PersonalRespellings()
        XCTAssertFalse(p.observe(word: "Nandi", heard: "helicopter"))
        XCTAssertTrue(p.all.isEmpty, "no empty entry is left behind")
    }

    func testSayingItTheWayWeAlreadySayItIsNotALesson() {
        // Counting it would build a personal entry that overrides the shipped
        // spelling with an identical one, for no reason.
        let p = PersonalRespellings()
        XCTAssertFalse(p.observe(word: "internet", heard: "inthanethi",
                                 currentSpelling: "inthanethi"))
        // A row may exist - only the DISTANCE rejection short-circuits before
        // the entry is created - but nothing was learned from the hearing.
        XCTAssertNil(p.respell("internet"))
        XCTAssertTrue(p.all.first?.candidates.isEmpty ?? true)
    }

    func testAgreementIsCaseInsensitive() {
        let p = PersonalRespellings()
        XCTAssertFalse(p.observe(word: "internet", heard: "INTHANETHI",
                                 currentSpelling: "inthanethi"))
        XCTAssertNil(p.respell("internet"))
        XCTAssertTrue(p.all.first?.candidates.isEmpty ?? true)
    }

    func testBlankObservationsAreRefused() {
        let p = PersonalRespellings()
        XCTAssertFalse(p.observe(word: "  ", heard: "x"))
        XCTAssertFalse(p.observe(word: "x", heard: "  "))
        XCTAssertTrue(p.all.isEmpty)
    }

    // MARK: - Somebody typing it in themselves

    func testAnExplicitCorrectionSkipsTheEvidenceEntirely() {
        // A person stating how their own name is said is stronger than any
        // number of hearings; putting it through the five-hearing rule would
        // ignore them four times first.
        let p = PersonalRespellings()
        XCTAssertTrue(p.learn(word: "Nandi", respelling: "NAHN-dee"))
        XCTAssertEqual(p.respell("Nandi"), "NAHN-dee")
        XCTAssertEqual(p.all.first?.state, .confirmed)
    }

    // MARK: - Reading a transcript

    func testLearnFromPicksTheNearestTokenNotMerelyAClosdOne() {
        // A sentence can hold two similar words; picking the wrong one teaches
        // the wrong lesson.
        let p = PersonalRespellings()
        for _ in 1...5 {
            p.learnFrom(transcript: "i said inthanethi not inthaneth today",
                        currentSpellings: ["internet": "inthanetha"])
        }
        XCTAssertEqual(p.respell("internet"), "inthanethi")
    }

    func testLearnFromIgnoresATranscriptThatDoesNotContainTheWord() {
        let p = PersonalRespellings()
        let changed = p.learnFrom(transcript: "the weather is fine today",
                                  currentSpellings: ["internet": "inthanethi"])
        XCTAssertTrue(changed.isEmpty)
        XCTAssertTrue(p.all.isEmpty)
    }

    func testLearnFromReturnsOnlyTheWordsItActuallyChanged() {
        let p = PersonalRespellings()
        var changed: [String] = []
        for _ in 1...5 {
            changed = p.learnFrom(transcript: "nandhi",
                                  currentSpellings: ["Nandi": "nandi"])
        }
        XCTAssertEqual(changed, ["Nandi"])
    }

    func testLearnFromTakesTheTailOfAHyphenatedToken() {
        let p = PersonalRespellings()
        for _ in 1...5 {
            p.learnFrom(transcript: "e-nandhi", currentSpellings: ["Nandi": "nandi"])
        }
        XCTAssertEqual(p.respell("Nandi"), "nandhi")
    }

    func testLearnFromWithNothingToLearnFromIsEmpty() {
        let p = PersonalRespellings()
        XCTAssertTrue(p.learnFrom(transcript: nil, currentSpellings: ["a": "b"]).isEmpty)
        XCTAssertTrue(p.learnFrom(transcript: "  ", currentSpellings: ["a": "b"]).isEmpty)
        XCTAssertTrue(p.learnFrom(transcript: "hello", currentSpellings: [:]).isEmpty)
    }

    func testSingleCharacterTokensAreNotEvidence() {
        let p = PersonalRespellings()
        p.learnFrom(transcript: "a b c", currentSpellings: ["a": "a"])
        XCTAssertTrue(p.all.isEmpty)
    }

    // MARK: - Between sessions

    func testAYearOfLearningSurvivesARestart() {
        let path = (dir as NSString).appendingPathComponent("respell.json")

        let p = PersonalRespellings()
        for _ in 1...5 { p.observe(word: "Nandi", heard: "Nandhi") }
        p.observe(word: "Nandi", heard: "Nandhi")          // confirm it
        p.learn(word: "internet", respelling: "MY-WAY")
        XCTAssertNoThrow(try p.save(to: path))

        let back = PersonalRespellings.load(from: path)
        XCTAssertEqual(back.respell("Nandi"), "Nandhi")
        XCTAssertEqual(back.respell("internet"), "MY-WAY")
        XCTAssertEqual(back.all.count, 2)
        XCTAssertEqual(back.all.first { $0.word == "Nandi" }?.state, .confirmed)
    }

    func testPartialProgressSurvivesToo() {
        // Three of five hearings is real learning and must not restart at zero.
        let path = (dir as NSString).appendingPathComponent("partial.json")
        let p = PersonalRespellings()
        for _ in 1...3 { p.observe(word: "Nandi", heard: "Nandhi") }
        try? p.save(to: path)

        let back = PersonalRespellings.load(from: path)
        XCTAssertEqual(back.all.first?.candidates["Nandhi"], 3)
        for _ in 1...2 { back.observe(word: "Nandi", heard: "Nandhi") }
        XCTAssertEqual(back.respell("Nandi"), "Nandhi")
    }

    func testTheDirtyFlagOnlyClearsOnceTheBytesAreInPlace() {
        let path = (dir as NSString).appendingPathComponent("dirty.json")
        let p = PersonalRespellings()
        XCTAssertFalse(p.hasUnsavedChanges)

        p.observe(word: "Nandi", heard: "Nandhi")
        XCTAssertTrue(p.hasUnsavedChanges)

        try? p.save(to: path)
        XCTAssertFalse(p.hasUnsavedChanges)
    }

    func testAMissingFileStartsOverRatherThanRefusingToStart() {
        // Losing the table costs tuning; refusing to start costs the voice.
        let p = PersonalRespellings.load(
            from: (dir as NSString).appendingPathComponent("never-written.json"))
        XCTAssertTrue(p.all.isEmpty)
    }

    func testAnUnreadableFileStartsOverRatherThanThrowing() {
        let path = (dir as NSString).appendingPathComponent("junk.json")
        try? "{{{ not json".write(toFile: path, atomically: true, encoding: .utf8)
        XCTAssertTrue(PersonalRespellings.load(from: path).all.isEmpty)
    }

    func testSavingTwiceOverwritesRatherThanFailing() {
        let path = (dir as NSString).appendingPathComponent("twice.json")
        let p = PersonalRespellings()
        p.learn(word: "a", respelling: "b")
        XCTAssertNoThrow(try p.save(to: path))
        p.learn(word: "c", respelling: "d")
        XCTAssertNoThrow(try p.save(to: path))
        XCTAssertEqual(PersonalRespellings.load(from: path).all.count, 2)
    }

    // MARK: - Distance

    func testEditDistanceIsLevenshtein() {
        XCTAssertEqual(PersonalRespellings.editDistance("", ""), 0)
        XCTAssertEqual(PersonalRespellings.editDistance("abc", ""), 3)
        XCTAssertEqual(PersonalRespellings.editDistance("", "abc"), 3)
        XCTAssertEqual(PersonalRespellings.editDistance("kitten", "sitting"), 3)
        XCTAssertEqual(PersonalRespellings.editDistance("nandi", "nandhi"), 1)
    }

    func testIsSameWordUsesAFractionOfTheLongerWord() {
        // A fraction, not a fixed count: one letter wrong in a three-letter word
        // is a different word; one wrong in a twelve-letter word is a mishearing.
        XCTAssertTrue(PersonalRespellings.isSameWord("nandi", "nandhi"))
        XCTAssertTrue(PersonalRespellings.isSameWord("NANDI", "nandi"))
        XCTAssertFalse(PersonalRespellings.isSameWord("nandi", "helicopter"))
        // Two empty strings ARE the same word - equality is checked before the
        // length guard, and there is nothing for them to disagree about.
        XCTAssertTrue(PersonalRespellings.isSameWord("", ""))
        XCTAssertFalse(PersonalRespellings.isSameWord("", "nandi"))
    }

    // MARK: - Lookup and removal still work

    func testLookupIsCaseInsensitiveAndForgettingWorks() {
        let p = PersonalRespellings()
        p.learn(word: "Nandi", respelling: "nandhi")
        XCTAssertEqual(p.respell("NANDI"), "nandhi")
        XCTAssertTrue(p.forget("nandi"))
        XCTAssertFalse(p.forget("nandi"))
        XCTAssertNil(p.respell("Nandi"))
    }

    func testAllPreservesTheCaseTheWordWasFirstSeenIn() {
        let p = PersonalRespellings()
        p.learn(word: "Nandi", respelling: "nandhi")
        XCTAssertEqual(p.all.first?.word, "Nandi")
    }
}
