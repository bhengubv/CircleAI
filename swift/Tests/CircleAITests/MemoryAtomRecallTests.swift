import XCTest
@testable import CircleAI

final class AtomRecallTests: XCTestCase {

    private let now = Date()
    private let situation = Situation(verb: "deploy", target: "android/p30")

    private func atom(_ text: String,
                      kind: AtomKind = .decision,
                      subject: String? = nil,
                      corrections: Int = 0,
                      outcome: DecisionOutcome? = nil,
                      verifiedOk: Bool? = nil,
                      recorded: Date? = nil) -> MemoryAtom {
        MemoryAtom(kind: kind, text: text, subject: subject,
                   recordedAtUtc: recorded ?? now, corrections: corrections,
                   outcome: outcome, verifiedOk: verifiedOk)
    }

    func testAnEmptySituationRecallsNothingWithoutTouchingTheStore() async throws {
        let store = FakeAtomStore()
        let out = try await Recall(atoms: store).recall(Situation())
        XCTAssertEqual(RecallResult.empty, out)
        XCTAssertEqual(-1, store.lastMatchLimit, "the store was queried for an empty situation")
    }

    func testItAsksForMoreCandidatesThanTheBudgetSoRankingHasAChoice() async throws {
        // The store's ordering is by subject match, not by what matters here.
        // Ask for exactly the budget and the ranking is decorative.
        let store = FakeAtomStore([atom("a")])
        _ = try await Recall(atoms: store).recall(situation, budget: RecallBudget(maxAtoms: 5))
        XCTAssertGreaterThanOrEqual(store.lastMatchLimit, 20)
    }

    func testARulingOutranksAPreferenceWhenBothMatch() async throws {
        let store = FakeAtomStore([
            atom("I like the shorter form of that command", kind: .preference),
            atom("Never use adb push to install", kind: .ruling),
        ])
        let out = try await Recall(atoms: store).recall(situation, budget: RecallBudget(maxAtoms: 2))
        XCTAssertEqual(.ruling, out.atoms.first?.kind)
    }

    func testARoadAlreadyFoundClosedGoesNearTheTop() async throws {
        // Knowing what failed is worth as much as knowing what worked, and it
        // arrives too late by default: the whole cost of a repeated mistake is
        // paid before anybody remembers making it the first time.
        let store = FakeAtomStore([
            atom("We used the incremental install", outcome: .resolved),
            atom("The incremental install did not work", outcome: .failed),
        ])
        let out = try await Recall(atoms: store).recall(situation, budget: RecallBudget(maxAtoms: 2))
        XCTAssertTrue(out.atoms.first?.failed ?? false, "the failure was not surfaced first")
    }

    func testARepeatedlyCorrectedAtomOutranksAFreshOneButTheBonusIsCapped() async throws {
        let store = FakeAtomStore([
            atom("Something said once"),
            atom("Something said four times", corrections: 4),
        ])
        let out = try await Recall(atoms: store).recall(situation, budget: RecallBudget(maxAtoms: 2))
        XCTAssertEqual("Something said four times", out.atoms.first?.text)

        let r = Recall(atoms: FakeAtomStore())
        XCTAssertEqual(r.score(atom("x", corrections: 4), situation: situation, now: now),
                       r.score(atom("x", corrections: 40), situation: situation, now: now))
    }

    func testAnExactSubjectMatchOutranksABroaderOne() async throws {
        let store = FakeAtomStore([
            atom("General deploy advice", subject: "deploy"),
            atom("Specific P30 advice", subject: "deploy:android/p30"),
        ])
        let out = try await Recall(atoms: store).recall(situation, budget: RecallBudget(maxAtoms: 2))
        XCTAssertEqual("Specific P30 advice", out.atoms.first?.text)
    }

    func testAStaleFactIsStillReturnedCarryingTheDoubtButRanksBelowASoundOne() async throws {
        // The kinds are weights, not gates. The agent is being told, not
        // handcuffed — and a fact that failed its check is still evidence.
        let store = FakeAtomStore([atom("The port is 8080", kind: .fact, verifiedOk: false)])
        let out = try await Recall(atoms: store).recall(situation)
        XCTAssertEqual(1, out.atoms.count)
        XCTAssertTrue(out.atoms[0].isStale)

        let r = Recall(atoms: FakeAtomStore())
        XCTAssertLessThan(
            r.score(atom("x", kind: .fact, verifiedOk: false), situation: situation, now: now),
            r.score(atom("x", kind: .fact, verifiedOk: true), situation: situation, now: now))
    }

