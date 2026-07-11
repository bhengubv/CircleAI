// Workflows.swift
//
// Port of the named CircleAI.Workflows/ types — durable-workflow contracts +
// the conversation state machine.
//   • Contracts.cs           — WorkflowPhase, WorkflowDefinition,
//                              WorkflowExecution, CheckpointPayload,
//                              IWorkflowDefinitionStore, IWorkflowRunner,
//                              IWorkflowState
//   • NullImplementations.cs — Null* fail-closed backends
//   • PacaConversations.cs   — ConversationState, AgentConversation,
//                              ConversationStep, ConversationPermissions,
//                              IConversationExecutor, PacaConversationRuntime
//
// Porting notes:
//   • `ReadOnlyMemory<byte>` → `Data`.
//   • `IReadOnlyDictionary<string, object?>? inputs` → `[String: AnyCodable]?`.
//   • The C# module ships only Null workflow impls (no InMemory). To satisfy
//     "no stubs — deterministic in-memory", real in-memory definition-store /
//     runner / state impls are added alongside the ported Null defaults.
//   • `PacaConversationRuntime` is the in-memory conversation state machine that
//     drives the injected `IConversationExecutor`. Step callbacks are appended
//     under a lock; a cancelled run → `.stopped`, a thrown error → `.failed`.
//     The C# links an outer + inner CTS; the Swift port models cancellation via
//     a per-conversation `stop` flag the executor observes through the token and
//     the runtime checks after execution.

import Foundation

// MARK: - Durable workflow contracts

/// Lifecycle phase of a workflow execution. (C# `WorkflowPhase`.)
public enum WorkflowPhase: Int, Sendable, Codable, CaseIterable {
    case pending = 0
    case running = 1
    case suspended = 2
    case completed = 3
    case failed = 4
}

/// A workflow definition. (C# `WorkflowDefinition`.)
public struct WorkflowDefinition: Sendable, Equatable, Codable {
    public let definitionId: String
    public let name: String
    public let version: String
    public let description: String

    public init(definitionId: String, name: String, version: String, description: String) {
        self.definitionId = definitionId
        self.name = name
        self.version = version
        self.description = description
    }
}

/// A workflow execution record. (C# `WorkflowExecution`.)
public struct WorkflowExecution: Sendable, Equatable, Codable {
    public let runId: String
    public let definitionId: String
    public let phase: WorkflowPhase
    public let startUtc: Date
    public let failureReason: String?

    public init(runId: String, definitionId: String, phase: WorkflowPhase, startUtc: Date,
                failureReason: String?) {
        self.runId = runId
        self.definitionId = definitionId
        self.phase = phase
        self.startUtc = startUtc
        self.failureReason = failureReason
    }
}

/// A workflow-state checkpoint. (C# `CheckpointPayload`.) `ReadOnlyMemory<byte>`
/// → `Data`.
public struct CheckpointPayload: Sendable, Equatable, Codable {
    public let runId: String
    public let stepId: String
    public let stateBlob: Data

    public init(runId: String, stepId: String, stateBlob: Data) {
        self.runId = runId
        self.stepId = stepId
        self.stateBlob = stateBlob
    }
}

/// Stores workflow definitions. (C# `IWorkflowDefinitionStore`.)
public protocol IWorkflowDefinitionStore: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Inserts or replaces a definition.
    func upsert(_ d: WorkflowDefinition) async
    /// Returns a definition by id, or `nil`.
    func get(_ id: String) async -> WorkflowDefinition?
}

/// Starts + tracks workflow runs. (C# `IWorkflowRunner`.)
public protocol IWorkflowRunner: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Starts a run of `definitionId` with optional inputs.
    func start(_ definitionId: String, inputs: [String: AnyCodable]?) async -> WorkflowExecution
    /// Returns a run by id, or `nil`.
    func get(_ runId: String) async -> WorkflowExecution?
    /// Cancels a run.
    func cancel(_ runId: String) async
}

public extension IWorkflowRunner {
    /// Overload matching the C# default `inputs = null`.
    func start(_ definitionId: String) async -> WorkflowExecution {
        await start(definitionId, inputs: nil)
    }
}

