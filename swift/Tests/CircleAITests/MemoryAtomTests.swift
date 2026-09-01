import XCTest
@testable import CircleAI

// MARK: - The model

final class MemoryAtomTests: XCTestCase {

    func testAnAtomIsCurrentUntilSomethingSupersedesIt() {
        let a = MemoryAtom(text: "Use dotnet build, not adb push.")
        XCTAssertTrue(a.isCurrent)
        var superseded = a
        superseded.supersededBy = UUID()
        XCTAssertFalse(superseded.isCurrent)
    }

    func testOnlyAFactCanGoStale() {
        // A ruling that failed its check is not stale, it is wrong — and a
        // decision that failed is a road found closed, which is still worth
        // remembering. Staleness is about a fact no longer being true.
        XCTAssertTrue(MemoryAtom(kind: .fact, verifiedOk: false).isStale)
        XCTAssertFalse(MemoryAtom(kind: .ruling, verifiedOk: false).isStale)
        XCTAssertFalse(MemoryAtom(kind: .fact, verifiedOk: true).isStale)
        // Never checked is not the same as checked and wrong.
        XCTAssertFalse(MemoryAtom(kind: .fact).isStale)
    }

    func testAFailedOutcomeIsTheSignalRecallPushesToTheTop() {
        XCTAssertTrue(MemoryAtom(outcome: .failed).failed)
        XCTAssertFalse(MemoryAtom(outcome: .resolved).failed)
        XCTAssertFalse(MemoryAtom().failed)
    }

    func testTwoAtomsMadeWithoutAnIdAreNotTheSameAtom() {
        XCTAssertNotEqual(MemoryAtom(text: "x").id, MemoryAtom(text: "x").id)
    }

    func testTheWireNamesMatchWhatTheCSharpWrites() {
        // The log is SHARED with the C#. A different spelling here and the two
        // ends stop agreeing about what kind of thing an atom is.
        XCTAssertEqual("Decision", AtomKind.decision.wireName)
        XCTAssertEqual("Relationship", AtomKind.relationship.wireName)
        XCTAssertEqual(AtomKind.ruling, AtomKind.fromWire("Ruling"))
        XCTAssertEqual(AtomKind.ruling, AtomKind.fromWire("ruling"))
        XCTAssertNil(AtomKind.fromWire("Nonsense"))
        XCTAssertEqual(DecisionOutcome.failed, DecisionOutcome.fromWire("Failed"))
        XCTAssertNil(DecisionOutcome.fromWire(nil))
    }
}

final class SituationTests: XCTestCase {

    func testTheKeyIsVerbAndTargetLowercased() {
        XCTAssertEqual("deploy:android", Situation(verb: "Deploy", target: "Android").key)
        XCTAssertEqual("deploy", Situation(verb: " Deploy ").key)
        XCTAssertEqual("", Situation().key)
    }

    func testASlashDelimitedTargetIsWalkedUpFromSpecificToGeneral() {
        // A rule filed against the general case has to be found by the specific
        // one. Without this, a rule about deploying to Android is invisible the
        // moment somebody names the phone.
        XCTAssertEqual(
            ["deploy:android/p30/merlin", "deploy:android/p30", "deploy:android", "deploy"],
            Situation(verb: "deploy", target: "android/p30/merlin").keys)
    }

    func testTheKeysAreMostSpecificFirst() {
        let keys = Situation(verb: "deploy", target: "android/p30").keys
        XCTAssertEqual("deploy:android/p30", keys.first)
        XCTAssertEqual("deploy", keys.last)
    }

    func testATargetWithNoVerbHasNoKeysAtAll() {
        // A target on its own does not say what is about to happen, and a key
        // meaning "anything to do with android" matches too much to help.
        XCTAssertTrue(Situation(target: "android").keys.isEmpty)
    }

    func testALeadingSlashDoesNotProduceAnEmptyKey() {
        let keys = Situation(verb: "deploy", target: "/android").keys
        XCTAssertFalse(keys.contains("deploy:"), "produced an empty target key: \(keys)")
    }

    func testTheQueryIsEverythingKnownJoinedForKeywordSearch() {
        let s = Situation(verb: "deploy", target: "android", tool: "dotnet", text: "install fails")
        XCTAssertEqual("deploy android dotnet install fails", s.query)
        XCTAssertEqual("deploy android",
                       Situation(verb: "deploy", target: "android", tool: "  ").query)
    }

