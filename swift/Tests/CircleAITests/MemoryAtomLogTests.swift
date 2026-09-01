import XCTest
@testable import CircleAI

/// A store that hands back exactly what a test puts in it, in that order.
final class FakeAtomStore: IAtomStore, @unchecked Sendable {
    private let lock = NSLock()
    private var atoms: [MemoryAtom] = []
    private(set) var lastMatchLimit = -1

    init(_ seed: [MemoryAtom] = []) { atoms = seed }

    func add(_ atom: MemoryAtom) async throws {
        lock.lock(); atoms.append(atom); lock.unlock()
    }

    func supersede(_ oldAtomId: UUID, with replacement: MemoryAtom) async throws -> MemoryAtom {
        lock.lock()
        if let i = atoms.firstIndex(where: { $0.id == oldAtomId }) {
            atoms[i].supersededBy = replacement.id
        }
        var carried = replacement
        carried.corrections = max(replacement.corrections, 1)
        atoms.append(carried)
        lock.unlock()
        return carried
    }

    func match(_ situation: Situation, limit: Int) async throws -> [MemoryAtom] {
        lock.lock(); defer { lock.unlock() }
        lastMatchLimit = limit
        return Array(atoms.filter { $0.kind != .relationship && $0.isCurrent }.prefix(limit))
    }

    func byKind(_ kind: AtomKind, limit: Int) async throws -> [MemoryAtom] {
        lock.lock(); defer { lock.unlock() }
        return Array(atoms.filter { $0.kind == kind && $0.isCurrent }.prefix(limit))
    }

    func all(includeSuperseded: Bool, limit: Int) async throws -> [MemoryAtom] {
        lock.lock(); defer { lock.unlock() }
        let pool = includeSuperseded ? atoms : atoms.filter(\.isCurrent)
        return Array(pool.prefix(limit))
    }

    func knows(_ text: String) async throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        let key = CueExtractor.normalise(text)
        return atoms.contains { $0.isCurrent && CueExtractor.normalise($0.text) == key }
    }

    func get(_ id: UUID) async throws -> MemoryAtom? {
        lock.lock(); defer { lock.unlock() }
        return atoms.first { $0.id == id }
    }

    func markVerified(_ id: UUID, ok: Bool, whenUtc: Date) async throws {
        lock.lock()
        if let i = atoms.firstIndex(where: { $0.id == id }) {
            atoms[i].verifiedOk = ok
            atoms[i].verifiedAtUtc = whenUtc
        }
        lock.unlock()
    }

    func count() async throws -> Int {
        lock.lock(); defer { lock.unlock() }
        return atoms.filter(\.isCurrent).count
    }
}