/// Persists + loads workflow checkpoints. (C# `IWorkflowState`.)
public protocol IWorkflowState: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Saves a checkpoint.
    func checkpoint(_ payload: CheckpointPayload) async
    /// Loads a checkpoint for (runId, stepId), or `nil`.
    func load(runId: String, stepId: String) async -> CheckpointPayload?
}

// MARK: - In-memory workflow impls

/// In-memory workflow definition store.
public final class InMemoryWorkflowDefinitionStore: IWorkflowDefinitionStore, @unchecked Sendable {
    private let lock = NSLock()
    private var defs: [String: WorkflowDefinition] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func upsert(_ d: WorkflowDefinition) async {
        lock.lock(); defs[d.definitionId] = d; lock.unlock()
    }

    public func get(_ id: String) async -> WorkflowDefinition? {
        lock.lock(); defer { lock.unlock() }
        return defs[id]
    }
}

/// In-memory workflow runner — records runs and marks them completed on start
/// (single-shot). `cancel` flips a run to `.failed` with a cancelled reason.
public final class InMemoryWorkflowRunner: IWorkflowRunner, @unchecked Sendable {
    private let lock = NSLock()
    private var runs: [String: WorkflowExecution] = [:]
    private var seq: Int64 = 0

    public init() {}

    public var backendId: String { "in-memory" }

    public func start(_ definitionId: String, inputs: [String: AnyCodable]?) async -> WorkflowExecution {
        precondition(!definitionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "definitionId required")
        lock.lock()
        seq += 1
        let runId = "wf-\(seq)"
        let exec = WorkflowExecution(runId: runId, definitionId: definitionId, phase: .completed,
                                     startUtc: Date(), failureReason: nil)
        runs[runId] = exec
        lock.unlock()
        return exec
    }

    public func get(_ runId: String) async -> WorkflowExecution? {
        lock.lock(); defer { lock.unlock() }
        return runs[runId]
    }

    public func cancel(_ runId: String) async {
        lock.lock()
        if let r = runs[runId] {
            runs[runId] = WorkflowExecution(runId: r.runId, definitionId: r.definitionId,
                                            phase: .failed, startUtc: r.startUtc,
                                            failureReason: "Cancelled")
        }
        lock.unlock()
    }
}

/// In-memory checkpoint store, keyed by (runId, stepId).
public final class InMemoryWorkflowState: IWorkflowState, @unchecked Sendable {
    private let lock = NSLock()
    private var checkpoints: [String: CheckpointPayload] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func checkpoint(_ payload: CheckpointPayload) async {
        lock.lock(); checkpoints["\(payload.runId)/\(payload.stepId)"] = payload; lock.unlock()
    }

    public func load(runId: String, stepId: String) async -> CheckpointPayload? {
        lock.lock(); defer { lock.unlock() }
        return checkpoints["\(runId)/\(stepId)"]
    }
}

// MARK: - Null workflow impls

/// Fail-closed definition store. (C# `NullWorkflowDefinitionStore`.)
public final class NullWorkflowDefinitionStore: IWorkflowDefinitionStore, @unchecked Sendable {
    public static let instance = NullWorkflowDefinitionStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ d: WorkflowDefinition) async {}
    public func get(_ id: String) async -> WorkflowDefinition? { nil }
}

/// Fail-closed runner — every run fails with the empty GUID. (C# `NullWorkflowRunner`.)
public final class NullWorkflowRunner: IWorkflowRunner, @unchecked Sendable {
    public static let instance = NullWorkflowRunner()
    public init() {}
    public var backendId: String { "null" }
    public func start(_ definitionId: String, inputs: [String: AnyCodable]?) async -> WorkflowExecution {
        WorkflowExecution(runId: "00000000-0000-0000-0000-000000000000", definitionId: definitionId,
                          phase: .failed, startUtc: IntegrationDates.minValue, failureReason: "NullWorkflowRunner")
    }
    public func get(_ runId: String) async -> WorkflowExecution? { nil }
    public func cancel(_ runId: String) async {}
}

/// Fail-closed checkpoint store. (C# `NullWorkflowState`.)
public final class NullWorkflowState: IWorkflowState, @unchecked Sendable {
    public static let instance = NullWorkflowState()
    public init() {}
    public var backendId: String { "null" }
    public func checkpoint(_ payload: CheckpointPayload) async {}
    public func load(runId: String, stepId: String) async -> CheckpointPayload? { nil }
}