    func testToneIsLoadedByKindRatherThanBySituation() async throws {
        // "Answer first, explain after" describes the PERSON, not the subject.
        // Filed under its own topic it simply never matched, so the manner
        // vanished the moment the work got specific — which is when it matters.
        let store = FakeAtomStore([
            atom("Answer first, explain after", kind: .relationship, subject: "something-else"),
            atom("Never use adb push", kind: .ruling),
        ])
        let out = try await Recall(atoms: store).recall(situation)
        XCTAssertEqual(1, out.tone.count)
        XCTAssertEqual("Answer first, explain after", out.tone.first?.text)
        XCTAssertTrue(out.atoms.allSatisfy { $0.kind != .relationship })
    }

    func testToneAloneStillComesBackWhenNothingElseMatched() async throws {
        let store = FakeAtomStore([atom("Answer first", kind: .relationship)])
        let out = try await Recall(atoms: store).recall(situation)
        XCTAssertFalse(out.any)
        XCTAssertEqual(1, out.tone.count)
        XCTAssertEqual(0, out.considered)
    }

    func testTheAtomBudgetIsRespected() async throws {
        let store = FakeAtomStore((1...20).map { atom("atom \($0)") })
        let out = try await Recall(atoms: store).recall(situation, budget: RecallBudget(maxAtoms: 3))
        XCTAssertEqual(3, out.atoms.count)
        XCTAssertEqual(20, out.considered)
    }

    func testASingleLongAtomDoesNotStarveThreeShortOnes() async throws {
        // Skipped, not stopped at. The next one may well fit, and three short
        // atoms together are usually worth more than one long one.
        let store = FakeAtomStore([
            atom("s1", kind: .ruling),
            atom(String(repeating: "x", count: 500), kind: .ruling),
            atom("s2", kind: .ruling),
            atom("s3", kind: .ruling),
        ])
        let out = try await Recall(atoms: store)
            .recall(situation, budget: RecallBudget(maxAtoms: 5, maxCharacters: 60))
        XCTAssertGreaterThanOrEqual(out.atoms.count, 3)
        XCTAssertFalse(out.atoms.contains { $0.text.count == 500 })
    }

    func testOneAtomLongerThanTheWholeBudgetIsStillReturned() async throws {
        // Better a long answer than an empty one.
        let store = FakeAtomStore([atom(String(repeating: "x", count: 5000), kind: .ruling)])
        let out = try await Recall(atoms: store)
            .recall(situation, budget: RecallBudget(maxAtoms: 5, maxCharacters: 60))
        XCTAssertEqual(1, out.atoms.count)
    }

    func testWhatHasFadedIsNotOfferedButIsNotGone() async throws {
        let old = atom("An old decision", recorded: now.addingTimeInterval(-3650 * 86_400))
        let store = FakeAtomStore([old])
        let out = try await Recall(atoms: store, wear: MemoryWear()).recall(situation)
        XCTAssertTrue(out.atoms.isEmpty, "a ten-year-old decision was still volunteered")
        // Still there by id: fading is what recall offers, not what the store holds.
        let still = try await store.get(old.id)
        XCTAssertNotNil(still)
    }

    func testOnlyWhatWasHandedBackCountsAsRemembered() async throws {
        // An atom that matched and lost on ranking was not remembered, it was
        // passed over.
        let winner = atom("Never use adb push", kind: .ruling)
        let loser = atom("I like the short form", kind: .preference)
        let wear = MemoryWear()
        _ = try await Recall(atoms: FakeAtomStore([winner, loser]), wear: wear)
            .recall(situation, budget: RecallBudget(maxAtoms: 1))
        XCTAssertNotNil(wear.forAtom(winner.id))
        XCTAssertNil(wear.forAtom(loser.id), "an atom that lost on ranking was marked as retrieved")
    }

    func testRecallWorksWithNoWearAtAll() async throws {
        let store = FakeAtomStore([atom("Never use adb push", kind: .ruling)])
        let out = try await Recall(atoms: store).recall(situation)
        XCTAssertEqual(1, out.atoms.count)
    }
}

final class MemoryServiceTests: XCTestCase {

    private func tempDir() -> String {
        let d = NSTemporaryDirectory() + "memservice-" + UUID().uuidString
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }

