import XCTest
@testable import CircleAI

final class ForgettingTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func days(_ n: Double) -> TimeInterval { n * 86_400 }

    private func atom(kind: AtomKind = .decision, corrections: Int = 0,
                      recorded: Date? = nil) -> MemoryAtom {
        MemoryAtom(kind: kind, recordedAtUtc: recorded ?? now, corrections: corrections)
    }

    func testRetrievabilityStartsAtOneAndDecaysTowardsZero() {
        XCTAssertEqual(1.0, Forgetting.retrievability(stabilityDays: 90, elapsed: 0), accuracy: 1e-12)
        XCTAssertLessThan(Forgetting.retrievability(stabilityDays: 90, elapsed: days(90)), 0.4)
        XCTAssertLessThan(Forgetting.retrievability(stabilityDays: 90, elapsed: days(1000)), 0.001)
    }

    func testMoreStabilityMeansSlowerDecay() {
        let fresh = Forgetting.retrievability(stabilityDays: 90, elapsed: days(180))
        let deep = Forgetting.retrievability(stabilityDays: 900, elapsed: days(180))
        XCTAssertGreaterThan(deep, fresh, "a more deeply learned atom faded faster")
    }

    func testZeroStabilityIsAlreadyGoneRatherThanDividingByZero() {
        XCTAssertEqual(0, Forgetting.retrievability(stabilityDays: 0, elapsed: 0))
        XCTAssertEqual(0, Forgetting.retrievability(stabilityDays: -5, elapsed: days(1)))
    }

    func testTimeNeverRunsBackwardsForTheCurve() {
        // A clock that went backwards must not make something MORE retrievable
        // than the moment it was learned.
        XCTAssertEqual(1.0, Forgetting.retrievability(stabilityDays: 90, elapsed: days(-30)), accuracy: 1e-12)
    }

    func testRetrievingSomethingNearlyForgottenStrengthensItFarMore() {
        // The spacing effect, and the reason there are two strengths instead of
        // one score. It falls out of the arithmetic rather than being bolted on.
        let fresh = Forgetting.strengthened(stabilityDays: 90, retrievability: 1.0)
        let faded = Forgetting.strengthened(stabilityDays: 90, retrievability: 0.05)
        XCTAssertGreaterThan(faded, fresh * 2)
        XCTAssertEqual(90, fresh, accuracy: 1e-9, "a retrieval of something perfectly fresh gained something")
    }

    func testStabilityOnlyEverGrows() {
        XCTAssertGreaterThanOrEqual(
            Forgetting.strengthened(stabilityDays: 10, retrievability: 1.0),
            Forgetting.initialStabilityDays)
        XCTAssertGreaterThanOrEqual(
            Forgetting.strengthened(stabilityDays: 500, retrievability: 0.5), 500)
    }

    func testAnOutOfRangeRetrievabilityIsClampedRatherThanInverted() {
        // A negative would produce a gain above the ceiling, and a value above
        // one would produce a LOSS, which stability is not allowed to have.
        XCTAssertGreaterThanOrEqual(Forgetting.strengthened(stabilityDays: 90, retrievability: 2), 90)
        XCTAssertLessThanOrEqual(
            Forgetting.strengthened(stabilityDays: 90, retrievability: -1),
            90 * (1 + Forgetting.spacingGain))
    }

    func testACorrectedAtomStartsOutMoreDeeplyLearnedAndTheBonusIsCapped() {
        XCTAssertGreaterThan(
            Forgetting.initialStability(atom(corrections: 3)),
            Forgetting.initialStability(atom()))
        XCTAssertEqual(
            Forgetting.initialStability(atom(corrections: 6)),
            Forgetting.initialStability(atom(corrections: 60)))
    }

    func testARuleDoesNotFadeBecauseNobodyMentionedItLately() {
        // That is exactly when a rule gets broken. A standing instruction has a
        // floor; a decision or a fact does not.
        XCTAssertEqual(0.40, Forgetting.floorFor(.ruling))
        XCTAssertEqual(0.40, Forgetting.floorFor(.relationship))
        XCTAssertEqual(0.20, Forgetting.floorFor(.preference))
        XCTAssertEqual(0.00, Forgetting.floorFor(.decision))
        XCTAssertEqual(0.00, Forgetting.floorFor(.fact))
    }

    func testATenYearOldRulingIsStillOfferedButADecisionIsNot() {
        let old = now.addingTimeInterval(-days(3650))
        XCTAssertFalse(Forgetting.faded(atom(kind: .ruling, recorded: old), trace: nil, now: now))
        XCTAssertTrue(Forgetting.faded(atom(kind: .decision, recorded: old), trace: nil, now: now))
    }

    func testTheClockStartsAtTheLastCorrectionWhenThereWasOne() {
        // Being corrected is a stronger event than being filed, so an old atom
        // corrected yesterday is fresh, not stale.
        var corrected = atom(kind: .decision, recorded: now.addingTimeInterval(-days(3650)))
        corrected.lastCorrectedUtc = now.addingTimeInterval(-days(1))
        XCTAssertFalse(Forgetting.faded(corrected, trace: nil, now: now))
    }

    func testATraceOverridesBoth() {
        let old = atom(kind: .decision, recorded: now.addingTimeInterval(-days(3650)))
        let trace = MemoryTrace(retrievals: 5,
                                lastRetrievedUtc: now.addingTimeInterval(-days(1)),
                                stabilityDays: 400)
        XCTAssertFalse(Forgetting.faded(old, trace: trace, now: now))
    }
}