    func testASituationWithNothingToLookUpIsEmpty() {
        XCTAssertTrue(Situation().isEmpty)
        XCTAssertTrue(Situation(target: "android").isEmpty)
        XCTAssertFalse(Situation(text: "why did the install fail").isEmpty)
        XCTAssertFalse(Situation(verb: "deploy").isEmpty)
    }
}

final class RecallShapeTests: XCTestCase {

    func testAnEmptyResultSaysItFoundNothing() {
        XCTAssertFalse(RecallResult.empty.any)
        XCTAssertEqual(0, RecallResult.empty.considered)
    }

    func testTheDefaultBudgetIsFiveAtomsAndSixHundredCharacters() {
        // Not arbitrary: it is what fits in front of an action without pushing
        // the action itself out of view.
        XCTAssertEqual(5, RecallBudget.default.maxAtoms)
        XCTAssertEqual(600, RecallBudget.default.maxCharacters)
    }

    func testTheBarIsEightyPercentAndItIsInclusive() {
        func candidate(_ c: Double) -> AtomCandidate {
            AtomCandidate(atom: MemoryAtom(text: "x"), confidence: c, cue: "never", quote: "x")
        }
        XCTAssertTrue(candidate(0.80).certain)
        XCTAssertTrue(candidate(0.92).certain)
        XCTAssertFalse(candidate(0.79).certain)
    }
}

// MARK: - Extraction

final class CueExtractorTests: XCTestCase {

    private let extractor = CueExtractor()
    private let recorded = Date(timeIntervalSince1970: 1_782_896_400)