    private func service(_ dir: String? = nil,
                         store: FakeAtomStore = FakeAtomStore()) throws -> MemoryService {
        try MemoryService(folderPath: dir ?? tempDir(), store: store, machine: "linux-build")
    }

    func testRememberingGoesStraightThroughToTheLog() async throws {
        // Nothing is queued, so nothing is lost when the app goes away — which
        // on a phone is the ordinary case rather than the exception.
        let dir = tempDir()
        let s = try service(dir)
        try await s.remember(MemoryAtom(kind: .ruling, text: "Never use adb push to install"))
        let n = try await s.count()
        XCTAssertEqual(1, n)
        XCTAssertEqual(1, s.log.readAll().count)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: (dir as NSString).appendingPathComponent("atoms.linux-build.jsonl")))
    }

    func testRecallFindsWhatWasRemembered() async throws {
        let s = try service()
        try await s.remember(MemoryAtom(
            kind: .ruling, text: "Never use adb push to install", subject: "deploy"))
        let out = try await s.recall(Situation(verb: "deploy", target: "android"))
        XCTAssertEqual(1, out.atoms.count)
        XCTAssertTrue(out.atoms[0].text.contains("adb push"))
    }

    func testLearningFilesWhatWasSaidAndOnlyWhatIsCertain() async throws {
        let s = try service()
        let report = try await s.learn(
            "Never use adb push to install, it keeps the old data. "
            + "I like the shorter form of that command better.",
            subject: "deploy")
        XCTAssertEqual(2, report.considered)
        XCTAssertEqual(1, report.recorded.count)
        XCTAssertEqual(1, report.offered.count)
        let n = try await s.count()
        XCTAssertEqual(1, n)
    }

    func testLearningTheSameSentenceTwiceKeepsOneAtom() async throws {
        let s = try service()
        let said = "Never use adb push to install, it keeps the old data."
        _ = try await s.learn(said, subject: "deploy")
        _ = try await s.learn(said, subject: "deploy")
        let n = try await s.count()
        XCTAssertEqual(1, n)
    }

    func testLearningNothingIsAnEmptyReport() async throws {
        let s = try service()
        let report = try await s.learn("   ")
        XCTAssertEqual(0, report.considered)
        let n = try await s.count()
        XCTAssertEqual(0, n)
    }

    func testTheGitignoreIsWrittenOnConstruction() throws {
        let dir = tempDir()
        _ = try service(dir)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: (dir as NSString).appendingPathComponent(".gitignore")))
    }

    func testARebuildRestoresTheIndexFromTheLogsAlone() async throws {
        let dir = tempDir()
        let first = try service(dir)
        try await first.remember(MemoryAtom(
            kind: .ruling, text: "Never use adb push", subject: "deploy"))

        // A brand new index over the same folder: cold start, or a corrupt file.
        let second = try service(dir)
        var n = try await second.count()
        XCTAssertEqual(0, n)
        let report = try await second.rebuild()
        XCTAssertEqual(1, report.records)
        n = try await second.count()
        XCTAssertEqual(1, n)
    }
}

final class ModuleMemoryTests: XCTestCase {

    private func service() throws -> MemoryService {
        let d = NSTemporaryDirectory() + "modmem-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return try MemoryService(folderPath: d, store: FakeAtomStore(), machine: "linux-build")
    }

    func testAModuleNameIsNormalisedAndRequired() throws {
        let s = try service()
        XCTAssertEqual("interpret", try ModuleMemory(memory: s, module: "  Interpret  ").module)
        XCTAssertThrowsError(try ModuleMemory(memory: s, module: "   "))
    }

    func testAModuleOwnsWhatItRemembersAndTheSubjectIsPrefixedNotReplaced() async throws {
        // "interpret:languages" still rolls up to "interpret", so a module's
        // whole memory can be read at once.
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "interpret")
        _ = try await m.remember(MemoryAtom(kind: .ruling, text: "Never keep what passes through me"))
        var all = try await s.all()
        XCTAssertEqual("interpret", all.first?.subject)

