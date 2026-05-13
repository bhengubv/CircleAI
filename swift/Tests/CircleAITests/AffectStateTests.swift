// AffectStateTests.swift
// Iterates all 12 vectors from fixtures/affect_state.json and asserts
// each dimension within epsilon 1e-5.

import XCTest
import Foundation
@testable import CircleAI

final class AffectStateTests: XCTestCase {

    // Navigate from the compile-time path of THIS file up to the fixtures directory.
    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/  (repo root)
            .appendingPathComponent("fixtures")
    }()

    private let epsilon: Float = 1e-5

    // ── Helpers ─────────────────────────────────────────────────────────────

    private func makeState(from dict: [String: Any]) -> AffectState {
        let s = AffectState(userId: "test")
        s.curiosity   = Float(truncating: dict["curiosity"]   as! NSNumber)
        s.engagement  = Float(truncating: dict["engagement"]  as! NSNumber)
        s.uncertainty = Float(truncating: dict["uncertainty"] as! NSNumber)
        s.rapport     = Float(truncating: dict["rapport"]     as! NSNumber)
        s.energy      = Float(truncating: dict["energy"]      as! NSNumber)
        return s
    }

    private func assertNear(_ actual: Float, _ expected: Float, _ label: String, file: StaticString = #file, line: UInt = #line) {
        XCTAssertTrue(
            abs(actual - expected) < epsilon,
            "\(label): expected \(expected), got \(actual) (diff=\(abs(actual - expected)))",
            file: file, line: line
        )
    }

    private func assertFields(
        _ state: AffectState,
        _ exp: [String: Any],
        id: String,
        file: StaticString = #file,
        line: UInt = #line
    ) {
        let ec = Float(truncating: exp["curiosity"]   as! NSNumber)
        let ee = Float(truncating: exp["engagement"]  as! NSNumber)
        let eu = Float(truncating: exp["uncertainty"] as! NSNumber)
        let er = Float(truncating: exp["rapport"]     as! NSNumber)
        let en = Float(truncating: exp["energy"]      as! NSNumber)
        assertNear(state.curiosity,   ec, "\(id).curiosity",   file: file, line: line)
        assertNear(state.engagement,  ee, "\(id).engagement",  file: file, line: line)
        assertNear(state.uncertainty, eu, "\(id).uncertainty", file: file, line: line)
        assertNear(state.rapport,     er, "\(id).rapport",     file: file, line: line)
        assertNear(state.energy,      en, "\(id).energy",      file: file, line: line)
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    func testAllVectors() throws {
        let url = fixturesDir.appendingPathComponent("affect_state.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]

        guard let vectors = json["vectors"] as? [[String: Any]] else {
            XCTFail("No vectors in affect_state.json")
            return
        }

        XCTAssertEqual(vectors.count, 12, "Expected exactly 12 test vectors")

        for vec in vectors {
            let id        = vec["id"]        as! String
            let op        = vec["operation"] as! String
            let input     = vec["input"]     as! [String: Any]
            let param     = vec["operationParam"] as? [String: Any] ?? [:]
            let expected  = vec["expected"]  as! [String: Any]

            let state = makeState(from: input)

            switch op {
            case "positive_signal":
                let count = param["count"] as? Int ?? 1
                for _ in 0 ..< count { state.applyPositiveSignal() }

            case "negative_signal":
                let count = param["count"] as? Int ?? 1
                for _ in 0 ..< count { state.applyNegativeSignal() }

            case "positive_then_negative":
                state.applyPositiveSignal()
                state.applyNegativeSignal()

            case "negative_then_positive":
                state.applyNegativeSignal()
                state.applyPositiveSignal()

            case "idle_decay":
                let hours = Double(truncating: param["hours"] as! NSNumber)
                state.applyIdleDecay(idle: hours * 3600.0)

            default:
                XCTFail("Unknown operation '\(op)' in vector '\(id)'")
                continue
            }

            assertFields(state, expected, id: id)
        }
    }

    func testDefaultState() {
        let s = AffectState()
        XCTAssertEqual(s.userId, "default")
        assertNear(s.curiosity,   0.5, "default.curiosity")
        assertNear(s.engagement,  0.5, "default.engagement")
        assertNear(s.uncertainty, 0.2, "default.uncertainty")
        assertNear(s.rapport,     0.0, "default.rapport")
        assertNear(s.energy,      0.5, "default.energy")
    }

    func testToSystemPromptHintEmpty() {
        // All neutral — hint should be empty
        let s = AffectState()
        XCTAssertEqual(s.toSystemPromptHint(), "")
    }

    func testToSystemPromptHintHighCuriosity() {
        let s = AffectState()
        s.curiosity = 0.8
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.hasPrefix("[Affect state]\n"))
        XCTAssertTrue(hint.contains("deeply curious"))
    }

    func testToSystemPromptHintHighRapport() {
        let s = AffectState()
        s.rapport = 0.75
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.contains("warm, familiar tone"))
    }

    func testToSystemPromptHintLowEngagement() {
        let s = AffectState()
        s.engagement = 0.2
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.contains("brief and to the point"))
    }

    func testToSystemPromptHintHighEnergy() {
        let s = AffectState()
        s.energy = 0.9
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.contains("energetic"))
    }

    func testToSystemPromptHintLowEnergy() {
        let s = AffectState()
        s.energy = 0.2
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.contains("calm and measured"))
    }

    func testToSystemPromptHintHighUncertainty() {
        let s = AffectState()
        s.uncertainty = 0.7
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.contains("clarifying question"))
    }

    func testToSystemPromptHintHighEngagement() {
        let s = AffectState()
        s.engagement = 0.8
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.contains("fully engaged"))
    }

    func testToSystemPromptHintTrailingNewline() {
        let s = AffectState()
        s.curiosity = 0.8
        let hint = s.toSystemPromptHint()
        XCTAssertTrue(hint.hasSuffix("\n"))
    }
}
