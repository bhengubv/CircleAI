// OrchestrationTests.swift
//
// Exercises the Orchestration port: AgentTask.create stamping, AgentSwarmConfig
// defaults + forDevice sizing, LocalAgentDispatcher routing (handler hit,
// missing-handler blocked, disposed), and the quality gate's [CRITICAL]/[HIGH]
// blocker classification. Mirrors CircleAI.Orchestration/*.

import XCTest
import Foundation
@testable import CircleAI

final class OrchestrationTests: XCTestCase {

    // ── AgentTask ─────────────────────────────────────────────────────────────

    func testAgentTaskCreateStampsIdAndTime() {
        let before = Date()
        let t = AgentTask.create(role: .engineering, description: "fix", priority: .high,
                                 inputs: ["file": "a.swift"])
        XCTAssertEqual(t.role, .engineering)
        XCTAssertEqual(t.priority, .high)
        XCTAssertEqual(t.inputs["file"], "a.swift")
        XCTAssertGreaterThanOrEqual(t.createdAt.timeIntervalSince1970, before.timeIntervalSince1970)
    }

    func testAgentTaskCreateDefaultsEmptyInputs() {
        let t = AgentTask.create(role: .review, description: "d", priority: .normal)
        XCTAssertTrue(t.inputs.isEmpty)
    }

    func testAgentTaskCodableRoundTrip() throws {
        let t = AgentTask(id: UUID(), role: .security, description: "d", priority: .critical,
                          inputs: ["k": "v"], createdAt: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(AgentTask.self, from: try JSONEncoder().encode(t)), t)
    }

    // ── AgentSwarmConfig ───────────────────────────────────────────────────────

    func testSwarmConfigDefault() {
        let c = AgentSwarmConfig.default
        XCTAssertEqual(c.maxConcurrency, 4)
        XCTAssertEqual(c.taskTimeout, 300)
        XCTAssertTrue(c.requireReviewPassBeforeDeploy)
        XCTAssertTrue(c.requireSecurityPassBeforeDeploy)
    }

    func testSwarmConfigForDeviceSizesConcurrency() {
        // A wearable-class probe → concurrency 1.
        let wearable = DeviceProbe(ramAvailableBytes: 1 * 1024 * 1024 * 1024, storageFreeBytes: 0,
                                   cpuCores: 4, thermalClass: .sealed)
        XCTAssertEqual(AgentSwarmConfig.forDevice(wearable).maxConcurrency, 1)
        // A workstation-class probe (>=32 GiB) → min(16, cores-2).
        let workstation = DeviceProbe(ramAvailableBytes: 64 * 1024 * 1024 * 1024, storageFreeBytes: 0,
                                      cpuCores: 12)
        XCTAssertEqual(AgentSwarmConfig.forDevice(workstation).maxConcurrency, 10)
    }

    // ── LocalAgentDispatcher ───────────────────────────────────────────────────

    func testDispatchRoutesToRegisteredHandler() async {
        let d = LocalAgentDispatcher()
        d.registerHandler(.engineering) { task in
            SwarmResult(taskId: task.id, role: .engineering, status: .passed,
                        output: "done \(task.description)", issues: [], completedAt: Date())
        }
        let task = AgentTask.create(role: .engineering, description: "build", priority: .normal)
        let result = await d.dispatch(task)
        XCTAssertEqual(result.status, .passed)
        XCTAssertEqual(result.output, "done build")
        XCTAssertEqual(result.taskId, task.id)
    }

    func testDispatchMissingHandlerReturnsBlocked() async {
        let d = LocalAgentDispatcher()
        let task = AgentTask.create(role: .operations, description: "deploy", priority: .high)
        let result = await d.dispatch(task)
        XCTAssertEqual(result.status, .blocked)
        XCTAssertTrue(result.output.contains("No handler registered"))
        XCTAssertEqual(result.issues.count, 1)
    }

    func testDispatchAfterDisposeThrows() async {
        let d = LocalAgentDispatcher()
        d.dispose()
        let task = AgentTask.create(role: .engineering, description: "x", priority: .low)
        do {
            _ = try await d.dispatchThrowing(task)
            XCTFail("expected throw")
        } catch let e as AgentDispatcherError {
            XCTAssertEqual(e, .disposed)
        } catch { XCTFail("wrong error \(error)") }
        // Non-throwing dispatch surfaces disposed as a blocked result.
        let blocked = await d.dispatch(task)
        XCTAssertEqual(blocked.status, .blocked)
    }

    // ── Quality gate ───────────────────────────────────────────────────────────

    func testQualityGateClassifiesBlockers() async {
        let d = LocalAgentDispatcher()
        let result = SwarmResult(taskId: UUID(), role: .review, status: .failed, output: "",
                                 issues: ["[CRITICAL] npm audit high", "[high] weak crypto",
                                          "style: rename var"],
                                 completedAt: Date())
        let gate = await d.runQualityGate(result)
        XCTAssertFalse(gate.passed)
        XCTAssertEqual(gate.blockers.count, 2)  // CRITICAL + high (case-insensitive)
        XCTAssertEqual(gate.warnings, ["style: rename var"])
    }

    func testQualityGatePassesWithNoBlockers() async {
        let d = LocalAgentDispatcher()
        let result = SwarmResult(taskId: UUID(), role: .review, status: .passed, output: "",
                                 issues: ["info: 2 nits"], completedAt: Date())
        let gate = await d.runQualityGate(result)
        XCTAssertTrue(gate.passed)
        XCTAssertTrue(gate.blockers.isEmpty)
        XCTAssertEqual(gate.warnings, ["info: 2 nits"])
    }
}
