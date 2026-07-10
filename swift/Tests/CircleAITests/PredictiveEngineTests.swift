// PredictiveEngineTests.swift
//
// Verifies HistogramPredictiveEngine (time-of-day recurrence forecasting) and
// SequencePredictiveEngine (variable-order Markov chain over the event
// timeline): the empty-timeline fallback, probability normalisation, ordering
// by descending probability, back-off across context lengths, and the
// mean-inter-arrival horizon filter. Timing-sensitive fields are exercised via
// distribution-independent constructions so results do not depend on the wall
// clock.

import XCTest
@testable import CircleAI

final class PredictiveEngineTests: XCTestCase {

    // ── TimeHistogramSlot (pure) ────────────────────────────────────────────────

    func testSlotMatchesDotNetFormula() {
        // 2024-01-07 is a Sunday (DayOfWeek 0). 09:00 UTC → 0*24 + 9 = 9.
        var comps = DateComponents()
        comps.year = 2024; comps.month = 1; comps.day = 7
        comps.hour = 9; comps.minute = 0; comps.second = 0
        comps.timeZone = TimeZone(identifier: "UTC")
        let date = Calendar(identifier: .gregorian).date(from: comps)!
        XCTAssertEqual(TimeHistogramSlot.of(date), 9)

        // 2024-01-08 is a Monday (DayOfWeek 1). 13:00 UTC → 1*24 + 13 = 37.
        comps.day = 8; comps.hour = 13
        let mon = Calendar(identifier: .gregorian).date(from: comps)!
        XCTAssertEqual(TimeHistogramSlot.of(mon), 37)
    }

    // ── HistogramPredictiveEngine ───────────────────────────────────────────────

    func testHistogramEmptyEngineReturnsEmpty() async throws {
        let e = HistogramPredictiveEngine()
        let needs = try await e.anticipate(horizonMinutes: 120)
        XCTAssertTrue(needs.isEmpty)
    }

    /// Fills a need at every one of the 168 slots so the horizon walk always
    /// lands on populated slots regardless of the current wall-clock time.
    private func fillAllSlots(_ e: HistogramPredictiveEngine, _ desc: String, times: Int = 1) {
        let base = Date(timeIntervalSince1970: 0) // slot 0 region; +1h steps cover all
        for slot in 0..<(24 * 7) {
            let when = base.addingTimeInterval(Double(slot) * 3600)
            for _ in 0..<times { e.observe(description: desc, atUtc: when) }
        }
    }

    func testHistogramPopulatedNeedIsAnticipated() async throws {
        let e = HistogramPredictiveEngine()
        fillAllSlots(e, "coffee")
        let needs = try await e.anticipate(horizonMinutes: 60)
        XCTAssertEqual(needs.count, 1)
        XCTAssertEqual(needs[0].description, "coffee")
        // total = 168; horizon 60 min → walk at m=0,30,60 → 3 iterations, each
        // reads a slot of count 1 → upcoming = 3 → prob = 3/168.
        XCTAssertEqual(needs[0].probability, 3.0 / 168.0, accuracy: 1e-9)
        XCTAssertGreaterThan(needs[0].probability, 0)
    }

    func testHistogramExpectedByIsHorizonHalf() async throws {
        let e = HistogramPredictiveEngine()
        fillAllSlots(e, "lunch")
        let before = Date()
        let needs = try await e.anticipate(horizonMinutes: 120)
        let after = Date()
        XCTAssertEqual(needs.count, 1)
        // ExpectedByUtc = now + (horizon/2) minutes = now + 60 min.
        let lo = before.addingTimeInterval(60 * 60)
        let hi = after.addingTimeInterval(60 * 60)
        XCTAssertGreaterThanOrEqual(needs[0].expectedByUtc, lo.addingTimeInterval(-1))
        XCTAssertLessThanOrEqual(needs[0].expectedByUtc, hi.addingTimeInterval(1))
    }

    func testHistogramDescriptionIsCaseInsensitive() async throws {
        let e = HistogramPredictiveEngine()
        // Same spelling, different case, at every slot → one bucket, display
        // casing is the first-seen "Coffee".
        let base = Date(timeIntervalSince1970: 0)
        for slot in 0..<(24 * 7) {
            let when = base.addingTimeInterval(Double(slot) * 3600)
            e.observe(description: "Coffee", atUtc: when)
            e.observe(description: "coffee", atUtc: when)
        }
        let needs = try await e.anticipate(horizonMinutes: 30)
        XCTAssertEqual(needs.count, 1)
        XCTAssertEqual(needs[0].description, "Coffee")
    }

    // ── SequencePredictiveEngine ────────────────────────────────────────────────

