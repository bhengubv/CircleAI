import XCTest
@testable import CircleAI

final class JsonStoreTests: XCTestCase {

    private func tempDir() -> String {
        let d = NSTemporaryDirectory() + "jsonstore-" + UUID().uuidString
        try? FileManager.default.createDirectory(atPath: d, withIntermediateDirectories: true)
        return d
    }

    func testAnAffectStateRoundTripsThroughDisk() async throws {
        let dir = tempDir()
        let state = AffectState(userId: "u1")
        state.curiosity = 0.9
        state.rapport = 0.4
        state.energy = 0.2
        try await JsonAffectStore(directory: dir).save(state)

        let back = try await JsonAffectStore(directory: dir).load(userId: "u1")
        XCTAssertEqual(0.9, back.curiosity, accuracy: 1e-6)
        XCTAssertEqual(0.4, back.rapport, accuracy: 1e-6)
        XCTAssertEqual(0.2, back.energy, accuracy: 1e-6)
        XCTAssertEqual("u1", back.userId)
    }

    func testAnUnknownUserGetsAFreshStateNotAnError() async throws {
        let back = try await JsonAffectStore(directory: tempDir()).load(userId: "nobody")
        XCTAssertEqual("nobody", back.userId)
        XCTAssertEqual(0.5, back.curiosity, accuracy: 1e-6)
    }

    func testACorruptFileReadsAsAFreshStateRatherThanThrowing() async throws {
        // Affect is a running estimate of how a conversation is going. Refusing
        // to start because one file is unreadable trades a lost estimate for a
        // dead app, and the next save overwrites it anyway.
        let dir = tempDir()
        let store = try JsonAffectStore(directory: dir)
        try "{ not json at all".write(toFile: store.path(for: "u1"),
                                      atomically: true, encoding: .utf8)
        let back = try await store.load(userId: "u1")
        XCTAssertEqual("u1", back.userId)
        XCTAssertEqual(0.5, back.curiosity, accuracy: 1e-6)
    }

    func testAPersonaRoundTripsWithItsWeightsAndCounters() async throws {
        let dir = tempDir()
        let p = PersonaState(userId: "u1")
        p.verbosity = "brief"
        p.formality = "formal"
        p.preferredLocale = "zu-ZA"
        p.topicWeights["deploy"] = 0.8
        p.disfavouredTopics.insert("smalltalk")
        p.totalInteractions = 12
        p.positiveSignals = 9
        p.negativeSignals = 3
        try await JsonPersonaStore(directory: dir).save(p)

        let back = try await JsonPersonaStore(directory: dir).load(userId: "u1")
        XCTAssertEqual("brief", back.verbosity)
        XCTAssertEqual("formal", back.formality)
        XCTAssertEqual("zu-ZA", back.preferredLocale)
        XCTAssertEqual(0.8, back.topicWeights["deploy"] ?? 0, accuracy: 1e-6)
        XCTAssertTrue(back.disfavouredTopics.contains("smalltalk"))
        XCTAssertEqual(12, back.totalInteractions)
    }

    func testAUserIdCannotWriteOutsideTheFolderItWasGiven() throws {
        // The id becomes part of a file name, and one with a slash in it would
        // otherwise land somewhere nobody asked for.
        let dir = tempDir()
        let path = try JsonAffectStore(directory: dir).path(for: "../../etc/passwd")
        XCTAssertEqual(dir, (path as NSString).deletingLastPathComponent)
        XCTAssertFalse((path as NSString).lastPathComponent.contains(".."))
    }

    func testABlankDirectoryIsRefused() {
        XCTAssertThrowsError(try JsonAffectStore(directory: "  "))
        XCTAssertThrowsError(try JsonPersonaStore(directory: ""))
    }

    func testNoTemporaryFileIsLeftBehindAfterASave() async throws {
        // Write-then-rename, and the temporary name is unique per save so two
        // saves for one user cannot contend on one path.
        let dir = tempDir()
        let store = try JsonAffectStore(directory: dir)
        for _ in 0..<5 { try await store.save(AffectState(userId: "u1")) }
        let left = try FileManager.default.contentsOfDirectory(atPath: dir)
            .filter { $0.hasSuffix(".tmp") }
        XCTAssertTrue(left.isEmpty, "left temporary files behind: \(left)")
    }
}

