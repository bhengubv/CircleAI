// WorkflowsTests.swift
//
// Exercises the Workflows port: the in-memory definition store / runner /
// checkpoint state, the null defaults, and the PacaConversationRuntime state
// machine (queue → start → finished/stopped/failed, step recording, duplicate
// + not-queued guards). Mirrors CircleAI.Workflows/* (named types).

import XCTest
import Foundation
@testable import CircleAI

final class WorkflowsTests: XCTestCase {

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testWorkflowExecutionCodableRoundTrip() throws {
        let e = WorkflowExecution(runId: "r", definitionId: "d", phase: .completed,
                                  startUtc: Date(timeIntervalSince1970: 2), failureReason: nil)
        XCTAssertEqual(try JSONDecoder().decode(WorkflowExecution.self, from: try JSONEncoder().encode(e)), e)
    }

    // ── In-memory workflow ───────────────────────────────────────────────────────

    func testDefinitionStoreUpsertGet() async {
        let store = InMemoryWorkflowDefinitionStore()
        await store.upsert(WorkflowDefinition(definitionId: "wf", name: "Flow", version: "1", description: "d"))
        let found = await store.get("wf")
        XCTAssertEqual(found?.name, "Flow")
        let missing = await store.get("missing")
        XCTAssertNil(missing)
    }

    func testRunnerStartGetCancel() async {
        let runner = InMemoryWorkflowRunner()
        let exec = await runner.start("wf", inputs: ["k": AnyCodable(1)])
        XCTAssertEqual(exec.phase, .completed)
        XCTAssertEqual(exec.runId, "wf-1")
        let running = await runner.get("wf-1")
        XCTAssertEqual(running?.definitionId, "wf")
        await runner.cancel("wf-1")
        let cancelled = await runner.get("wf-1")
        XCTAssertEqual(cancelled?.phase, .failed)
    }

    func testRunnerDefaultInputsOverload() async {
        let runner = InMemoryWorkflowRunner()
        let exec = await runner.start("wf")
        XCTAssertEqual(exec.definitionId, "wf")
    }

    func testWorkflowStateCheckpointLoad() async {
        let state = InMemoryWorkflowState()
        let cp = CheckpointPayload(runId: "r", stepId: "s1", stateBlob: Data([1, 2]))
        await state.checkpoint(cp)
        let loaded = await state.load(runId: "r", stepId: "s1")
        XCTAssertEqual(loaded, cp)
        let missing = await state.load(runId: "r", stepId: "other")
        XCTAssertNil(missing)
    }

    // ── Null workflow ────────────────────────────────────────────────────────────

    func testNullWorkflowBackends() async {
        await NullWorkflowDefinitionStore.instance.upsert(
            WorkflowDefinition(definitionId: "d", name: "n", version: "1", description: ""))
        let nullDef = await NullWorkflowDefinitionStore.instance.get("d")
        XCTAssertNil(nullDef)
        let run = await NullWorkflowRunner.instance.start("wf")
        XCTAssertEqual(run.phase, .failed)
        XCTAssertEqual(run.runId, "00000000-0000-0000-0000-000000000000")
        await NullWorkflowState.instance.checkpoint(CheckpointPayload(runId: "r", stepId: "s", stateBlob: Data()))
        let nullState = await NullWorkflowState.instance.load(runId: "r", stepId: "s")
        XCTAssertNil(nullState)
    }

    // ── Conversation runtime ───────────────────────────────────────────────────────

    func testConversationHappyPathFinishes() async throws {
        let exec = ScriptedExecutor(steps: [
            ConversationStep(conversationId: "c1", order: 0, speaker: "agent", contentJson: "{}", at: Date())
        ])
        let rt = PacaConversationRuntime(executor: exec)
        let queued = try rt.queue(id: "c1", projectId: "p", agentMemberId: "agent", openingPrompt: "hi")
        XCTAssertEqual(queued.state, .queued)
        try await rt.start("c1", permissions: ConversationPermissions(allowCloneRepos: false, allowCreatePr: false))
        let final = rt.get("c1")
        XCTAssertEqual(final?.state, .finished)
        XCTAssertEqual(final?.resultJson, "{}")
        XCTAssertEqual(rt.stepsFor("c1").count, 1)
    }

    func testConversationFailureCapturesReason() async throws {
        let exec = ThrowingExecutor()
        let rt = PacaConversationRuntime(executor: exec)
        _ = try rt.queue(id: "c1", projectId: "p", agentMemberId: "agent", openingPrompt: "hi")
        try await rt.start("c1", permissions: ConversationPermissions(allowCloneRepos: false, allowCreatePr: false))
        XCTAssertEqual(rt.get("c1")?.state, .failed)
        XCTAssertNotNil(rt.get("c1")?.failureReason)
    }

    func testConversationStopBeforeExecutorReturnsMarksStopped() async throws {
        // Executor observes the token and returns cleanly once cancelled.
        let exec = CancelAwareExecutor()
        let rt = PacaConversationRuntime(executor: exec)
        _ = try rt.queue(id: "c1", projectId: "p", agentMemberId: "agent", openingPrompt: "hi")
        exec.onStarted = { rt.stop("c1") }
        try await rt.start("c1", permissions: ConversationPermissions(allowCloneRepos: false, allowCreatePr: false))
        XCTAssertEqual(rt.get("c1")?.state, .stopped)
    }

    func testQueueDuplicateThrows() throws {
        let rt = PacaConversationRuntime(executor: ScriptedExecutor(steps: []))
        _ = try rt.queue(id: "c1", projectId: "p", agentMemberId: "a", openingPrompt: "")
        XCTAssertThrowsError(try rt.queue(id: "c1", projectId: "p", agentMemberId: "a", openingPrompt: "")) { e in
            XCTAssertEqual(e as? ConversationError, .alreadyExists("c1"))
        }
    }

    func testStartNonQueuedThrows() async throws {
        let rt = PacaConversationRuntime(executor: ScriptedExecutor(steps: []))
        do {
            try await rt.start("ghost", permissions: ConversationPermissions(allowCloneRepos: false, allowCreatePr: false))
            XCTFail("expected throw")
        } catch let e as ConversationError {
            XCTAssertEqual(e, .notQueued("ghost"))
        } catch { XCTFail("wrong error \(error)") }
    }

    // ── Executors ──────────────────────────────────────────────────────────────

    private final class ScriptedExecutor: IConversationExecutor, @unchecked Sendable {
        let steps: [ConversationStep]
        init(steps: [ConversationStep]) { self.steps = steps }
        func run(conversation: AgentConversation, permissions: ConversationPermissions,
                 onStep: @escaping @Sendable (ConversationStep) -> Void,
                 token: ConversationCancellationToken) async throws {
            for s in steps { onStep(s) }
        }
    }

    private final class ThrowingExecutor: IConversationExecutor, @unchecked Sendable {
        struct Boom: Error {}
        func run(conversation: AgentConversation, permissions: ConversationPermissions,
                 onStep: @escaping @Sendable (ConversationStep) -> Void,
                 token: ConversationCancellationToken) async throws {
            throw Boom()
        }
    }

    private final class CancelAwareExecutor: IConversationExecutor, @unchecked Sendable {
        var onStarted: (@Sendable () -> Void)?
        func run(conversation: AgentConversation, permissions: ConversationPermissions,
                 onStep: @escaping @Sendable (ConversationStep) -> Void,
                 token: ConversationCancellationToken) async throws {
            onStarted?()
            // Observe the token — return cleanly once asked to stop.
            if token.isCancellationRequested { return }
        }
    }
}
