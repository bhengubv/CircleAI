// OperatorTests.swift
//
// Exercises the Operator port: DTO Codable, the InMemoryModelOperator lifecycle
// state machine (Pending→Downloading→Loading→Ready), phase-change observer
// fan-out + dispose, apply validation throws, delete/getStatus, and the null
// backends. Mirrors CircleAI.Operator/*.

import XCTest
import Foundation
@testable import CircleAI

final class OperatorTests: XCTestCase {

    private func dep(_ id: String = "m1", ns: String = "default", replicas: Int = 3) -> ModelDeployment {
        ModelDeployment(modelId: id, namespace: ns, replicas: replicas, targetTierLabel: "gpu")
    }

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testModelStatusCodableRoundTrip() throws {
        let s = ModelStatus(modelId: "m", namespace: "n", phase: .ready, readyReplicas: 2, lastError: nil)
        XCTAssertEqual(try JSONDecoder().decode(ModelStatus.self, from: try JSONEncoder().encode(s)), s)
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    func testApplyDrivesToReadyAndRecordsStatus() async throws {
        let op = InMemoryModelOperator()
        try await op.apply(dep("m1", ns: "prod", replicas: 5))
        let status = try await op.getStatus(modelId: "m1", namespace: "prod")
        XCTAssertEqual(status?.phase, .ready)
        XCTAssertEqual(status?.readyReplicas, 5)
        XCTAssertNil(status?.lastError)
    }

    func testObserverSeesEveryPhaseTransition() async throws {
        let op = InMemoryModelOperator()
        let collector = PhaseCollector()
        let sub = op.subscribe { s in await collector.add(s.phase) }
        try await op.apply(dep(replicas: 2))
        let phases = await collector.phases
        XCTAssertEqual(phases, [.pending, .downloading, .loading, .ready])
        sub.dispose()
        // After dispose no more notifications.
        try await op.apply(dep("m2"))
        let after = await collector.phases
        XCTAssertEqual(after.count, 4)  // unchanged
    }

    func testDisposeIsIdempotent() async throws {
        let op = InMemoryModelOperator()
        let sub = op.subscribe { _ in }
        XCTAssertEqual(op.observerCount, 1)
        sub.dispose()
        sub.dispose()
        XCTAssertEqual(op.observerCount, 0)
    }

    func testApplyValidationThrows() async {
        let op = InMemoryModelOperator()
        await assertThrows(op, ModelDeployment(modelId: "  ", namespace: "n", replicas: 1, targetTierLabel: "t"), .modelIdRequired)
        await assertThrows(op, ModelDeployment(modelId: "m", namespace: "", replicas: 1, targetTierLabel: "t"), .namespaceRequired)
        await assertThrows(op, ModelDeployment(modelId: "m", namespace: "n", replicas: -1, targetTierLabel: "t"), .negativeReplicas)
    }

    private func assertThrows(_ op: InMemoryModelOperator, _ d: ModelDeployment, _ expected: OperatorError) async {
        do { try await op.apply(d); XCTFail("expected throw") }
        catch let e as OperatorError { XCTAssertEqual(e, expected) }
        catch { XCTFail("wrong error \(error)") }
    }

    func testDeleteRemovesStatus() async throws {
        let op = InMemoryModelOperator()
        try await op.apply(dep("m1", ns: "n"))
        let beforeDelete = try await op.getStatus(modelId: "m1", namespace: "n")
        XCTAssertNotNil(beforeDelete)
        try await op.delete(modelId: "m1", namespace: "n")
        let afterDelete = try await op.getStatus(modelId: "m1", namespace: "n")
        XCTAssertNil(afterDelete)
    }

    func testStatusIsNamespaced() async throws {
        let op = InMemoryModelOperator()
        try await op.apply(dep("m1", ns: "a", replicas: 1))
        try await op.apply(dep("m1", ns: "b", replicas: 9))
        let statusA = try await op.getStatus(modelId: "m1", namespace: "a")
        XCTAssertEqual(statusA?.readyReplicas, 1)
        let statusB = try await op.getStatus(modelId: "m1", namespace: "b")
        XCTAssertEqual(statusB?.readyReplicas, 9)
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullOperator() async throws {
        XCTAssertEqual(NullModelOperator.instance.backendId, "null")
        try await NullModelOperator.instance.apply(dep())
        let nullStatus = try await NullModelOperator.instance.getStatus(modelId: "m", namespace: "n")
        XCTAssertNil(nullStatus)
    }

    func testNullObserverReturnsNoopHandle() {
        XCTAssertEqual(NullDeploymentObserver.instance.backendId, "null")
        let sub = NullDeploymentObserver.instance.subscribe { _ in }
        sub.dispose()  // no crash
    }

    // Actor collector so observer callbacks accumulate safely.
    private actor PhaseCollector {
        var phases: [ModelLifecyclePhase] = []
        func add(_ p: ModelLifecyclePhase) { phases.append(p) }
    }
}
