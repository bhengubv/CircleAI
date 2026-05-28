// GoalProgressTests.swift
// Validates Goal.advancingProgress(by:) against all vectors in
// fixtures/goal_progress.json.

import XCTest
import Foundation
@testable import CircleAI

final class GoalProgressTests: XCTestCase {

    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/ (repo root)
            .appendingPathComponent("fixtures")
    }()

    private let epsilon: Float = 1e-5

    // ── Fixture-driven ───────────────────────────────────────────────────────

    func testAllProgressVectors() throws {
        let url = fixturesDir.appendingPathComponent("goal_progress.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let vectors = json["vectors"] as! [[String: Any]]

        XCTAssertEqual(vectors.count, 7, "Expected exactly 7 goal progress test vectors")

        for vec in vectors {
            let id               = vec["id"]               as! String
            let initialProgress  = Float(truncating: vec["initial_progress"] as! NSNumber)
            let delta            = Float(truncating: vec["delta"]            as! NSNumber)
            let expectedProgress = Float(truncating: vec["expected_progress"] as! NSNumber)

            let goal = makeGoal(progress: initialProgress)
            let advanced = goal.advancingProgress(by: delta)

            XCTAssertTrue(
                abs(advanced.progress - expectedProgress) < epsilon,
                "[\(id)] expected \(expectedProgress), got \(advanced.progress) (diff=\(abs(advanced.progress - expectedProgress)))"
            )
            // Mutation must not change the original
            XCTAssertEqual(goal.progress, initialProgress, "[\(id)] original Goal must be unchanged")
        }
    }

    // ── Inline unit tests ─────────────────────────────────────────────────────

    func testAdvanceZeroFromZero() {
        let g = makeGoal(progress: 0.0)
        XCTAssertEqual(g.advancingProgress(by: 0.0).progress, 0.0, accuracy: epsilon)
    }

    func testAdvancePartial() {
        let g = makeGoal(progress: 0.0)
        XCTAssertEqual(g.advancingProgress(by: 0.3).progress, 0.3, accuracy: epsilon)
    }

    func testClampMax() {
        let g = makeGoal(progress: 0.9)
        XCTAssertEqual(g.advancingProgress(by: 0.5).progress, 1.0, accuracy: epsilon)
    }

    func testClampMin() {
        let g = makeGoal(progress: 0.1)
        XCTAssertEqual(g.advancingProgress(by: -0.5).progress, 0.0, accuracy: epsilon)
    }

    func testNegativeDeltaWithinRange() {
        let g = makeGoal(progress: 0.6)
        XCTAssertEqual(g.advancingProgress(by: -0.2).progress, 0.4, accuracy: epsilon)
    }

    func testAdvanceToExactlyFull() {
        let g = makeGoal(progress: 0.75)
        XCTAssertEqual(g.advancingProgress(by: 0.25).progress, 1.0, accuracy: epsilon)
    }

    func testZeroDeltaMidProgress() {
        let g = makeGoal(progress: 0.5)
        XCTAssertEqual(g.advancingProgress(by: 0.0).progress, 0.5, accuracy: epsilon)
    }

    func testReturnsCopy() {
        let g = makeGoal(progress: 0.4)
        let advanced = g.advancingProgress(by: 0.3)
        XCTAssertEqual(advanced.progress, 0.7, accuracy: epsilon)
        XCTAssertEqual(g.progress, 0.4, accuracy: epsilon,
                       "advancingProgress(by:) must return a new value, not mutate in place")
    }

    func testProgressInitialClamp() {
        // Goal.init should clamp progress if someone passes an out-of-range value
        let g = Goal(
            id: "g-clamp",
            userId: "u-1",
            title: "Test",
            description: "",
            status: .active,
            priority: .normal,
            createdAt: Date(),
            progress: 1.5
        )
        XCTAssertEqual(g.progress, 1.0, accuracy: epsilon)
    }

    func testProgressInitialClampNegative() {
        let g = Goal(
            id: "g-clamp-neg",
            userId: "u-1",
            title: "Test",
            description: "",
            status: .active,
            priority: .normal,
            createdAt: Date(),
            progress: -0.1
        )
        XCTAssertEqual(g.progress, 0.0, accuracy: epsilon)
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private func makeGoal(progress: Float) -> Goal {
        Goal(
            id: UUID().uuidString,
            userId: "test-user",
            title: "Test Goal",
            description: "A goal used in unit tests",
            status: .active,
            priority: .normal,
            createdAt: Date(),
            progress: progress
        )
    }
}