final class AtomLogTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func tempDir() -> String {
        let d = NSTemporaryDirectory() + "atomlog-" + UUID().uuidString
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }

    private func atom(_ text: String = "Never use adb push to install",
                      kind: AtomKind = .ruling,
                      recorded: Date? = nil) -> MemoryAtom {
        MemoryAtom(kind: kind, text: text, subject: "deploy", recordedAtUtc: recorded ?? now)
    }

    func testALineIsPlainJsonAPersonCanRead() throws {
        let dir = tempDir()
        let log = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        try log.append(atom())
        let text = try String(
            contentsOfFile: (dir as NSString).appendingPathComponent("atoms.linux-build.jsonl"),
            encoding: .utf8)
        XCTAssertTrue(text.hasPrefix("{"))
        XCTAssertTrue(text.contains("Never use adb push"))
        XCTAssertTrue(text.contains("linux-build"))
    }

    func testTheCharactersSomebodyTypedAreWrittenNotEscaped() throws {
        // Half the reason this is text is that a person can open it and read it.
        // An escaping encoder turns isiZulu, Amharic or Japanese into runs of
        // backslash-u and the file stops being readable at all.
        let dir = tempDir()
        let log = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        try log.append(atom("Yenza kanjena, ngiyabonga - ありがとう"))
        let text = try String(
            contentsOfFile: (dir as NSString).appendingPathComponent("atoms.linux-build.jsonl"),
            encoding: .utf8)
        XCTAssertTrue(text.contains("ありがとう"))
        XCTAssertTrue(text.contains("ngiyabonga"))
        XCTAssertFalse(text.contains("\\u"), "the log escaped characters somebody typed")
    }

    func testACorrectionIsANewLineNamingWhatItSupersedes() throws {
        // A row in a table can be UPDATEd; a line already written cannot. The
        // forward pointer is derived on replay, and that is what makes two
        // machines' logs mergeable by simple concatenation.
        let dir = tempDir()
        let log = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        let first = atom()
        try log.append(first)
        let replacement = atom("Use dotnet build -t:Install instead",
                               recorded: now.addingTimeInterval(3600))
        try log.append(replacement, supersedes: first.id)

        let all = log.readAll()
        XCTAssertEqual(2, all.count)
        let original = all.first { $0.id == AtomLog.compact(first.id) }!
        let correction = all.first { $0.id == AtomLog.compact(replacement.id) }!
        XCTAssertNil(original.supersedes)
        XCTAssertEqual("Never use adb push to install", original.text)
        XCTAssertEqual(AtomLog.compact(first.id), correction.supersedes)
    }

    func testARecordRoundTripsBackToTheAtomItCameFrom() throws {
        let a = MemoryAtom(
            kind: .preference, text: "Answer first, explain after", subject: "style",
            sourceEpisode: UUID(), recordedAtUtc: now,
            challenge: "why", outcome: .resolved, verify: "check the README")
        let dir = tempDir()
        let log = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        let back = AtomLog.rehydrate(try log.append(a))

        XCTAssertEqual(a.id, back.id)
        XCTAssertEqual(a.kind, back.kind)
        XCTAssertEqual(a.text, back.text)
        XCTAssertEqual(a.subject, back.subject)
        XCTAssertEqual(a.challenge, back.challenge)
        XCTAssertEqual(a.outcome, back.outcome)
        XCTAssertEqual(a.verify, back.verify)
        XCTAssertEqual(a.sourceEpisode, back.sourceEpisode)
        XCTAssertEqual("linux-build", back.machine)
        XCTAssertEqual(a.recordedAtUtc.timeIntervalSince1970,
                       back.recordedAtUtc.timeIntervalSince1970, accuracy: 0.001)
    }

    func testEveryKindAndOutcomeSurvivesTheRoundTrip() throws {
        let dir = tempDir()
        let log = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        for kind in AtomKind.allCases {
            for outcome in DecisionOutcome.allCases.map({ Optional($0) }) + [nil] {
                let a = MemoryAtom(kind: kind, text: "x", recordedAtUtc: now, outcome: outcome)
                let back = AtomLog.rehydrate(try log.append(a))
                XCTAssertEqual(kind, back.kind)
                XCTAssertEqual(outcome, back.outcome)
            }
        }
    }

    func testTheIdIsTheThirtyTwoCharacterFormTheCSharpUses() {
        let id = UUID()
        let compact = AtomLog.compact(id)
        XCTAssertEqual(32, compact.count)
        XCTAssertFalse(compact.contains("-"))
        XCTAssertEqual(id, AtomLog.parseCompact(compact))
        XCTAssertNil(AtomLog.parseCompact("too-short"))
        XCTAssertNil(AtomLog.parseCompact(String(repeating: "z", count: 32)))
    }

    func testReplayOrdersByTimeAcrossEveryMachineLog() throws {
        // A correction made on the Mac has to supersede a decision made on
        // Windows the same way it would have locally.
        let dir = tempDir()
        try AtomLog(folder: try MemoryFolder(path: dir, machine: "windows-dev"))
            .append(atom("written on windows first", recorded: now))
        try AtomLog(folder: try MemoryFolder(path: dir, machine: "mac-build"))
            .append(atom("written on the mac later", recorded: now.addingTimeInterval(3600)))

        let all = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build")).readAll()
        XCTAssertEqual(2, all.count)
        XCTAssertEqual("written on windows first", all[0].text)
        XCTAssertEqual("written on the mac later", all[1].text)
    }

    func testAnIdenticalTimestampOrdersTheSameOnEveryMachine() throws {
        // Two records at the same instant must not order differently depending
        // on which machine read them, or a rebuild produces a different memory
        // on each box.
        let dir = tempDir()
        try AtomLog(folder: try MemoryFolder(path: dir, machine: "windows-dev"))
            .append(atom("from windows", recorded: now))
        try AtomLog(folder: try MemoryFolder(path: dir, machine: "mac-build"))
            .append(atom("from mac", recorded: now))

        let a = AtomLog(folder: try MemoryFolder(path: dir, machine: "linux-build")).readAll().map(\.text)
        let b = AtomLog(folder: try MemoryFolder(path: dir, machine: "mac-build")).readAll().map(\.text)
        XCTAssertEqual(a, b)
        XCTAssertEqual(["from mac", "from windows"], a)
    }

    func testAnUnreadableLineCostsOnlyItself() throws {
        // One truncated write must not cost every memory in the file behind it.
        let dir = tempDir()
        let folder = try MemoryFolder(path: dir, machine: "linux-build")
        let log = AtomLog(folder: folder)
        try log.append(atom("the first one that was written"))
        if let h = FileHandle(forWritingAtPath: folder.ownLog) {
            try h.seekToEnd()
            try h.write(contentsOf: Data("{\"id\": truncated\n".utf8))
            try h.close()
        }
        try log.append(atom("the third one that was written"))
        XCTAssertEqual(2, log.readAll().count)
    }

    func testAnInterruptedLineDoesNotSwallowTheNextRecord() throws {
        // A half-written line with no trailing newline would otherwise absorb
        // whatever is appended next, losing both.
        let dir = tempDir()
        let folder = try MemoryFolder(path: dir, machine: "linux-build")
        try "{\"id\":\"\(String(repeating: "a", count: 32))\",\"text\":\"half a line"
            .write(toFile: folder.ownLog, atomically: true, encoding: .utf8)
        try AtomLog(folder: folder).append(atom("the one written afterwards"))

        let lines = try String(contentsOfFile: folder.ownLog, encoding: .utf8)
            .split(separator: "\n").filter { !$0.isEmpty }
        XCTAssertEqual(2, lines.count, "the append ran into the truncated line")
    }

    func testABrokenTimestampSortsFirstRatherThanThrowing() throws {
        let dir = tempDir()
        try "{\"id\":\"\(String(repeating: "a", count: 32))\",\"kind\":\"Decision\",\"text\":\"broken date\",\"recorded\":\"not a date\",\"machine\":\"odd\"}\n"
            .write(toFile: (dir as NSString).appendingPathComponent("atoms.odd.jsonl"),
                   atomically: true, encoding: .utf8)
        let folder = try MemoryFolder(path: dir, machine: "linux-build")
        try AtomLog(folder: folder).append(atom("a good one"))
        let all = AtomLog(folder: folder).readAll()
        XCTAssertEqual(2, all.count)
        XCTAssertEqual("broken date", all[0].text)
    }

    func testAnEmptyFolderReadsAsNoRecords() throws {
        XCTAssertTrue(AtomLog(folder: try MemoryFolder(path: tempDir(), machine: "linux-build"))
            .readAll().isEmpty)
    }
}