    private func episode(_ said: String, appContext: String? = nil) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(
            recordedAt: recorded, userText: said, assistantText: "", appContext: appContext)
    }

    private func extract(_ said: String, subject: String? = nil) -> [AtomCandidate] {
        extractor.extract(episode(said), subject: subject)
    }

    func testARuleStatedAtTheStartOfASentenceIsARuling() {
        let out = extract("Never use adb push to install, it keeps the old data.")
        XCTAssertEqual(1, out.count)
        XCTAssertEqual(.ruling, out[0].atom.kind)
        XCTAssertEqual("never", out[0].cue)
        XCTAssertTrue(out[0].certain)
    }

    func testTheSameWordInsideASentenceIsNotARuling() {
        // "never" at the start is a rule and nothing else. In the middle it is
        // usually a description, and filing it as a rule puts a stray
        // instruction in front of somebody at the worst moment.
        let out = extract("I have never seen that particular error message before today.")
        XCTAssertFalse(out.contains { $0.cue == "never" })
    }

    func testTheApostropheLessFormsAreCaughtToo() {
        // That is how people type when they are annoyed, which is exactly when
        // they are stating the rule that was just broken.
        XCTAssertEqual("dont", extract("Dont ever push to master without running the tests.")[0].cue)
        XCTAssertEqual("we dont", extract("Look, we dont use central APIs in this project at all.")[0].cue)
    }

    func testARoadFoundClosedIsRecordedAsAFailedDecision() {
        let out = extract("The incremental install did not work on that MIUI phone.")
        XCTAssertEqual(1, out.count)
        XCTAssertEqual(.decision, out[0].atom.kind)
        XCTAssertEqual(.failed, out[0].atom.outcome)
    }

    func testASettledDecisionIsRecordedAsResolved() {
        let out = extract("Lets use the sliding window guard for this rate limit.")
        XCTAssertEqual(.decision, out[0].atom.kind)
        XCTAssertEqual(.resolved, out[0].atom.outcome)
    }

    func testARulingCarriesNoOutcomeBecauseThereIsNothingToResolve() {
        XCTAssertNil(extract("Never use adb push to install, it keeps the old data.")[0].atom.outcome)
    }

    func testBeingToldAgainScoresHighest() {
        // The single highest-value thing in a transcript: whatever follows has
        // already cost somebody twice.
        let out = extract("I told you already, the P30 is the only benchmark that counts.")
        XCTAssertEqual(.ruling, out[0].atom.kind)
        XCTAssertGreaterThanOrEqual(out[0].confidence, 0.90)
    }

    func testTheMostSpecificCueWinsWhenTwoAreInOneSentence() {
        // Filing it twice makes one complaint look like a pattern.
        let out = extract("I told you that you keep forgetting to uninstall before deploying.")
        XCTAssertEqual(1, out.count)
        XCTAssertEqual("i told you", out[0].cue)
    }

    func testTheSentenceIsKeptWholeRatherThanParaphrased() {
        // Paraphrasing is where extraction starts inventing, and an invented
        // memory comes back with the same confidence as a true one.
        let said = "Never use adb push to install, it keeps the old data"
        XCTAssertEqual(said, extract(said + ".")[0].atom.text)
        XCTAssertEqual(said, extract(said + ".")[0].quote)
    }

    func testASentenceTooShortToMeanAnythingIsSkipped() {
        // "never mind" and "stop it" carry a cue and no content, and filing them
        // fills the memory with things that match everything and mean nothing.
        XCTAssertTrue(extract("Never mind.").isEmpty)
        XCTAssertTrue(extract("Stop it.").isEmpty)
        XCTAssertTrue(extract("I want that.").isEmpty)
    }

    func testAParagraphThatMerelyContainsTheWordIsSkipped() {
        let long = "Never " + String(repeating: "and so on ", count: 40)
        XCTAssertGreaterThan(long.count, CueExtractor.longestWorthKeeping)
        XCTAssertTrue(extract(long).isEmpty)
    }

    func testOnlyWhatThePersonSaidIsRead() {
        // Extracting from the assistant turn lets the thing that was just
        // corrected file its own version of events alongside the correction.
        let e = EpisodicMemoryEntry(
            userText: "",
            assistantText: "Never use adb push to install, it keeps the old data.")
        XCTAssertTrue(extractor.extract(e).isEmpty)
    }

    func testTheSameSentenceTwiceInOneTurnIsFiledOnce() {
        let said = "Never use adb push to install, it keeps the old data."
        XCTAssertEqual(1, extract(said + " " + said).count)
    }

    func testTheSubjectIsTakenNotGuessed() {
        // A wrong subject key makes an atom findable in the wrong situation and
        // invisible in the right one, which is worse than no key at all.
        let said = "Never use adb push to install, it keeps data."
        XCTAssertEqual("deploy", extract(said, subject: "deploy")[0].atom.subject)
        XCTAssertEqual("android",
                       extractor.extract(episode(said, appContext: "android"))[0].atom.subject)
        XCTAssertNil(extract(said)[0].atom.subject)
    }

    func testTheEpisodeTimeIsCarriedOntoTheAtom() {
        // When it was SAID, not when it was filed. A batch import of an old
        // transcript must not look like today.
        XCTAssertEqual(recorded, extract("Never use adb push, it keeps data.")[0].atom.recordedAtUtc)
    }

    func testAFullStopInsideAVersionNumberDoesNotSplitTheSentence() {
        // Only whitespace or the end of the text ends a sentence, or every file
        // name and version cuts a rule in half.
        let out = extract("Never build against net9.0 for this, always use net10.0 here.")
        XCTAssertEqual(1, out.count)
        XCTAssertTrue(out[0].atom.text.contains("net10.0"))
    }

    func testACueMustSitAtAWordBoundary() {
        // "use " inside "abuse the" would file a decision nobody made.
        XCTAssertEqual(-1, CueExtractor.position("dont abuse the thing", "use "))
        XCTAssertEqual(5, CueExtractor.position("dont use the thing", "use "))
    }

    func testNormalisingCollapsesWhitespaceCaseAndTrailingPunctuation() {
        XCTAssertEqual(
            CueExtractor.normalise("Never use ADB push!"),
            CueExtractor.normalise("  never   use   adb   push  "))
        XCTAssertEqual("never use adb push", CueExtractor.normalise("Never use ADB push."))
    }

    func testSentenceSplittingHandlesNewlinesBulletsAndQuestionMarks() {
        XCTAssertEqual(
            ["First one here", "Second one there", "Third one"],
            CueExtractor.sentences("- First one here\n* Second one there? Third one!"))
        XCTAssertTrue(CueExtractor.sentences("   ").isEmpty)
    }
}