// MARK: - Conversation state machine (PacaConversations.cs)

/// Conversation lifecycle state. (C# `ConversationState`.)
public enum ConversationState: Int, Sendable, Codable, CaseIterable {
    case queued = 0
    case running = 1
    case finished = 2
    case failed = 3
    case stopped = 4
}

/// One conversation between a human + an agent (or agents). (C# `AgentConversation`.)
public struct AgentConversation: Sendable, Equatable, Codable {
    public let id: String
    public let projectId: String
    public let agentMemberId: String
    public let humanMemberId: String?
    public let openingPrompt: String
    public let state: ConversationState
    public let queuedAtUtc: Date
    public let startedAtUtc: Date?
    public let finishedAtUtc: Date?
    public let resultJson: String?
    public let failureReason: String?

    public init(id: String, projectId: String, agentMemberId: String, humanMemberId: String?,
                openingPrompt: String, state: ConversationState, queuedAtUtc: Date,
                startedAtUtc: Date?, finishedAtUtc: Date?, resultJson: String?, failureReason: String?) {
        self.id = id
        self.projectId = projectId
        self.agentMemberId = agentMemberId
        self.humanMemberId = humanMemberId
        self.openingPrompt = openingPrompt
        self.state = state
        self.queuedAtUtc = queuedAtUtc
        self.startedAtUtc = startedAtUtc
        self.finishedAtUtc = finishedAtUtc
        self.resultJson = resultJson
        self.failureReason = failureReason
    }

    func with(state: ConversationState? = nil, startedAtUtc: Date?? = nil,
              finishedAtUtc: Date?? = nil, resultJson: String?? = nil,
              failureReason: String?? = nil) -> AgentConversation {
        AgentConversation(
            id: id, projectId: projectId, agentMemberId: agentMemberId, humanMemberId: humanMemberId,
            openingPrompt: openingPrompt, state: state ?? self.state, queuedAtUtc: queuedAtUtc,
            startedAtUtc: startedAtUtc ?? self.startedAtUtc,
            finishedAtUtc: finishedAtUtc ?? self.finishedAtUtc,
            resultJson: resultJson ?? self.resultJson,
            failureReason: failureReason ?? self.failureReason)
    }
}

/// One executed step in a conversation. (C# `ConversationStep`.)
public struct ConversationStep: Sendable, Equatable, Codable {
    public let conversationId: String
    public let order: Int
    /// "user" / "agent" / "tool".
    public let speaker: String
    public let contentJson: String
    public let at: Date

    public init(conversationId: String, order: Int, speaker: String, contentJson: String, at: Date) {
        self.conversationId = conversationId
        self.order = order
        self.speaker = speaker
        self.contentJson = contentJson
        self.at = at
    }
}

/// Permission flags required to run risky conversation actions.
/// (C# `ConversationPermissions`.)
public struct ConversationPermissions: Sendable, Equatable, Codable {
    public let allowCloneRepos: Bool
    public let allowCreatePr: Bool

    public init(allowCloneRepos: Bool, allowCreatePr: Bool) {
        self.allowCloneRepos = allowCloneRepos
        self.allowCreatePr = allowCreatePr
    }
}

/// Cancellation token an executor observes to stop early. Mirrors the linked
/// CancellationTokenSource the C# runtime creates per conversation.
public final class ConversationCancellationToken: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    public init() {}
    /// True once the conversation has been asked to stop.
    public var isCancellationRequested: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled
    }
    func cancel() { lock.lock(); cancelled = true; lock.unlock() }
}

/// Host-supplied executor — runs the agent conversation (Docker / OpenHands SDK
/// in production), emitting `ConversationStep`s via `onStep`. (C#
/// `IConversationExecutor`.)
public protocol IConversationExecutor: Sendable {
    /// Start a conversation; emit steps as work progresses. Observe `token` to
    /// stop early.
    func run(conversation: AgentConversation, permissions: ConversationPermissions,
             onStep: @escaping @Sendable (ConversationStep) -> Void,
             token: ConversationCancellationToken) async throws
}