    func testSequenceEmptyReturnsEmpty() async throws {
        let e = SequencePredictiveEngine()
        let needs = try await e.anticipate(horizonMinutes: 120)
        XCTAssertTrue(needs.isEmpty)
    }

    func testSequencePredictsNextFromContext() async throws {
        let e = SequencePredictiveEngine(order: 3)
        // Repeated pattern a → b, small gaps so mean-interval < horizon.
        let t0 = Date(timeIntervalSince1970: 1_000_000)
        e.observe(event: "a", atUtc: t0)
        e.observe(event: "b", atUtc: t0.addingTimeInterval(10))
        e.observe(event: "a", atUtc: t0.addingTimeInterval(20))
        e.observe(event: "b", atUtc: t0.addingTimeInterval(30))
        e.observe(event: "a", atUtc: t0.addingTimeInterval(40))
        // Context now ends in "a"; the model should anticipate "b".
        let needs = try await e.anticipate(horizonMinutes: 60)
        XCTAssertTrue(needs.contains { $0.description == "b" })
        // Probabilities are normalised to sum ≤ 1 (single dominant → ~1).
        let bNeed = needs.first { $0.description == "b" }!
        XCTAssertGreaterThan(bNeed.probability, 0)
        XCTAssertLessThanOrEqual(bNeed.probability, 1.0 + 1e-9)
    }

    func testSequenceProbabilitiesNormaliseAcrossCandidates() async throws {
        let e = SequencePredictiveEngine(order: 2)
        let t0 = Date(timeIntervalSince1970: 2_000_000)
        // From context "x": x→y twice, x→z once. Small gaps for both.
        e.observe(event: "x", atUtc: t0)
        e.observe(event: "y", atUtc: t0.addingTimeInterval(5))
        e.observe(event: "x", atUtc: t0.addingTimeInterval(10))
        e.observe(event: "y", atUtc: t0.addingTimeInterval(15))
        e.observe(event: "x", atUtc: t0.addingTimeInterval(20))
        e.observe(event: "z", atUtc: t0.addingTimeInterval(25))
        e.observe(event: "x", atUtc: t0.addingTimeInterval(30))

        let needs = try await e.anticipate(horizonMinutes: 120)
        let sum = needs.reduce(0.0) { $0 + $1.probability }
        // All candidate probabilities are shares of the total weight.
        XCTAssertLessThanOrEqual(sum, 1.0 + 1e-9)
        if needs.count >= 2 {
            // Descending order by probability.
            for i in 1..<needs.count {
                XCTAssertGreaterThanOrEqual(needs[i - 1].probability, needs[i].probability)
            }
        }
    }

    func testSequenceFiltersEventsBeyondHorizon() async throws {
        let e = SequencePredictiveEngine(order: 2)
        let t0 = Date(timeIntervalSince1970: 3_000_000)
        // Pattern a, b, b, a, b, b, a with a 2-hour gap between each b→b repeat.
        // The self-repeat (last.Event == "b") is what records b's inter-arrival,
        // so mean-interval(b) ≈ 7200s. Trailing context ends in "a", and a→b is
        // the strong transition → b is a candidate, but 7200s > 3600s horizon
        // → it is filtered out as not expected within the window.
        e.observe(event: "a", atUtc: t0)
        e.observe(event: "b", atUtc: t0.addingTimeInterval(60))
        e.observe(event: "b", atUtc: t0.addingTimeInterval(60 + 7200))   // b→b gap 2h
        e.observe(event: "a", atUtc: t0.addingTimeInterval(60 + 7200 + 60))
        e.observe(event: "b", atUtc: t0.addingTimeInterval(60 + 7200 + 120))
        e.observe(event: "b", atUtc: t0.addingTimeInterval(60 + 7200 + 120 + 7200)) // b→b gap 2h
        e.observe(event: "a", atUtc: t0.addingTimeInterval(60 + 7200 + 120 + 7200 + 60))

        let needs = try await e.anticipate(horizonMinutes: 60)
        XCTAssertFalse(needs.contains { $0.description == "b" })
    }

    func testSequenceUsesHalfHorizonWhenNoInterArrival() async throws {
        // A first-time next event (no self-transition recorded) uses
        // horizonSec * 0.5 as its mean interval, which is ≤ horizon → included.
        let e = SequencePredictiveEngine(order: 1)
        let t0 = Date(timeIntervalSince1970: 4_000_000)
        e.observe(event: "start", atUtc: t0)
        e.observe(event: "next", atUtc: t0.addingTimeInterval(1))
        e.observe(event: "start", atUtc: t0.addingTimeInterval(2))
        let needs = try await e.anticipate(horizonMinutes: 120)
        XCTAssertTrue(needs.contains { $0.description == "next" })
    }
}