final class AtomLearnerTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)
    private let rule = "Never use adb push to install, it keeps the old data."
    private let soft = "I like the shorter form of that command better."

    private func episode(_ said: String, at: Date? = nil) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(recordedAt: at ?? now, userText: said, assistantText: "")
    }

    func testSomethingCertainIsRecordedAndSomethingFaintIsOffered() async throws {
        var kept: [MemoryAtom] = []
        let report = try await AtomLearner().learn(
            episodes: [episode(rule), episode(soft)],
            record: { kept.append($0) },
            known: [])
        XCTAssertEqual(1, report.recorded.count)
        XCTAssertEqual(1, kept.count)
        XCTAssertEqual(1, report.offered.count)
        XCTAssertFalse(report.offered[0].certain)
        XCTAssertEqual(2, report.considered)
    }

    func testRunningItTwiceIsTheSameAsRunningItOnce() async throws {
        // After a crash, a pull, or simply a second pass.
        var kept: [MemoryAtom] = []
        let learner = AtomLearner()
        let episodes = [episode(rule)]
        _ = try await learner.learn(episodes: episodes, record: { kept.append($0) }, known: [])
        let second = try await learner.learn(episodes: episodes, record: { kept.append($0) }, known: kept)
        XCTAssertEqual(1, kept.count)
        XCTAssertEqual(1, second.alreadyKnown.count)
        XCTAssertTrue(second.recorded.isEmpty)
    }

    func testTheSameSentenceTwiceInOnePassIsKeptOnce() async throws {
        // It is not in any store yet, so the store check cannot catch it.
        var kept: [MemoryAtom] = []
        let report = try await AtomLearner().learn(
            episodes: [episode(rule), episode(rule, at: now.addingTimeInterval(60))],
            record: { kept.append($0) },
            known: [])
        XCTAssertEqual(1, kept.count)
        XCTAssertEqual(1, report.alreadyKnown.count)
    }

    func testTheSentenceKeptIsTheOneSaidFirst() async throws {
        // A rebuild has to land on the same atom either way.
        var kept: [MemoryAtom] = []
        _ = try await AtomLearner().learn(
            episodes: [episode(rule, at: now.addingTimeInterval(86_400)), episode(rule, at: now)],
            record: { kept.append($0) },
            known: [])
        XCTAssertEqual(1, kept.count)
        XCTAssertEqual(now, kept[0].recordedAtUtc)
    }

    func testAlreadyKnownBeatsNotSureEnough() async throws {
        // A sentence already remembered is not a question for anybody, however
        // faintly it was spotted.
        let report = try await AtomLearner().learn(
            episodes: [episode(soft)],
            record: { _ in },
            known: [MemoryAtom(kind: .preference, text: soft)])
        XCTAssertEqual(1, report.alreadyKnown.count)
        XCTAssertTrue(report.offered.isEmpty, "an already-known sentence was offered for confirmation")
    }

    func testTheKnownCheckIgnoresPunctuationAndCase() async throws {
        let report = try await AtomLearner().learn(
            episodes: [episode(rule)],
            record: { _ in },
            known: [MemoryAtom(text: "NEVER USE ADB PUSH TO INSTALL, IT KEEPS THE OLD DATA")])
        XCTAssertEqual(1, report.alreadyKnown.count)
        XCTAssertTrue(report.recorded.isEmpty)
    }

    func testReadingSpotsWithoutKeepingAnything() {
        let learner = AtomLearner()
        XCTAssertEqual(1, learner.read(episode(rule)).count)
        XCTAssertEqual("cues", learner.extractorName)
    }

    func testAnEmptyBatchIsAnEmptyReport() async throws {
        let report = try await AtomLearner().learn(episodes: [], record: { _ in }, known: [])
        XCTAssertEqual(0, report.considered)
        XCTAssertTrue(report.recorded.isEmpty)
    }
}

