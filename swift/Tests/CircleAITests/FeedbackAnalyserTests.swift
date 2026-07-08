// FeedbackAnalyserTests.swift
// Exercises FeedbackAnalyser (persona-adaptation deltas from a window of
// signals) and InMemoryFeedbackStore. Mirrors the C# FeedbackAnalyser rules and
// the verified TS feedback_analyser.test.ts.

import XCTest
@testable import CircleAI

final class FeedbackAnalyserTests: XCTestCase {

    // FP32 deltas — must equal the C# `float` literals exactly.
    private let verbosityDown: Float = -0.1
    private let verbosityUp: Float = 0.05

    private var seq: Double = 0
    private func make(_ polarity: FeedbackPolarity, at: Date? = nil, user: String = "user") -> FeedbackSignal {
        // Monotonic default timestamps so window ordering is deterministic per call.
        let ts = at ?? Date(timeIntervalSince1970: 1_700_000_000 + seq)
        seq += 1
        return FeedbackSignal(recordedAt: ts, userText: user, assistantText: "response", polarity: polarity)
    }

    // ── FeedbackAnalyser ──────────────────────────────────────────────────

    func testEmptySignalSetReturnsZeroDeltas() {
        let a = FeedbackAnalyser().analyse([])
        XCTAssertEqual(a.verbosityDelta, 0)
        XCTAssertEqual(a.formalityDelta, 0)
        XCTAssertEqual(a.preferredTopics, [])
    }

    func testDropsVerbosityWhenOver70PercentNegative() {
        let analyser = FeedbackAnalyser()
        // 8 negative + 2 positive = 80% negative.
        var signals: [FeedbackSignal] = []
        for _ in 0..<8 { signals.append(make(.negative)) }
        for _ in 0..<2 { signals.append(make(.positive)) }

        let a = analyser.analyse(signals)
        XCTAssertEqual(a.verbosityDelta, verbosityDown)
        XCTAssertEqual(a.formalityDelta, 0)
        XCTAssertEqual(a.preferredTopics, [])
    }

    func testRaisesVerbosityWhenOver70PercentPositive() {
        let analyser = FeedbackAnalyser()
        var signals: [FeedbackSignal] = []
        for _ in 0..<8 { signals.append(make(.positive)) }
        for _ in 0..<2 { signals.append(make(.negative)) }

        XCTAssertEqual(analyser.analyse(signals).verbosityDelta, verbosityUp)
    }

    func testBalancedWindowLeavesVerbosityAtZero() {
        let analyser = FeedbackAnalyser()
        var signals: [FeedbackSignal] = []
        for _ in 0..<5 { signals.append(make(.positive)) }
        for _ in 0..<5 { signals.append(make(.negative)) }

        XCTAssertEqual(analyser.analyse(signals).verbosityDelta, 0)
    }

    func testExactly70PercentDoesNotCrossThreshold() {
        let analyser = FeedbackAnalyser(windowSize: 10)
        // Exactly 7/10 negative — 0.70 is not > 0.70.
        var signals: [FeedbackSignal] = []
        for _ in 0..<7 { signals.append(make(.negative)) }
        for _ in 0..<3 { signals.append(make(.positive)) }

        XCTAssertEqual(analyser.analyse(signals).verbosityDelta, 0)
    }

    func testOnlyConsidersMostRecentWindow() {
        let analyser = FeedbackAnalyser(windowSize: 3)
        // Older bulk is positive; the 3 newest are negative → window 100% negative.
        var older: [FeedbackSignal] = []
        for i in 0..<10 { older.append(make(.positive, at: Date(timeIntervalSince1970: 1000 + Double(i)))) }
        var newest: [FeedbackSignal] = []
        for i in 0..<3 { newest.append(make(.negative, at: Date(timeIntervalSince1970: 9_000_000 + Double(i)))) }

        let a = analyser.analyse(older + newest)
        XCTAssertEqual(a.verbosityDelta, verbosityDown)
    }

    func testIgnoresCorrectionSignalsInRatio() {
        let analyser = FeedbackAnalyser()
        // 8 negative + 2 correction = 8/10 = 80% negative → down.
        var signals: [FeedbackSignal] = []
        for _ in 0..<8 { signals.append(make(.negative)) }
        for _ in 0..<2 { signals.append(make(.correction)) }
        XCTAssertEqual(analyser.analyse(signals).verbosityDelta, verbosityDown)
    }

    // ── InMemoryFeedbackStore ─────────────────────────────────────────────

    func testAddIncrementsCount() async throws {
        let store = InMemoryFeedbackStore()
        try await store.add(make(.positive))
        let n = try await store.count()
        XCTAssertEqual(n, 1)
    }

    func testGetRecentOnEmptyStoreReturnsEmpty() async throws {
        let store = InMemoryFeedbackStore()
        let r = try await store.getRecent(count: 10)
        XCTAssertEqual(r.count, 0)
    }

    func testGetRecentReturnsNewestFirst() async throws {
        let store = InMemoryFeedbackStore()
        let now = Date()
        try await store.add(make(.positive, at: now.addingTimeInterval(-600), user: "old"))
        try await store.add(make(.negative, at: now, user: "new"))

        let result = try await store.getRecent(count: 10)
        XCTAssertEqual(result.count, 2)
        XCTAssertEqual(result[0].userText, "new")
    }

    func testPositiveRatioReturnsNilWithNoSignals() async throws {
        let store = InMemoryFeedbackStore()
        let r = try await store.positiveRatio()
        XCTAssertNil(r)
    }

    func testPositiveRatioReturnsOneWhenAllPositive() async throws {
        let store = InMemoryFeedbackStore()
        try await store.add(make(.positive))
        try await store.add(make(.positive))
        let r = try await store.positiveRatio()
        XCTAssertEqual(r, 1.0)
    }

    func testPositiveRatioRightFractionForMixed() async throws {
        let store = InMemoryFeedbackStore()
        try await store.add(make(.positive))
        try await store.add(make(.positive))
        try await store.add(make(.negative))
        let ratio = try await store.positiveRatio()
        XCTAssertNotNil(ratio)
        XCTAssertGreaterThan(ratio!, 0.66)
        XCTAssertLessThan(ratio!, 0.68) // 2/3
    }

    func testEvictsOldestWhenMaxExceeded() async throws {
        let store = InMemoryFeedbackStore(maxSignals: 3)
        for i in 0..<5 { try await store.add(make(.positive, user: "u\(i)")) }
        let n = try await store.count()
        XCTAssertEqual(n, 3)
    }
}