final class MemoryWearTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func atom(kind: AtomKind = .decision) -> MemoryAtom {
        MemoryAtom(kind: kind, recordedAtUtc: now)
    }

    func testAnUntouchedWearKnowsNothing() {
        let w = MemoryWear()
        XCTAssertEqual(0, w.count)
        XCTAssertNil(w.forAtom(UUID()))
        XCTAssertFalse(w.isDirty)
    }

    func testARetrievalCountsAndStampsTheTime() {
        let w = MemoryWear()
        let a = atom()
        w.retrieved(a, now: now)
        let t = w.forAtom(a.id)!
        XCTAssertEqual(1, t.retrievals)
        XCTAssertEqual(now, t.lastRetrievedUtc)
        XCTAssertTrue(w.isDirty)
    }

    func testTheReachIsMeasuredBeforeTheTraceIsUpdated() {
        // Measure it after and every retrieval looks fresh, so nothing ever
        // gains anything and the spacing effect quietly does not exist.
        let a = atom()

        let onceOnly = MemoryWear()
        onceOnly.retrieved(a, now: now)
        let first = onceOnly.forAtom(a.id)!.stabilityDays

        let spaced = MemoryWear()
        spaced.retrieved(a, now: now)
        spaced.retrieved(a, now: now.addingTimeInterval(2000 * 86_400))
        XCTAssertGreaterThan(spaced.forAtom(a.id)!.stabilityDays, first * 2)

        let crammed = MemoryWear()
        crammed.retrieved(a, now: now)
        crammed.retrieved(a, now: now.addingTimeInterval(1))
        XCTAssertLessThan(
            crammed.forAtom(a.id)!.stabilityDays,
            spaced.forAtom(a.id)!.stabilityDays,
            "two retrievals a second apart gained as much as one at the edge of fading")
    }

    func testRetrievalsAccumulateAndABatchMarksEveryAtom() {
        let w = MemoryWear()
        let a = atom()
        for i in 0..<3 { w.retrieved(a, now: now.addingTimeInterval(Double(i) * 86_400)) }
        XCTAssertEqual(3, w.forAtom(a.id)!.retrievals)

        let batch = MemoryWear()
        batch.retrieved((0..<4).map { _ in atom() }, now: now)
        XCTAssertEqual(4, batch.count)
    }

    func testARetrievedAtomIsHarderToFadeThanAnUntouchedOne() {
        let w = MemoryWear()
        let old = MemoryAtom(kind: .decision, recordedAtUtc: now.addingTimeInterval(-3650 * 86_400))
        XCTAssertTrue(w.faded(old, now: now))
        w.retrieved(old, now: now)
        XCTAssertFalse(w.faded(old, now: now), "retrieving it did not bring it back")
    }

    func testASnapshotRestoresRowForRow() {
        let w = MemoryWear()
        let a = atom()
        w.retrieved(a, now: now)
        let restored = MemoryWear()
        restored.restore(w.snapshot())
        XCTAssertEqual(w.count, restored.count)
        XCTAssertEqual(w.forAtom(a.id), restored.forAtom(a.id))
        XCTAssertFalse(restored.isDirty, "a freshly loaded wear file should not need writing back")
    }
}

final class MemoryFolderTests: XCTestCase {

    private func tempDir() -> String {
        let d = NSTemporaryDirectory() + "memfolder-" + UUID().uuidString
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }

    func testEveryMachineGetsItsOwnLogBecauseOneWriterCannotConflict() throws {
        let dir = tempDir()
        let a = try MemoryFolder(path: dir, machine: "linux-build")
        let b = try MemoryFolder(path: dir, machine: "windows-dev")
        XCTAssertNotEqual(a.ownLog, b.ownLog)
        XCTAssertTrue(a.ownLog.hasSuffix("atoms.linux-build.jsonl"))
        XCTAssertTrue(a.indexPath.hasSuffix("index.linux-build.db"))
    }

    func testAMachineNameThatIdentifiesNothingGetsAMintedIdInstead() throws {
        // Every Android device reports localhost, so two phones would both call
        // themselves android-localhost and append to ONE log — the merge problem
        // this whole layout exists to avoid, arriving through the front door.
        let dir = tempDir()
        let f = try MemoryFolder(path: dir, machine: "android-unnamed")
        XCTAssertFalse(f.machine.hasSuffix(MemoryFolder.anonymous))
        XCTAssertTrue(f.machine.hasPrefix("android-"))
        XCTAssertGreaterThan(f.machine.count, "android-".count)
    }

    func testTheMintedIdIsStableAcrossRunsInTheSameFolder() throws {
        let dir = tempDir()
        let first = try MemoryFolder(path: dir, machine: "android-unnamed").machine
        let second = try MemoryFolder(path: dir, machine: "android-unnamed").machine
        XCTAssertEqual(first, second, "the machine id was re-minted and the logs would split")
    }

    func testLocalhostAndUnknownAreBothTreatedAsAnonymous() {
        XCTAssertTrue(MemoryFolder.defaultMachineName(host: "localhost", platform: "linux")
            .hasSuffix(MemoryFolder.anonymous))
        XCTAssertTrue(MemoryFolder.defaultMachineName(host: "UNKNOWN", platform: "linux")
            .hasSuffix(MemoryFolder.anonymous))
        XCTAssertTrue(MemoryFolder.defaultMachineName(host: "   ", platform: "linux")
            .hasSuffix(MemoryFolder.anonymous))
        XCTAssertEqual("linux-buildbox",
                       MemoryFolder.defaultMachineName(host: "buildbox", platform: "linux"))
    }

    func testAMachineNameIsSanitisedIntoSomethingThatCanBeAFileName() throws {
        let dir = tempDir()
        XCTAssertEqual("my-box-2", try MemoryFolder(path: dir, machine: "My Box/2").machine)
        XCTAssertEqual("unknown", try MemoryFolder(path: dir, machine: "///").machine)
    }

    func testListingLogsIsStableSoARebuildIsReproducible() throws {
        let dir = tempDir()
        let f = try MemoryFolder(path: dir, machine: "linux-build")
        for name in ["atoms.zebra.jsonl", "atoms.alpha.jsonl", "notes.txt"] {
            try "".write(toFile: (dir as NSString).appendingPathComponent(name),
                         atomically: true, encoding: .utf8)
        }
        XCTAssertEqual(["atoms.alpha.jsonl", "atoms.zebra.jsonl"],
                       f.allLogs.map { ($0 as NSString).lastPathComponent })
    }

    func testTheGitIgnoreKeepsTheDerivedAndTheLocalOutOfTheRepo() throws {
        // The index is rebuildable; wear is this machine's habits, and syncing
        // it would put one machine in charge of what another finds easy to
        // recall. The LOGS themselves must be committed.
        let dir = tempDir()
        let f = try MemoryFolder(path: dir, machine: "linux-build")
        try f.ensureGitIgnore()
        let text = try String(contentsOfFile: (dir as NSString).appendingPathComponent(".gitignore"),
                              encoding: .utf8)
        XCTAssertTrue(text.contains("index.*.db"))
        XCTAssertTrue(text.contains("wear.*.json"))
        XCTAssertTrue(text.contains(".machine-id"))
        XCTAssertFalse(text.contains("atoms."))
    }

    func testAnExistingGitIgnoreIsNotOverwritten() throws {
        let dir = tempDir()
        let file = (dir as NSString).appendingPathComponent(".gitignore")
        try "mine".write(toFile: file, atomically: true, encoding: .utf8)
        try MemoryFolder(path: dir, machine: "linux-build").ensureGitIgnore()
        XCTAssertEqual("mine", try String(contentsOfFile: file, encoding: .utf8))
    }

    func testABlankPathIsRefused() {
        XCTAssertThrowsError(try MemoryFolder(path: "  "))
    }
}