#if canImport(SQLite3)
final class SqliteAtomStoreTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func store() throws -> SqliteAtomStore {
        try SqliteAtomStore(connection: try SqliteConnection.inMemory())
    }

    private func atom(_ text: String = "Never use adb push to install",
                      kind: AtomKind = .ruling,
                      subject: String? = "deploy:android",
                      corrections: Int = 0,
                      recorded: Date? = nil) -> MemoryAtom {
        MemoryAtom(kind: kind, text: text, subject: subject,
                   recordedAtUtc: recorded ?? now, machine: "linux-build",
                   corrections: corrections)
    }

    func testAnAtomRoundTripsThroughEveryColumn() async throws {
        let s = try store()
        let a = MemoryAtom(
            kind: .preference, text: "Answer first, explain after", subject: "style",
            sourceEpisode: UUID(), recordedAtUtc: now, machine: "mac-build",
            corrections: 3, lastCorrectedUtc: now.addingTimeInterval(86_400),
            challenge: "why does it matter", outcome: .resolved,
            verify: "check the README", verifiedAtUtc: now.addingTimeInterval(172_800),
            verifiedOk: true)
        try await s.add(a)
        let back = try await s.get(a.id)!

        XCTAssertEqual(a.id, back.id)
        XCTAssertEqual(a.kind, back.kind)
        XCTAssertEqual(a.text, back.text)
        XCTAssertEqual(a.subject, back.subject)
        XCTAssertEqual(a.sourceEpisode, back.sourceEpisode)
        XCTAssertEqual(a.machine, back.machine)
        XCTAssertEqual(a.corrections, back.corrections)
        XCTAssertEqual(a.challenge, back.challenge)
        XCTAssertEqual(a.outcome, back.outcome)
        XCTAssertEqual(a.verify, back.verify)
        XCTAssertEqual(true, back.verifiedOk)
        XCTAssertEqual(a.recordedAtUtc.timeIntervalSince1970,
                       back.recordedAtUtc.timeIntervalSince1970, accuracy: 0.001)
    }

    func testANullVerifiedOkStaysNullRatherThanBecomingFalse() async throws {
        // Never checked is not the same as checked and wrong, and a store that
        // conflates them makes every unverified fact look stale.
        let s = try store()
        let a = atom(kind: .fact)
        try await s.add(a)
        let v1 = try await s.get(a.id)?.verifiedOk
        XCTAssertNil(v1)
        let v2 = try await s.get(a.id)!.isStale
        XCTAssertFalse(v2)
    }

    func testAddingTheSameAtomTwiceIsTheSameAsAddingItOnce() async throws {
        // Delete-then-insert, which is exactly the idempotence a replay needs.
        let s = try store()
        var a = atom()
        try await s.add(a)
        a.text = "corrected in place"
        try await s.add(a)
        let n = try await s.count()
        XCTAssertEqual(1, n)
        let v3 = try await s.get(a.id)?.text
        XCTAssertEqual("corrected in place", v3)
    }

    func testSupersedingCarriesTheCountForwardAndDoesNotReclassify() async throws {
        // Losing the tally throws away the signal that makes a
        // repeatedly-corrected atom outrank a fresh one; and a ruling corrected
        // into a decision would quietly lose its floor and start fading.
        let s = try store()
        let first = atom(kind: .ruling, corrections: 2)
        try await s.add(first)
        let carried = try await s.supersede(
            first.id, with: atom("Use dotnet build instead", kind: .decision))
        XCTAssertEqual(3, carried.corrections)
        XCTAssertEqual(.ruling, carried.kind)
        XCTAssertNotNil(carried.lastCorrectedUtc)
    }

    func testASupersededAtomIsNeverDeleted() async throws {
        // It stops being an answer and stays readable, because the history is
        // what gives a current atom its weight.
        let s = try store()
        let first = atom()
        try await s.add(first)
        _ = try await s.supersede(first.id, with: atom("the newer version"))

        let v4 = try await s.get(first.id)
        XCTAssertNotNil(v4)
        let v5 = try await s.get(first.id)!.isCurrent
        XCTAssertFalse(v5)
        let current = try await s.count()
        XCTAssertEqual(1, current)
        let v6 = try await s.all(includeSuperseded: true, limit: 50).count
        XCTAssertEqual(2, v6)
        let v7 = try await s.all(includeSuperseded: false, limit: 50).count
        XCTAssertEqual(1, v7)
    }

    func testMarkVerifiedRecordsBothTheAnswerAndWhen() async throws {
        let s = try store()
        let a = atom(kind: .fact)
        try await s.add(a)
        try await s.markVerified(a.id, ok: false, whenUtc: now.addingTimeInterval(259_200))
        let back = try await s.get(a.id)!
        XCTAssertEqual(false, back.verifiedOk)
        XCTAssertTrue(back.isStale)
    }

    func testMatchFindsBySubjectFirstThenFallsBackToKeywords() async throws {
        let s = try store()
        try await s.add(atom("General advice about deploying", subject: "deploy"))
        try await s.add(atom("Specific advice for android", subject: "deploy:android"))
        try await s.add(atom("Something about invoices entirely", subject: "billing"))

        let out = try await s.match(Situation(verb: "deploy", target: "android"), limit: 10)
        XCTAssertEqual(2, out.count)
        XCTAssertFalse(out.contains { $0.subject == "billing" })

        // And with no subject at all, the keyword floor still finds it.
        let s2 = try store()
        try await s2.add(atom("The merlin phone refuses an incremental install", subject: nil))
        let v8 = try await s2.match(Situation(verb: "install", target: "merlin"), limit: 10).count
        XCTAssertEqual(1, v8)
    }

    func testASupersededAtomIsNotOfferedByMatch() async throws {
        let s = try store()
        let first = atom("The old way of deploying to android")
        try await s.add(first)
        _ = try await s.supersede(first.id, with: atom("The new way of deploying to android"))
        let out = try await s.match(Situation(verb: "deploy", target: "android"), limit: 10)
        XCTAssertEqual(1, out.count)
        XCTAssertEqual("The new way of deploying to android", out[0].text)
    }

    func testAnAtomIsNotReturnedTwiceWhenTwoKeysBothMatchIt() async throws {
        // deploy:android and deploy both hit the same row on a walk-up.
        let s = try store()
        try await s.add(atom(subject: "deploy"))
        let v9 = try await s.match(Situation(verb: "deploy", target: "android"), limit: 10).count
        XCTAssertEqual(1, v9)
    }

    func testKnowsIgnoresCaseAndPunctuation() async throws {
        // Learning asks this of every sentence it spots, on every turn.
        let s = try store()
        try await s.add(atom("Never use adb push to install"))
        let v10 = try await s.knows("never   use ADB push to install!")
        XCTAssertTrue(v10)
        let v11 = try await s.knows("something else entirely")
        XCTAssertFalse(v11)
        let v12 = try await s.knows("   ")
        XCTAssertFalse(v12)
    }

    func testASupersededSentenceIsNoLongerKnown() async throws {
        let s = try store()
        let a = atom("Never use adb push to install")
        try await s.add(a)
        _ = try await s.supersede(a.id, with: atom("Use dotnet build to install"))
        let v13 = try await s.knows("Never use adb push to install")
        XCTAssertFalse(v13)
        let v14 = try await s.knows("Use dotnet build to install")
        XCTAssertTrue(v14)
    }

    func testByKindReturnsOnlyThatKindAndOnlyCurrentOnes() async throws {
        let s = try store()
        try await s.add(atom("a standing rule about things", kind: .ruling))
        try await s.add(atom("a preference about things", kind: .preference))
        let old = atom("an older rule about things", kind: .ruling)
        try await s.add(old)
        _ = try await s.supersede(old.id, with: atom("the newer rule", kind: .ruling))

        let rulings = try await s.byKind(.ruling, limit: 50)
        XCTAssertEqual(2, rulings.count)
        XCTAssertTrue(rulings.allSatisfy { $0.kind == .ruling && $0.isCurrent })
    }

    func testListingIsNewestFirst() async throws {
        let s = try store()
        try await s.add(atom("the oldest one written", recorded: now.addingTimeInterval(-172_800)))
        try await s.add(atom("the newest one written", recorded: now))
        try await s.add(atom("the middle one written", recorded: now.addingTimeInterval(-86_400)))
        let newest = try await s.all(includeSuperseded: false, limit: 50).first?.text
        XCTAssertEqual("the newest one written", newest)
    }

    func testATermTooShortToNarrowAnythingIsDropped() {
        // Two letters match everything; past eight terms a keyword search stops
        // narrowing and starts costing.
        XCTAssertEqual(["deploy", "android"], SqliteAtomStore.terms("to deploy on android, ok"))
        XCTAssertEqual(8, SqliteAtomStore.terms((1...20).map { "term\($0)" }.joined(separator: " ")).count)
        XCTAssertEqual(["deploy"], SqliteAtomStore.terms("deploy DEPLOY Deploy"))
    }

    func testAnEmptyStoreAnswersEverythingWithoutComplaint() async throws {
        let s = try store()
        let v15 = try await s.count()
        XCTAssertEqual(0, v15)
        let v16 = try await s.all(includeSuperseded: false, limit: 50).isEmpty
        XCTAssertTrue(v16)
        let v17 = try await s.byKind(.ruling, limit: 50).isEmpty
        XCTAssertTrue(v17)
        let v18 = try await s.match(Situation(verb: "deploy"), limit: 10).isEmpty
        XCTAssertTrue(v18)
        let v19 = try await s.get(UUID())
        XCTAssertNil(v19)
    }
}

final class SqliteEpisodicAndGoalStoreTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func episode(_ id: UUID = UUID(), at: Date? = nil,
                         embedding: [Float]? = [0.1, -0.5, 0.9]) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(id: id, recordedAt: at ?? now, userText: "hello",
                            assistantText: "the answer", appContext: "deploy",
                            embedding: embedding)
    }

    func testAnEpisodeRoundTripsThroughEveryColumn() async throws {
        let s = try SqliteEpisodicStore(connection: try SqliteConnection.inMemory())
        let e = episode()
        try await s.add(e)
        let back = try await s.getRecent(count: 10).first!

        XCTAssertEqual(e.id, back.id)
        XCTAssertEqual(e.userText, back.userText)
        XCTAssertEqual(e.assistantText, back.assistantText)
        XCTAssertEqual(e.appContext, back.appContext)
        XCTAssertEqual(e.recordedAt.timeIntervalSince1970,
                       back.recordedAt.timeIntervalSince1970, accuracy: 0.001)
    }

    func testTheEmbeddingComesBackTheWayItWentIn() async throws {
        // Bytes the other way round produce plausible nonsense rather than an
        // error, and a vector search silently returns the wrong neighbours.
        let s = try SqliteEpisodicStore(connection: try SqliteConnection.inMemory())
        let e = episode()
        try await s.add(e)
        let v20 = try await s.getRecent(count: 10).first?.embedding
        XCTAssertEqual(e.embedding, v20)
    }

    func testTheFloatCodecSurvivesTheAwkwardValues() {
        let hard: [Float] = [0, -0, 1, -1, .leastNormalMagnitude, .greatestFiniteMagnitude, 3.14159]
        let back = SqliteEpisodicStore.bytesToFloats(SqliteEpisodicStore.floatsToBytes(hard))
        XCTAssertEqual(hard, back)
        XCTAssertTrue(SqliteEpisodicStore.bytesToFloats([]).isEmpty)
        XCTAssertTrue(SqliteEpisodicStore.bytesToFloats([1, 2]).isEmpty)
    }

    func testRecentEpisodesAreNewestFirst() async throws {
        let s = try SqliteEpisodicStore(connection: try SqliteConnection.inMemory())
        let a = UUID(), b = UUID(), c = UUID()
        try await s.add(episode(a, at: now))
        try await s.add(episode(c, at: now.addingTimeInterval(172_800)))
        try await s.add(episode(b, at: now.addingTimeInterval(86_400)))
        let v21 = try await s.getRecent(count: 10).map(\.id)
        XCTAssertEqual([c, b, a], v21)
        let v22 = try await s.getRecent(count: 2).count
        XCTAssertEqual(2, v22)
    }

    func testSearchFallsBackToRecencyWithNoQueryVector() async throws {
        // An embedding model that failed to load must leave the store answering
        // something useful rather than nothing. Recall gets worse; it does not
        // stop.
        let s = try SqliteEpisodicStore(connection: try SqliteConnection.inMemory())
        try await s.add(episode(at: now))
        try await s.add(episode(at: now.addingTimeInterval(86_400)))
        let v23 = try await s.search(queryEmbedding: nil, topK: 10).count
        XCTAssertEqual(2, v23)
    }

    func testSearchRanksByCosineWhenThereIsAQueryVector() async throws {
        let s = try SqliteEpisodicStore(connection: try SqliteConnection.inMemory())
        let near = UUID(), far = UUID()
        try await s.add(episode(near, embedding: [1, 0, 0]))
        try await s.add(episode(far, embedding: [0, 0, 1]))
        let out = try await s.search(queryEmbedding: [1, 0, 0], topK: 2)
        XCTAssertEqual(near, out.first?.id)
    }

    func testAZeroVectorIsSimilarToNothingRatherThanNaN() {
        // NaN sorts unpredictably, so a divide-by-zero here would shuffle the
        // results differently on every run.
        XCTAssertEqual(0, SqliteEpisodicStore.cosine([0, 0, 0], [1, 0, 0]))
        XCTAssertEqual(0, SqliteEpisodicStore.cosine([1, 0, 0], [0, 0, 0]))
        XCTAssertEqual(1, SqliteEpisodicStore.cosine([1, 0, 0], [1, 0, 0]), accuracy: 1e-6)
    }

    func testPruningRemovesTheOldAndSaysHowMany() async throws {
        let s = try SqliteEpisodicStore(connection: try SqliteConnection.inMemory())
        try await s.add(episode(at: now.addingTimeInterval(-864_000)))
        try await s.add(episode(at: now))
        let v24 = try await s.pruneOlderThan(cutoff: now.addingTimeInterval(-1))
        XCTAssertEqual(1, v24)
        let v25 = try await s.count()
        XCTAssertEqual(1, v25)
    }

    func testAGoalRoundTripsThroughSql() async throws {
        let s = try SqliteGoalStore(connection: try SqliteConnection.inMemory())
        let g = Goal(id: "g1", userId: "u1", title: "Ship the port",
                     description: "all eight languages", status: .active, priority: .high,
                     createdAt: now, dueAt: now.addingTimeInterval(2_678_400),
                     completedAt: nil, notes: "one language at a time")
        _ = try await s.upsert(g)
        let back = try await s.get(id: "g1")!
        XCTAssertEqual(g.title, back.title)
        XCTAssertEqual(g.description, back.description)
        XCTAssertEqual(.active, back.status)
        XCTAssertEqual(.high, back.priority)
        XCTAssertEqual(g.notes, back.notes)
        XCTAssertNil(back.completedAt)
    }

    func testGoalsFilterByUserAndByActive() async throws {
        let s = try SqliteGoalStore(connection: try SqliteConnection.inMemory())
        func g(_ id: String, _ user: String, _ status: GoalStatus) -> Goal {
            Goal(id: id, userId: user, title: "t", description: "d",
                 status: status, priority: .normal, createdAt: now,
                 dueAt: nil, completedAt: nil, notes: nil)
        }
        _ = try await s.upsert(g("a", "u1", .active))
        _ = try await s.upsert(g("b", "u1", .completed))
        _ = try await s.upsert(g("c", "u2", .active))

        let v26 = try await s.list(userId: "u1").count
        XCTAssertEqual(2, v26)
        let v27 = try await s.getActive(userId: "u1").map(\.id)
        XCTAssertEqual(["a"], v27)
        let v28 = try await s.list(userId: "nobody").isEmpty
        XCTAssertTrue(v28)
    }

    func testDeletingAGoalRemovesItAndAnUnknownIdIsHarmless() async throws {
        let s = try SqliteGoalStore(connection: try SqliteConnection.inMemory())
        _ = try await s.upsert(Goal(id: "g1", userId: "u1", title: "t", description: "d",
                                    status: .active, priority: .normal, createdAt: now,
                                    dueAt: nil, completedAt: nil, notes: nil))
        try await s.delete(id: "g1")
        let v29 = try await s.get(id: "g1")
        XCTAssertNil(v29)
        try await s.delete(id: "g1")
    }
}
#endif