/// Errors raised by the conversation runtime.
public enum ConversationError: Error, Equatable, CustomStringConvertible {
    case alreadyExists(String)
    case notQueued(String)

    public var description: String {
        switch self {
        case .alreadyExists(let id): return "Conversation '\(id)' already exists."
        case .notQueued(let id): return "Conversation '\(id)' is not in Queued state."
        }
    }
}

/// Conversation registry + state machine. Drives the injected
/// `IConversationExecutor` and tracks conversations, steps, and running tokens.
/// (C# `PacaConversationRuntime`.)
public final class PacaConversationRuntime: @unchecked Sendable {
    private let lock = NSLock()
    private var conversations: [String: AgentConversation] = [:]
    private var steps: [String: [ConversationStep]] = [:]
    private var running: [String: ConversationCancellationToken] = [:]
    private let executor: any IConversationExecutor
    private let clock: @Sendable () -> Date

    public init(executor: any IConversationExecutor, clock: (@Sendable () -> Date)? = nil) {
        self.executor = executor
        self.clock = clock ?? { Date() }
    }

    /// Queues a new conversation. Throws when the id already exists.
    @discardableResult
    public func queue(id: String, projectId: String, agentMemberId: String,
                      openingPrompt: String, humanMemberId: String? = nil) throws -> AgentConversation {
        let c = AgentConversation(
            id: id, projectId: projectId, agentMemberId: agentMemberId, humanMemberId: humanMemberId,
            openingPrompt: openingPrompt, state: .queued, queuedAtUtc: clock(),
            startedAtUtc: nil, finishedAtUtc: nil, resultJson: nil, failureReason: nil)
        lock.lock()
        if conversations[id] != nil { lock.unlock(); throw ConversationError.alreadyExists(id) }
        conversations[id] = c
        steps[id] = []
        lock.unlock()
        return c
    }

    /// Returns a conversation snapshot, or `nil`.
    public func get(_ id: String) -> AgentConversation? {
        lock.lock(); defer { lock.unlock() }
        return conversations[id]
    }

    /// Returns the recorded steps for a conversation.
    public func stepsFor(_ id: String) -> [ConversationStep] {
        lock.lock(); defer { lock.unlock() }
        return steps[id] ?? []
    }

    /// Begins executing the conversation, driving the executor to completion.
    /// A cancelled run → `.stopped`; a thrown error → `.failed`. Throws when the
    /// conversation is not currently `.queued`. (C# `StartAsync`.)
    public func start(_ id: String, permissions: ConversationPermissions) async throws {
        lock.lock()
        guard let current = conversations[id], current.state == .queued else {
            lock.unlock()
            throw ConversationError.notQueued(id)
        }
        let started = current.with(state: .running, startedAtUtc: .some(clock()))
        conversations[id] = started
        let token = ConversationCancellationToken()
        running[id] = token
        lock.unlock()

        // Append callback runs under the lock (mirrors the C# `lock (list)`).
        let onStep: @Sendable (ConversationStep) -> Void = { [weak self] step in
            guard let self = self else { return }
            self.lock.lock()
            self.steps[id, default: []].append(step)
            self.lock.unlock()
        }

        do {
            try await executor.run(conversation: started, permissions: permissions,
                                   onStep: onStep, token: token)
            lock.lock()
            if token.isCancellationRequested {
                conversations[id] = started.with(state: .stopped, finishedAtUtc: .some(clock()))
            } else {
                conversations[id] = started.with(state: .finished, finishedAtUtc: .some(clock()),
                                                 resultJson: .some("{}"))
            }
            running[id] = nil
            lock.unlock()
        } catch {
            lock.lock()
            if token.isCancellationRequested {
                conversations[id] = started.with(state: .stopped, finishedAtUtc: .some(clock()))
            } else {
                conversations[id] = started.with(state: .failed, finishedAtUtc: .some(clock()),
                                                 failureReason: .some("\(error)"))
            }
            running[id] = nil
            lock.unlock()
        }
    }

    /// Requests that a running conversation stop. (C# `Stop`.)
    public func stop(_ id: String) {
        lock.lock(); let token = running[id]; lock.unlock()
        token?.cancel()
    }
}