final class MemorySyncTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func tempDir() -> String {
        let d = NSTemporaryDirectory() + "memsync-" + UUID().uuidString
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }

    private func atom(_ text: String, recorded: Date? = nil) -> MemoryAtom {
        MemoryAtom(kind: .ruling, text: text, subject: "deploy", recordedAtUtc: recorded ?? now)
    }

    func testRecordingWritesTheLogAndTheIndexTogether() async throws {
        let sync = MemorySync(folder: try MemoryFolder(path: tempDir(), machine: "linux-build"))
        let store = FakeAtomStore()
        try await sync.record(store: store, atom: atom("Never use adb push to install"))
        let count = try await store.count()
        XCTAssertEqual(1, count)
        XCTAssertEqual(1, sync.log.readAll().count)
    }

    func testTheIndexHoldsWhatTheLogSaysNotWhatTheCallerPassed() async throws {
        // Reading the line back is what makes "the index now" and "the index
        // after a rebuild" the same thing without two pieces of code agreeing.
        let sync = MemorySync(folder: try MemoryFolder(path: tempDir(), machine: "linux-build"))
        let store = FakeAtomStore()
        try await sync.record(store: store, atom: atom("Never use adb push to install"))
        let all = try await store.all()
        XCTAssertEqual("linux-build", all.first?.machine)
    }

    func testARebuildFromTheLogsProducesTheSameMemory() async throws {
        let dir = tempDir()
        let sync = MemorySync(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        let first = FakeAtomStore()
        try await sync.record(store: first, atom: atom("Never use adb push to install"))
        try await sync.record(store: first,
                              atom: atom("Always uninstall before deploying",
                                         recorded: now.addingTimeInterval(60)))

        let rebuilt = FakeAtomStore()
        let report = try await MemorySync(folder: try MemoryFolder(path: dir, machine: "linux-build"))
            .rebuild(into: rebuilt)

        XCTAssertEqual(2, report.records)
        XCTAssertEqual(2, report.atoms)
        XCTAssertEqual(2, report.current)
        XCTAssertEqual(1, report.machines)
        let a = try await first.count(), b = try await rebuilt.count()
        XCTAssertEqual(a, b)
    }

    func testSupersedingIsResolvedDuringReplay() async throws {
        // A log line can only point BACKWARDS at what it replaces; the forward
        // pointer the index wants is worked out by walking in time order.
        let dir = tempDir()
        let sync = MemorySync(folder: try MemoryFolder(path: dir, machine: "linux-build"))
        let store = FakeAtomStore()
        let first = atom("The old way of deploying")
        try await sync.record(store: store, atom: first)
        try await sync.record(store: store,
                              atom: atom("The new way of deploying", recorded: now.addingTimeInterval(60)),
                              supersedes: first.id)

        let current = MemorySync(folder: try MemoryFolder(path: dir, machine: "linux-build")).current()
        XCTAssertEqual(1, current.count)
        XCTAssertEqual("The new way of deploying", current[0].text)

        let all = MemorySync(folder: try MemoryFolder(path: dir, machine: "linux-build")).replay().atoms
        XCTAssertEqual(2, all.count)
        XCTAssertNotNil(all.first { $0.id == first.id }?.supersededBy)
    }

    func testACorrectionMadeOnTheMacAppliesToADecisionMadeOnWindows() async throws {
        // They are just two lines in one ordered stream, which is the whole
        // reason superseding is resolved on replay rather than in the log.
        let dir = tempDir()
        let first = atom("The old way of deploying")
        try await MemorySync(folder: try MemoryFolder(path: dir, machine: "windows-dev"))
            .record(store: FakeAtomStore(), atom: first)
        try await MemorySync(folder: try MemoryFolder(path: dir, machine: "mac-build"))
            .record(store: FakeAtomStore(),
                    atom: atom("The new way of deploying", recorded: now.addingTimeInterval(3600)),
                    supersedes: first.id)

        let replay = MemorySync(folder: try MemoryFolder(path: dir, machine: "linux-build")).replay()
        XCTAssertEqual(2, replay.machines)
        XCTAssertEqual(1, replay.atoms.filter(\.isCurrent).count)
        XCTAssertEqual("The new way of deploying", replay.atoms.first(where: \.isCurrent)?.text)
    }

    func testTheCorrectionCountCarriesDownTheChain() async throws {
        // An atom corrected on three machines reads as corrected three times
        // rather than once each — which is what makes a much-argued rule
        // outrank a fresh one.
        let dir = tempDir()
        let store = FakeAtomStore()
        let a = atom("version one of the rule")
        try await MemorySync(folder: try MemoryFolder(path: dir, machine: "m1"))
            .record(store: store, atom: a)
        let b = atom("version two of the rule", recorded: now.addingTimeInterval(60))
        try await MemorySync(folder: try MemoryFolder(path: dir, machine: "m2"))
            .record(store: store, atom: b, supersedes: a.id)
        let c = atom("version three of the rule", recorded: now.addingTimeInterval(120))
        try await MemorySync(folder: try MemoryFolder(path: dir, machine: "m3"))
            .record(store: store, atom: c, supersedes: b.id)

        let current = MemorySync(folder: try MemoryFolder(path: dir, machine: "m1")).current()
        XCTAssertEqual(1, current.count)
        XCTAssertEqual("version three of the rule", current[0].text)
        XCTAssertEqual(2, current[0].corrections)
        XCTAssertNotNil(current[0].lastCorrectedUtc)
    }

    func testAnEmptyFolderRebuildsToNothing() async throws {
        let report = try await MemorySync(folder: try MemoryFolder(path: tempDir(), machine: "linux-build"))
            .rebuild(into: FakeAtomStore())
        XCTAssertEqual(SyncReport(records: 0, atoms: 0, current: 0, machines: 0), report)
    }
}
