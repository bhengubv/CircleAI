// TheoryOfMindTests.swift
//
// Verifies BeliefTrackerTheoryOfMind: belief-verb extraction, positional decay,
// believe-vs-other weighting, key accumulation, insertion-ordered JSON output,
// integer-valued weights rendered without a fractional part (".NET JSON" style),
// and the min(1, Σ/5) confidence. Ground-truth values were cross-checked against
// the C# reference implementation.

import XCTest
@testable import CircleAI

final class TheoryOfMindTests: XCTestCase {

    private let tom = BeliefTrackerTheoryOfMind()

    /// Parses the LikelyBeliefJson object into an ordered list of (key, value)
    /// preserving declaration order (JSONSerialization drops order, so read the
    /// object shell ourselves via the shared ordered reader is overkill here —
    /// we validate order separately). Returns a plain dictionary for value checks.
    private func parse(_ json: String) -> [String: Double] {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return [:]
        }
        var out: [String: Double] = [:]
        for (k, v) in obj {
            if let n = v as? NSNumber { out[k] = n.doubleValue }
        }
        return out
    }

    func testExtractsWeightedBeliefs() async throws {
        let history = "She believes the project will succeed. He thinks it might fail. She wants more time."
        let est = try await tom.estimate(target: "user", interactionHistoryJson: history)
        XCTAssertEqual(est.targetIdentifier, "user")

        let m = parse(est.likelyBeliefJson)
        XCTAssertEqual(m.count, 3)
        // idx 0, believe → weight 1.0, decay 1.0 → 1.0
        XCTAssertEqual(m["believes:the project will succeed"]!, 1.0, accuracy: 1e-12)
        // idx 1, thinks → weight 0.7, decay 1/1.1 → 0.6363636363636364
        XCTAssertEqual(m["thinks:it might fail"]!, 0.7 / 1.1, accuracy: 1e-12)
        // idx 2, wants → weight 0.7, decay 1/1.2 → 0.5833333333333334
        XCTAssertEqual(m["wants:more time"]!, 0.7 / 1.2, accuracy: 1e-12)

        // Confidence = min(1, sum/5).
        let sum = 1.0 + 0.7 / 1.1 + 0.7 / 1.2
        XCTAssertEqual(est.confidence, min(1.0, sum / 5.0), accuracy: 1e-12)
    }

    func testEmptyWhenNoBeliefVerbs() async throws {
        let est = try await tom.estimate(target: "t", interactionHistoryJson: "nothing relevant here at all")
        XCTAssertEqual(est.likelyBeliefJson, "{}")
        XCTAssertEqual(est.confidence, 0.0, accuracy: 1e-12)
    }

    func testAccumulatesRepeatedKey() async throws {
        // "He believes X. He believes X." → one key, weights 1.0 + 1.0/1.1.
        let est = try await tom.estimate(target: "t", interactionHistoryJson: "He believes X. He believes X.")
        let m = parse(est.likelyBeliefJson)
        XCTAssertEqual(m.count, 1)
        XCTAssertEqual(m["believes:X"]!, 1.0 + 1.0 / 1.1, accuracy: 1e-12)
    }

    func testIntegerWeightRendersWithoutFraction() async throws {
        // A single believe-match at idx 0 → weight exactly 1.0 → JSON "1", not "1.0".
        let est = try await tom.estimate(target: "t", interactionHistoryJson: "She believes it will rain.")
        XCTAssertEqual(est.likelyBeliefJson, "{\"believes:it will rain\":1}")
    }

    func testJsonKeyOrderIsInsertionOrder() async throws {
        let history = "Alice thinks she will win! Bob fears the storm; Carol hopes for rain."
        let est = try await tom.estimate(target: "t", interactionHistoryJson: history)
        // Keys must appear in first-seen order: thinks, fears, hopes.
        let json = est.likelyBeliefJson
        let iThinks = json.range(of: "\"thinks:she will win\"")!.lowerBound
        let iFears = json.range(of: "\"fears:the storm\"")!.lowerBound
        let iHopes = json.range(of: "\"hopes:for rain\"")!.lowerBound
        XCTAssertTrue(iThinks < iFears)
        XCTAssertTrue(iFears < iHopes)

        let m = parse(json)
        XCTAssertEqual(m["thinks:she will win"]!, 0.7, accuracy: 1e-12)      // idx0
        XCTAssertEqual(m["fears:the storm"]!, 0.7 / 1.1, accuracy: 1e-12)     // idx1
        XCTAssertEqual(m["hopes:for rain"]!, 0.7 / 1.2, accuracy: 1e-12)      // idx2
    }

    func testStopsClaimAtSentenceTerminators() async throws {
        // The claim group [^.;!?]+ stops before . ; ! ? — so the claim excludes
        // whatever follows the terminator.
        let est = try await tom.estimate(target: "t", interactionHistoryJson: "He wants coffee. Then he left.")
        let m = parse(est.likelyBeliefJson)
        XCTAssertEqual(m.count, 1)
        XCTAssertNotNil(m["wants:coffee"])
    }

    func testCaseInsensitiveVerbMatch() async throws {
        let est = try await tom.estimate(target: "t", interactionHistoryJson: "She BELIEVES it works")
        let m = parse(est.likelyBeliefJson)
        // Verb is lowercased in the key regardless of source casing.
        XCTAssertNotNil(m["believes:it works"])
    }

    func testConfidenceCappedAtOne() async throws {
        // Many strong beliefs → sum/5 exceeds 1 → capped at 1.0.
        let history = String(repeating: "She believes A; He believes B; They believe C; I believe D; We believe E. ",
                             count: 3)
        let est = try await tom.estimate(target: "t", interactionHistoryJson: history)
        XCTAssertLessThanOrEqual(est.confidence, 1.0)
        XCTAssertGreaterThan(est.confidence, 0.9)
    }

    // ── serialisation unit ──────────────────────────────────────────────────────

    func testSerialiseFormatsIntegersAndDecimals() {
        let json = BeliefTrackerTheoryOfMind.serialise(
            order: ["a", "b"],
            weights: ["a": 1.0, "b": 0.5])
        XCTAssertEqual(json, "{\"a\":1,\"b\":0.5}")
    }

    func testSerialiseEscapesKeysLikeDotNetDefaultEncoder() {
        // System.Text.Json's default encoder escapes " as " (not \"),
        // and & < > ' + ` as \uXXXX (uppercase hex). Verified against .NET 8.
        let json = BeliefTrackerTheoryOfMind.serialise(
            order: ["a\"&<>'+`b"],
            weights: ["a\"&<>'+`b": 2.0])
        XCTAssertEqual(json, "{\"a\\u0022\\u0026\\u003C\\u003E\\u0027\\u002B\\u0060b\":2}")
    }

    func testSerialiseEscapesBackslashAndControls() {
        let json = BeliefTrackerTheoryOfMind.serialise(
            order: ["tab\tback\\slash"],
            weights: ["tab\tback\\slash": 1.0])
        XCTAssertEqual(json, "{\"tab\\tback\\\\slash\":1}")
    }
}
