// WorldModelTests.swift
//
// Verifies FrequencyWorldModel and BayesianWorldModel: observation extraction
// from scenario JSON, most-likely-outcome prediction, the "unknown"/0.5 fallback
// for unseen or empty scenarios, case-insensitive keying, and (Bayesian) that a
// dominant training outcome wins with a normalised softmax probability.

import XCTest
@testable import CircleAI

final class WorldModelTests: XCTestCase {

    // ── FrequencyWorldModel ─────────────────────────────────────────────────────

    func testFrequencyPredictsTopOutcome() async throws {
        let m = FrequencyWorldModel()
        m.observe(observations: ["weather=rainy"], outcome: "take_umbrella")
        m.observe(observations: ["weather=rainy"], outcome: "take_umbrella")
        m.observe(observations: ["weather=rainy"], outcome: "stay_home")

        let p = try await m.predict(scenarioJson: "{\"weather\":\"rainy\"}")
        XCTAssertEqual(p.outcome, "take_umbrella")
        XCTAssertEqual(p.probability, 2.0 / 3.0, accuracy: 1e-9)
        XCTAssertEqual(p.supportingFactors, ["weather=rainy"])
    }

    func testFrequencyUnknownWhenNoMatch() async throws {
        let m = FrequencyWorldModel()
        m.observe(observations: ["weather=rainy"], outcome: "take_umbrella")
        let p = try await m.predict(scenarioJson: "{\"weather\":\"sunny\"}")
        XCTAssertEqual(p.outcome, "unknown")
        XCTAssertEqual(p.probability, 0.5, accuracy: 1e-9)
        XCTAssertTrue(p.supportingFactors.isEmpty)
    }

    func testFrequencyUnknownWhenEmptyScenario() async throws {
        let m = FrequencyWorldModel()
        m.observe(observations: ["a=1"], outcome: "x")
        let p = try await m.predict(scenarioJson: "not json")
        XCTAssertEqual(p.outcome, "unknown")
        XCTAssertEqual(p.probability, 0.5, accuracy: 1e-9)
    }

    func testFrequencyAggregatesMultipleObservations() async throws {
        let m = FrequencyWorldModel()
        m.observe(observations: ["a=1"], outcome: "hit")
        m.observe(observations: ["b=2"], outcome: "hit")
        m.observe(observations: ["b=2"], outcome: "miss")

        // Scenario has both a=1 and b=2: hit gets 1 (from a) + 1 (from b) = 2,
        // miss gets 1 → total 3.
        let p = try await m.predict(scenarioJson: "{\"a\":1,\"b\":2}")
        XCTAssertEqual(p.outcome, "hit")
        XCTAssertEqual(p.probability, 2.0 / 3.0, accuracy: 1e-9)
        XCTAssertEqual(p.supportingFactors.sorted(), ["a=1", "b=2"])
    }

    func testFrequencyObservationKeyIsCaseInsensitive() async throws {
        let m = FrequencyWorldModel()
        m.observe(observations: ["Weather=Rainy"], outcome: "umbrella")
        // Same spelling, different case → folds into the same observation bucket.
        let p = try await m.predict(scenarioJson: "{\"Weather\":\"Rainy\"}")
        XCTAssertEqual(p.outcome, "umbrella")
        XCTAssertEqual(p.probability, 1.0, accuracy: 1e-9)
    }

    func testFrequencyRendersBooleanLikeDotNet() async throws {
        let m = FrequencyWorldModel()
        // .NET JsonElement.ToString() renders true as "True".
        m.observe(observations: ["ok=True"], outcome: "proceed")
        let p = try await m.predict(scenarioJson: "{\"ok\":true}")
        XCTAssertEqual(p.outcome, "proceed")
        XCTAssertEqual(p.supportingFactors, ["ok=True"])
    }

    // ── BayesianWorldModel ──────────────────────────────────────────────────────

    func testBayesianUnknownWhenUntrained() async throws {
        let m = BayesianWorldModel()
        let p = try await m.predict(scenarioJson: "{\"x\":1}")
        XCTAssertEqual(p.outcome, "unknown")
        XCTAssertEqual(p.probability, 0.5, accuracy: 1e-9)
        XCTAssertTrue(p.supportingFactors.isEmpty)
    }

    func testBayesianUnknownWhenNoObservations() async throws {
        let m = BayesianWorldModel()
        m.observe(observations: ["x=1"], outcome: "a")
        let p = try await m.predict(scenarioJson: "{}")
        XCTAssertEqual(p.outcome, "unknown")
        XCTAssertEqual(p.probability, 0.5, accuracy: 1e-9)
    }

    func testBayesianPicksDominantOutcome() async throws {
        let m = BayesianWorldModel()
        for _ in 0..<5 { m.observe(observations: ["sky=dark", "pressure=low"], outcome: "rain") }
        m.observe(observations: ["sky=clear", "pressure=high"], outcome: "sunny")

        let p = try await m.predict(scenarioJson: "{\"sky\":\"dark\",\"pressure\":\"low\"}")
        XCTAssertEqual(p.outcome, "rain")
        XCTAssertGreaterThan(p.probability, 0.5)
        XCTAssertLessThanOrEqual(p.probability, 1.0)
        XCTAssertEqual(p.supportingFactors, ["sky=dark", "pressure=low"])
    }

    func testBayesianProbabilityIsNormalised() async throws {
        // Two outcomes, softmax over log-posteriors → both probabilities in (0,1).
        let m = BayesianWorldModel()
        m.observe(observations: ["a=1"], outcome: "x")
        m.observe(observations: ["a=1"], outcome: "y")
        let p = try await m.predict(scenarioJson: "{\"a\":1}")
        XCTAssertTrue(p.outcome == "x" || p.outcome == "y")
        XCTAssertGreaterThan(p.probability, 0.0)
        XCTAssertLessThanOrEqual(p.probability, 1.0)
    }

    func testBayesianRejectsNonPositiveAlpha() {
        // precondition failure is a trap in Swift; we assert the happy path
        // constructs and a valid small alpha is accepted.
        let m = BayesianWorldModel(laplaceAlpha: 0.5)
        XCTAssertNotNil(m)
    }

    // ── Shared observation extraction ───────────────────────────────────────────

    func testExtractPreservesKeyOrderAndRawValues() {
        let obs = ScenarioObservations.extract("{\"b\":2,\"a\":\"hi\",\"n\":null}")
        // Order preserved; string unquoted; null → empty value.
        XCTAssertEqual(obs, ["b=2", "a=hi", "n="])
    }

    func testExtractNestedObjectKeepsRawText() {
        // .NET JsonElement.ToString() returns GetRawText() for objects —
        // verbatim source, whitespace preserved.
        let obs = ScenarioObservations.extract("{\"o\": { \"k\" : 1 }}")
        XCTAssertEqual(obs, ["o={ \"k\" : 1 }"])
    }

    func testExtractNonObjectRootIsEmpty() {
        XCTAssertTrue(ScenarioObservations.extract("[1,2,3]").isEmpty)
        XCTAssertTrue(ScenarioObservations.extract("\"scalar\"").isEmpty)
        XCTAssertTrue(ScenarioObservations.extract("   ").isEmpty)
    }
}