        let s2 = try service()
        let m2 = try ModuleMemory(memory: s2, module: "interpret")
        _ = try await m2.remember(MemoryAtom(kind: .ruling, text: "x", subject: "languages"))
        all = try await s2.all()
        XCTAssertEqual("interpret:languages", all.first?.subject)
    }

    func testAnAlreadyOwnedSubjectIsNotPrefixedTwice() async throws {
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "interpret")
        _ = try await m.remember(MemoryAtom(kind: .ruling, text: "x", subject: "interpret:languages"))
        let all = try await s.all()
        XCTAssertEqual("interpret:languages", all.first?.subject)
    }

    func testARulesOnlyModuleStillRemembersItsOwnProhibition() async throws {
        // That is the part that is easy to get backwards. A module with no
        // continuity cannot remember that it must not keep anything.
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "interpret", retention: .rulesOnly)
        let kept = try await m.remember(
            MemoryAtom(kind: .ruling, text: "Never keep what passes through me"))
        XCTAssertTrue(kept)
        let n = try await s.count()
        XCTAssertEqual(1, n)
    }

    func testButARulesOnlyModuleKeepsNoneOfTheWords() async throws {
        // A live interpreter must not retain what passes through it: those are
        // two other people's words.
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "interpret", retention: .rulesOnly)
        let a = try await m.remember(MemoryAtom(kind: .decision, text: "what one of them said"))
        let b = try await m.remember(MemoryAtom(kind: .fact, text: "a fact about one of them"))
        XCTAssertFalse(a)
        XCTAssertFalse(b)
        let n = try await s.count()
        XCTAssertEqual(0, n)
    }

    func testARulesOnlyModuleDoesNotEvenExtractFromWhatItHeard() async throws {
        // The words never reach the learner at all.
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "interpret", retention: .rulesOnly)
        let report = try await m.heard("Never use adb push to install, it keeps the old data.")
        XCTAssertEqual(0, report.considered)
        let n = try await s.count()
        XCTAssertEqual(0, n)
    }

    func testANormalModuleLearnsFromWhatItHeardUnderItsOwnSubject() async throws {
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "deploy")
        let report = try await m.heard("Never use adb push to install, it keeps the old data.")
        XCTAssertEqual(1, report.recorded.count)
        let all = try await s.all()
        XCTAssertEqual("deploy", all.first?.subject)
    }

    func testPreferencesAndRelationshipsAreRulesForRetentionPurposes() async throws {
        // How somebody wants to be worked with is a standing instruction, not a
        // record of what they said.
        let s = try service()
        let m = try ModuleMemory(memory: s, module: "interpret", retention: .rulesOnly)
        let a = try await m.remember(MemoryAtom(kind: .preference, text: "speak slowly"))
        let b = try await m.remember(MemoryAtom(kind: .relationship, text: "answer first"))
        XCTAssertTrue(a)
        XCTAssertTrue(b)
        let n = try await s.count()
        XCTAssertEqual(2, n)
    }
}

final class HookPayloadTests: XCTestCase {

    func testAnEnvelopeGivesUpItsPrompt() {
        XCTAssertEqual("deploy the app",
                       HookPayload.promptFrom("{\"prompt\":\"deploy the app\"}"))
        XCTAssertEqual("x",
                       HookPayload.promptFrom("  \n {\"session\":\"1\",\"prompt\":\"x\"} "))
    }

    func testSomethingThatIsNotAnEnvelopeIsTakenAtFaceValue() {
        // A person piping their own notes in is the other half of what this reads.
        XCTAssertEqual("just some words", HookPayload.promptFrom("just some words"))
        XCTAssertEqual("a line about {braces}", HookPayload.promptFrom("a line about {braces}"))
    }

    func testAnEnvelopeWithNoMessageInItIsNothing() {
        // Reading the envelope as if it were the message would file field names
        // as things somebody said.
        XCTAssertEqual("", HookPayload.promptFrom("{\"session\":\"1\",\"cwd\":\"/tmp\"}"))
        XCTAssertEqual("", HookPayload.promptFrom("{}"))
    }

    func testANonStringPromptIsNothingRatherThanItsRendering() {
        XCTAssertEqual("", HookPayload.promptFrom("{\"prompt\":42}"))
        XCTAssertEqual("", HookPayload.promptFrom("{\"prompt\":{\"nested\":\"x\"}}"))
    }

    func testSomethingThatStartsWithABraceAndIsNotJsonIsProse() {
        let prose = "{ this is not json, it is a note somebody typed"
        XCTAssertEqual(prose, HookPayload.promptFrom(prose))
    }

    func testNothingInIsNothingOut() {
        XCTAssertEqual("", HookPayload.promptFrom(nil))
        XCTAssertEqual("", HookPayload.promptFrom(""))
        XCTAssertEqual("", HookPayload.promptFrom("   "))
    }
}
