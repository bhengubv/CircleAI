// ConsolidationTests.swift
//
// Verifies the hierarchical memory-consolidation subsystem ported from
// CircleAI.Memory.Consolidation. Uses a fixed injected clock and hand-built
// EpisodicMemoryEntry lists so every deterministic formula can be asserted
// exactly. Covers: day helpers, full cosine, daily summary formulas + produce/
// idempotency/today-exclusion, weekly clustering's 2-day threshold + centroid,
// high-salience → core promotion, retention pruning (7/30/365), persona-delta
// new-topic detection, OnDemand, and full-cosine ranking in the in-memory
// stores. Mirrors the verified TS suite (consolidation.test.ts).

import XCTest
import Foundation
@testable import CircleAI

final class ConsolidationTests: XCTestCase {

    // ── Fixtures ────────────────────────────────────────────────────────────────

    /// Parses an ISO-8601 UTC instant like "2026-06-08T09:00:00Z".
    private static func iso(_ s: String) -> Date {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: s)!
    }

    private func entry(id: UUID = UUID(),
                       recordedAt: String = "2026-06-01T12:00:00Z",
                       userText: String = "u",
                       assistantText: String = "a",
                       embedding: [Float]? = nil,
                       tags: [String: String]? = nil) -> EpisodicMemoryEntry {
        EpisodicMemoryEntry(id: id, recordedAt: Self.iso(recordedAt), userText: userText,
                            assistantText: assistantText, appContext: nil, embedding: embedding, tags: tags)
    }

    /// Clock fixed at a given instant so week/month math is stable.
    private func fixedClock(_ isoString: String) -> () -> Date {
        let d = Self.iso(isoString)
        return { d }
    }

    private struct Parts {
        let episodic: InMemoryEpisodicStore
        let daily: InMemoryDailyMemoryStore
        let semantic: InMemorySemanticMemoryStore
        let personaDelta: InMemoryPersonaDeltaStore
        let core: InMemoryCoreMemoryStore
        let personaStore: InMemoryPersonaStore
        let summarizer: HeuristicSummarizer
        let consolidator: MemoryConsolidator
    }

    private func makeConsolidator(_ clock: @escaping () -> Date,
                                  options: MemoryConsolidationOptions = MemoryConsolidationOptions()) -> Parts {
        let episodic = InMemoryEpisodicStore(maxEntries: 100000)
        let daily = InMemoryDailyMemoryStore()
        let semantic = InMemorySemanticMemoryStore()
        let personaDelta = InMemoryPersonaDeltaStore()
        let core = InMemoryCoreMemoryStore()
        let personaStore = InMemoryPersonaStore()
        let summarizer = HeuristicSummarizer(clock: clock)
        let consolidator = MemoryConsolidator(episodic: episodic, daily: daily, semantic: semantic,
                                              personaDelta: personaDelta, core: core, personaStore: personaStore,
                                              summarizer: summarizer, options: options, clock: clock)
        return Parts(episodic: episodic, daily: daily, semantic: semantic, personaDelta: personaDelta,
                     core: core, personaStore: personaStore, summarizer: summarizer, consolidator: consolidator)
    }

    // ── Day helpers ─────────────────────────────────────────────────────────────

    func testDayKeyUsesUtcCalendarDay() {
        XCTAssertEqual(dayKey(from: Self.iso("2026-06-08T23:59:59Z")), "2026-06-08")
        XCTAssertEqual(dayKey(from: Self.iso("2026-01-05T00:00:00Z")), "2026-01-05")
    }

    func testMondayOf() {
        XCTAssertEqual(mondayOf("2026-06-08"), "2026-06-08") // Monday → itself
        XCTAssertEqual(mondayOf("2026-06-14"), "2026-06-08") // Sunday → prior Monday
        XCTAssertEqual(mondayOf("2026-06-10"), "2026-06-08") // Wednesday → Monday
    }

    func testAddDaysCrossesMonthBoundaries() {
        XCTAssertEqual(addDays("2026-06-01", -1), "2026-05-31")
        XCTAssertEqual(addDays("2026-06-30", 1), "2026-07-01")
    }

    func testMonthFirstDay() {
        XCTAssertEqual(monthFirstDay(of: "2026-06-17"), "2026-06-01")
    }

    // ── cosineFull ──────────────────────────────────────────────────────────────

    func testCosineFullDirections() {
        XCTAssertEqual(cosineFull([1, 0], [1, 0]), 1, accuracy: 1e-12)
        XCTAssertEqual(cosineFull([1, 0], [0, 1]), 0, accuracy: 1e-12)
        // Not L2-normalised inputs: full cosine still yields 1 for same direction.
        XCTAssertEqual(cosineFull([3, 0], [7, 0]), 1, accuracy: 1e-12)
    }

    func testCosineFullMismatchOrZero() {
        XCTAssertEqual(cosineFull([1, 0], [1, 0, 0]), 0, accuracy: 1e-12)
        XCTAssertEqual(cosineFull([0, 0], [1, 0]), 0, accuracy: 1e-12)
    }

    // ── Daily summarization formulas ────────────────────────────────────────────

    func testDailyFormulasExact() async throws {
        let s = HeuristicSummarizer(clock: fixedClock("2026-06-02T00:00:00Z"))
        // 3 entries: finance×2 (topic tag) + health×1; embeddings [1,0],[0,1],[1,0].
        let entries = [
            entry(embedding: [1, 0], tags: ["topic": "finance"]),
            entry(embedding: [0, 1], tags: ["topic": "health"]),
            entry(embedding: [1, 0], tags: ["topic": "finance"]),
        ]
        let summary = try await s.summarizeDay(day: "2026-06-01", entries: entries)

        XCTAssertEqual(summary.episodeCount, 3)
        XCTAssertEqual(summary.topicWeights["finance"], 2)
        XCTAssertEqual(summary.topicWeights["health"], 1)
        // dispersion = mean(1-cos) over pairs = (1 + 0 + 1)/3 = 2/3
        XCTAssertEqual(summary.topicDispersion, 2.0 / 3.0, accuracy: 1e-12)
        // salience = volume(3/30=0.1)*0.4 + disp(2/3)*0.3 + conc(2/3)*0.3 = 0.44
        XCTAssertEqual(summary.salience, 0.44, accuracy: 1e-12)
        XCTAssertTrue(summary.summary.hasPrefix("On 2026-06-01 you had 3 exchanges."))
        XCTAssertTrue(summary.summary.contains("Top topics: finance, health."))
    }

    func testSplitsPipeDelimitedTopics() async throws {
        let s = HeuristicSummarizer()
        let summary = try await s.summarizeDay(day: "2026-06-01", entries: [
            entry(tags: ["topics": "Finance | Health |finance"]),
        ])
        XCTAssertEqual(summary.topicWeights["finance"], 2)
        XCTAssertEqual(summary.topicWeights["health"], 1)
    }

    func testTopicConcentrationHalfWhenNoTopics() async throws {
        let s = HeuristicSummarizer()
        // 1 entry, no tags, no embedding → dispersion 0, volume 1/30, conc 0.5
        let summary = try await s.summarizeDay(day: "2026-06-01", entries: [entry()])
        let expected = (1.0 / 30.0) * 0.4 + 0 * 0.3 + 0.5 * 0.3
        XCTAssertEqual(summary.salience, expected, accuracy: 1e-12)
        // A single entry is always a highlight, so the standout clause is appended
        // (userText defaults to "u"). No topics → no "Top topics" clause.
        XCTAssertEqual(summary.summary, "On 2026-06-01 you had 1 exchange. Standout moment: \"u\".")
        XCTAssertFalse(summary.summary.contains("Top topics"))
    }

    func testEmptyDaySummary() async throws {
        let s = HeuristicSummarizer()
        let summary = try await s.summarizeDay(day: "2026-06-01", entries: [])
        XCTAssertEqual(summary.episodeCount, 0)
        XCTAssertEqual(summary.summary, "No exchanges recorded on 2026-06-01.")
    }

    // ── Daily pass: production, idempotency, today-exclusion ─────────────────────

    func testDailyProducesAndIsIdempotent() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z") // "today" = 2026-06-08
        let p = makeConsolidator(clock)
        try await p.episodic.add(entry(recordedAt: "2026-06-06T10:00:00Z", tags: ["topic": "x"]))
        try await p.episodic.add(entry(recordedAt: "2026-06-06T11:00:00Z", tags: ["topic": "x"]))

        let r1 = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r1.dailySummariesProduced, 1)
        let summary = try await p.daily.get(day: "2026-06-06")
        XCTAssertNotNil(summary)
        XCTAssertEqual(summary!.episodeCount, 2)

        // Second tick with no new episodes → idempotent skip (episodeCount matches).
        let r2 = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r2.dailySummariesProduced, 0)
        let count = try await p.daily.count()
        XCTAssertEqual(count, 1)
    }

    func testDoesNotSummariseToday() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        // Episode recorded "today" → excluded (day is not < today).
        try await p.episodic.add(entry(recordedAt: "2026-06-08T08:00:00Z"))

        let r = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r.dailySummariesProduced, 0)
        let count = try await p.daily.count()
        XCTAssertEqual(count, 0)
    }

    func testReSummarisesOnCountMismatch() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        try await p.episodic.add(entry(recordedAt: "2026-06-06T10:00:00Z"))
        _ = try await p.consolidator.tick(kind: .daily)
        var d = try await p.daily.get(day: "2026-06-06")
        XCTAssertEqual(d!.episodeCount, 1)

        try await p.episodic.add(entry(recordedAt: "2026-06-06T12:00:00Z"))
        let r = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r.dailySummariesProduced, 1)
        d = try await p.daily.get(day: "2026-06-06")
        XCTAssertEqual(d!.episodeCount, 2)
    }

    // ── High-salience daily → core promotion (≥0.80) ────────────────────────────

    func testPromotesHighSalienceDay() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)

        // 30 entries, single topic 'finance' (conc=1); embeddings 15×[1,0] + 15×[0,1]
        // → dispersion ≈ 0.5172, salience ≈ 0.8552 (≥ 0.80).
        for i in 0..<30 {
            let hh = String(format: "%02d", i % 24)
            try await p.episodic.add(entry(recordedAt: "2026-06-06T\(hh):00:00Z",
                                           embedding: i < 15 ? [1, 0] : [0, 1],
                                           tags: ["topic": "finance"]))
        }

        let r = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r.dailySummariesProduced, 1)
        XCTAssertEqual(r.corePromotions, 1)

        let all = try await p.core.listAll()
        XCTAssertEqual(all.count, 1)
        XCTAssertEqual(all[0].kind, .highSalience)
        XCTAssertEqual(all[0].topic, "finance")
        XCTAssertEqual(all[0].statement, "\"finance\" mattered enough on 2026-06-06 to be remembered.")
        XCTAssertNotNil(all[0].embedding)
    }

    func testDoesNotPromoteLowSalienceDay() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        try await p.episodic.add(entry(recordedAt: "2026-06-06T10:00:00Z", tags: ["topic": "x"]))
        let r = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r.corePromotions, 0)
        let count = try await p.core.count()
        XCTAssertEqual(count, 0)
    }

    // ── Weekly clustering + 2-day threshold ─────────────────────────────────────

    func testWeeklyTwoDayThreshold() async throws {
        let s = HeuristicSummarizer(clock: fixedClock("2026-06-08T00:00:00Z"))
        // Day1: finance=1, health=1 ; Day2: finance=1.
        // finance → 2 days (weight 2) → cluster ; health → 1 day → excluded.
        let day1 = DailyMemorySummary(day: "2026-06-01", episodeCount: 2,
                                      topicWeights: ["finance": 1, "health": 1])
        let day2 = DailyMemorySummary(day: "2026-06-02", episodeCount: 1,
                                      topicWeights: ["finance": 1])

        let clusters = try await s.consolidateWeek(weekStartingMonday: "2026-06-01", daysInWeek: [day1, day2])
        XCTAssertEqual(clusters.count, 1)
        XCTAssertEqual(clusters[0].topic, "finance")
        XCTAssertEqual(clusters[0].topicWeight, 2)
        // salience = min(1, 2/3 + (2/7)*0.25) = 0.7380952…
        XCTAssertEqual(clusters[0].salience, 2.0 / 3.0 + (2.0 / 7.0) * 0.25, accuracy: 1e-12)
        XCTAssertEqual(clusters[0].summary,
                       "Across 2 days this week you returned to \"finance\" — 3 exchanges in total.")
        XCTAssertEqual(Set(clusters[0].sourceDailyIds), Set([day1.id, day2.id]))
    }

    func testWeeklyNoClustersWhenAllSingleDay() async throws {
        let s = HeuristicSummarizer()
        let clusters = try await s.consolidateWeek(weekStartingMonday: "2026-06-01", daysInWeek: [
            DailyMemorySummary(day: "2026-06-01", topicWeights: ["a": 1]),
            DailyMemorySummary(day: "2026-06-02", topicWeights: ["b": 1]),
        ])
        XCTAssertEqual(clusters.count, 0)
    }

    func testWeeklyCentroidIsMeanOfHighlights() async throws {
        let s = HeuristicSummarizer()
        let h1 = entry(embedding: [2, 0])
        let h2 = entry(embedding: [0, 4])
        let day1 = DailyMemorySummary(day: "2026-06-01", highlightEntries: [h1], topicWeights: ["t": 1])
        let day2 = DailyMemorySummary(day: "2026-06-02", highlightEntries: [h2], topicWeights: ["t": 1])
        let clusters = try await s.consolidateWeek(weekStartingMonday: "2026-06-01", daysInWeek: [day1, day2])
        XCTAssertEqual(clusters.count, 1)
        XCTAssertEqual(clusters[0].centroidEmbedding, [1, 2]) // ([2,0]+[0,4])/2
    }

    func testWeeklyPassClustersAndIsIdempotent() async throws {
        // "today" Monday 2026-06-08 → thisMonday 06-08, lastMonday 06-01..lastSunday 06-07.
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        try await p.daily.upsert(DailyMemorySummary(day: "2026-06-01", episodeCount: 2, topicWeights: ["finance": 1]))
        try await p.daily.upsert(DailyMemorySummary(day: "2026-06-03", episodeCount: 1, topicWeights: ["finance": 1]))

        let r1 = try await p.consolidator.tick(kind: .weekly)
        XCTAssertEqual(r1.semanticClustersProduced, 1)
        var count = try await p.semantic.count()
        XCTAssertEqual(count, 1)

        let r2 = try await p.consolidator.tick(kind: .weekly)
        XCTAssertEqual(r2.semanticClustersProduced, 0) // getWeek non-empty → skip
        count = try await p.semantic.count()
        XCTAssertEqual(count, 1)
    }

    // ── Retention pruning ───────────────────────────────────────────────────────

    func testPrunesEpisodicOlderThan7Days() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        // cutoff = now - 7 days = 2026-06-01T09:00:00Z
        let freshId = UUID()
        try await p.episodic.add(entry(id: UUID(), recordedAt: "2026-05-20T00:00:00Z"))
        try await p.episodic.add(entry(id: freshId, recordedAt: "2026-06-06T00:00:00Z"))

        let r = try await p.consolidator.tick(kind: .daily)
        XCTAssertEqual(r.episodesPruned, 1)
        let count = try await p.episodic.count()
        XCTAssertEqual(count, 1)
        let remaining = try await p.episodic.getRecent(count: 10)
        XCTAssertEqual(remaining[0].id, freshId)
    }

    func testPrunesDailiesOlderThan30Days() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        // cutoff = 2026-06-08 - 30 = 2026-05-09. Older day is pruned.
        try await p.daily.upsert(DailyMemorySummary(day: "2026-04-01")) // < cutoff → pruned
        try await p.daily.upsert(DailyMemorySummary(day: "2026-06-03")) // kept

        let r = try await p.consolidator.tick(kind: .weekly)
        XCTAssertEqual(r.dailiesPruned, 1)
        let gone = try await p.daily.get(day: "2026-04-01")
        XCTAssertNil(gone)
        let kept = try await p.daily.get(day: "2026-06-03")
        XCTAssertNotNil(kept)
    }

    func testPrunesSemanticsOlderThan365Days() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        // cutoff = 2026-06-08 - 365 = 2025-06-08.
        try await p.semantic.add(SemanticMemoryCluster(weekStartingMonday: "2024-01-01", topic: "t"))
        try await p.semantic.add(SemanticMemoryCluster(weekStartingMonday: "2026-05-04", topic: "t"))

        let r = try await p.consolidator.tick(kind: .monthly)
        XCTAssertEqual(r.semanticsPruned, 1)
        let count = try await p.semantic.count()
        XCTAssertEqual(count, 1)
    }

    // ── Monthly persona-delta ─────────────────────────────────────────────────────

    func testMonthlyDeltaNewTopicAndIdempotent() async throws {
        // "today" 2026-06-08 → previous month = May 2026 (2026-05-01..2026-05-31).
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)

        // A daily summary inside May so the month has data.
        try await p.daily.upsert(DailyMemorySummary(day: "2026-05-15", episodeCount: 4))

        // Persona "after" has a topic the fresh "before" lacks → newTopic.
        let after = PersonaState()
        after.userId = "default"
        after.topicWeights = ["finance": 3]
        after.totalInteractions = 10
        after.positiveSignals = 6
        after.negativeSignals = 1
        try await p.personaStore.save(after)

        let r1 = try await p.consolidator.tick(kind: .monthly)
        XCTAssertEqual(r1.personaDeltasProduced, 1)
        var deltas = try await p.personaDelta.getForUser(userId: "default")
        XCTAssertEqual(deltas.count, 1)
        XCTAssertEqual(deltas[0].newTopics["finance"], 3)
        XCTAssertEqual(deltas[0].periodStart, "2026-05-15")
        XCTAssertEqual(deltas[0].periodEnd, "2026-05-15")
        XCTAssertTrue(deltas[0].narrative.contains("New interests appeared: finance."))

        // Second monthly tick → idempotent (delta already exists for May).
        let r2 = try await p.consolidator.tick(kind: .monthly)
        XCTAssertEqual(r2.personaDeltasProduced, 0)
        deltas = try await p.personaDelta.getForUser(userId: "default")
        XCTAssertEqual(deltas.count, 1)
    }

    func testMonthlyNoDeltaWhenPreviousMonthEmpty() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)
        let r = try await p.consolidator.tick(kind: .monthly)
        XCTAssertEqual(r.personaDeltasProduced, 0)
        let count = try await p.personaDelta.count()
        XCTAssertEqual(count, 0)
    }

    func testDerivePersonaDeltaSeparatesNewFromStrengthened() async throws {
        let s = HeuristicSummarizer()
        let before = PersonaState()
        before.topicWeights = ["finance": 2]
        before.positiveSignals = 1
        before.negativeSignals = 1
        before.totalInteractions = 5
        before.verbosity = "balanced"

        let after = PersonaState()
        after.topicWeights = ["finance": 5, "travel": 3] // finance strengthened(+3), travel new
        after.positiveSignals = 7
        after.negativeSignals = 2
        after.totalInteractions = 20
        after.verbosity = "detailed"

        let day = DailyMemorySummary(day: "2026-05-10")
        let delta = try await s.derivePersonaDelta(before: before, after: after, daysInPeriod: [day])

        XCTAssertEqual(delta.newTopics["travel"], 3)
        XCTAssertNil(delta.newTopics["finance"])
        XCTAssertEqual(delta.strengthenedTopics["finance"], 3)
        // netSignals = (7-1) - (2-1) = 5 ; interactions = 20-5 = 15
        XCTAssertEqual(delta.netSignalDelta, 5)
        XCTAssertEqual(delta.interactionsInPeriod, 15)
        XCTAssertTrue(delta.narrative.contains("Preferred verbosity shifted from balanced to detailed."))
        XCTAssertTrue(delta.narrative.contains("Net feedback was positive (+5)."))
    }

    // ── OnDemand runs every tier ────────────────────────────────────────────────

    func testOnDemandRunsAllPasses() async throws {
        let clock = fixedClock("2026-06-08T09:00:00Z")
        let p = makeConsolidator(clock)

        // Daily fuel: a completed day earlier this week.
        try await p.episodic.add(entry(recordedAt: "2026-06-06T10:00:00Z", tags: ["topic": "finance"]))
        try await p.episodic.add(entry(recordedAt: "2026-06-06T11:00:00Z", tags: ["topic": "finance"]))
        // Weekly fuel: dailies inside last week (06-01..06-07) with a repeated topic.
        try await p.daily.upsert(DailyMemorySummary(day: "2026-06-01", episodeCount: 2, topicWeights: ["finance": 1]))
        try await p.daily.upsert(DailyMemorySummary(day: "2026-06-02", episodeCount: 1, topicWeights: ["finance": 1]))
        // Monthly fuel: a daily inside May + a persona.
        try await p.daily.upsert(DailyMemorySummary(day: "2026-05-20", episodeCount: 3))
        let persona = PersonaState()
        persona.topicWeights = ["finance": 2]
        persona.totalInteractions = 6
        try await p.personaStore.save(persona)

        let r = try await p.consolidator.tick(kind: .onDemand)
        XCTAssertEqual(r.kind, .onDemand)
        XCTAssertGreaterThanOrEqual(r.dailySummariesProduced, 1)
        XCTAssertGreaterThanOrEqual(r.semanticClustersProduced, 1)
        XCTAssertEqual(r.personaDeltasProduced, 1)
        XCTAssertEqual(r.ranAtUtc, clock())
        let semCount = try await p.semantic.count()
        XCTAssertGreaterThanOrEqual(semCount, 1)
        let deltas = try await p.personaDelta.getForUser(userId: "default")
        XCTAssertEqual(deltas.count, 1)
    }

    // ── In-memory store cosine ranking + ordering ───────────────────────────────

    func testCoreStoreRanksByFullCosine() async throws {
        let core = InMemoryCoreMemoryStore()
        try await core.add(CoreMemory(statement: "x", embedding: [1, 0]))
        try await core.add(CoreMemory(statement: "y", embedding: [0, 1]))
        try await core.add(CoreMemory(statement: "diag", embedding: [1, 1]))

        let ranked = try await core.search(queryEmbedding: [1, 0], topK: 3)
        XCTAssertEqual(ranked[0].statement, "x")   // cos 1
        XCTAssertEqual(ranked[2].statement, "y")   // cos 0
        // 'diag' cos([1,1],[1,0]) = 0.707 → middle
        XCTAssertEqual(ranked[1].statement, "diag")
    }

    func testCoreStoreFallsBackToReinforcementOrder() async throws {
        let core = InMemoryCoreMemoryStore()
        let a = CoreMemory(statement: "a")
        let b = CoreMemory(statement: "b")
        try await core.add(a)
        try await core.add(b)
        try await core.reinforce(id: b.id)
        try await core.reinforce(id: b.id)

        let top = try await core.search(queryEmbedding: nil, topK: 2)
        XCTAssertEqual(top[0].statement, "b") // more reinforced first
        XCTAssertEqual(top[0].reinforcementCount, 2)
    }

    func testSemanticStoreGetWeekAndSearch() async throws {
        let sem = InMemorySemanticMemoryStore()
        try await sem.add(SemanticMemoryCluster(weekStartingMonday: "2026-06-01", topic: "low",
                                                centroidEmbedding: [0, 1], topicWeight: 1))
        try await sem.add(SemanticMemoryCluster(weekStartingMonday: "2026-06-01", topic: "high",
                                                centroidEmbedding: [1, 0], topicWeight: 5))

        let week = try await sem.getWeek(weekStartingMonday: "2026-06-01")
        XCTAssertEqual(week.map { $0.topic }, ["high", "low"])

        let ranked = try await sem.search(queryEmbedding: [1, 0], topK: 2)
        XCTAssertEqual(ranked[0].topic, "high") // centroid [1,0] cos 1
    }

    func testDailyStoreGetRangeIsOrderedInclusive() async throws {
        let daily = InMemoryDailyMemoryStore()
        try await daily.upsert(DailyMemorySummary(day: "2026-06-03"))
        try await daily.upsert(DailyMemorySummary(day: "2026-06-01"))
        try await daily.upsert(DailyMemorySummary(day: "2026-06-10"))

        let range = try await daily.getRange(fromInclusive: "2026-06-01", toInclusive: "2026-06-05")
        XCTAssertEqual(range.map { $0.day }, ["2026-06-01", "2026-06-03"])
    }
}
