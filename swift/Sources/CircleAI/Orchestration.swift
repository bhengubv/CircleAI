// Orchestration.swift
//
// Port of the named CircleAI.Orchestration types — the agent-swarm dispatch +
// quality-gate surface that loki-mode hooks into at the host level.
//   • AgentRole.cs           — AgentRole, AgentPriority, AgentStatus
//   • AgentTask.cs           — AgentTask (+ Create factory)
//   • AgentSwarmConfig.cs    — AgentSwarmConfig (+ Default / ForDevice)
//   • SwarmResult.cs         — SwarmResult
//   • QualityGateResult.cs   — QualityGateResult
//   • IAgentDispatcher.cs    — IAgentDispatcher
//   • LocalAgentDispatcher.cs— LocalAgentDispatcher (in-process, handler-per-role)
//
// Porting notes:
//   • `Guid` → `UUID`; `Guid.NewGuid()` → `UUID()`.
//   • `TimeSpan` → `TimeInterval` (seconds).
//   • `AgentSwarmConfig.ForDevice` reuses the already-ported `DeviceProbe` /
//     `DeviceTierDefaults` from Device.swift.
//   • `LocalAgentDispatcher` stores an async handler per role. The C# internal
//     unbounded `Channel<AgentTask>` is a vestigial queue that is only completed
//     on Dispose and never read; the Swift port keeps `dispose()` semantics
//     (post-dispose dispatch throws) without the dead channel.

import Foundation

// MARK: - Enums

/// Categorises the domain responsibility of an agent in a swarm. (C# `AgentRole`.)
public enum AgentRole: Int, Sendable, Codable, CaseIterable {
    /// Writing, reviewing, and fixing code.
    case engineering = 0
    /// Infrastructure, deployments, incident response.
    case operations = 1
    /// Quality review, testing, acceptance criteria.
    case review = 2
    /// Security analysis and vulnerability assessment.
    case security = 3
}

/// Execution priority of an agent task. Lower value = higher urgency.
/// (C# `AgentPriority`.)
public enum AgentPriority: Int, Sendable, Codable, CaseIterable {
    /// Immediate — blocks all other work.
    case critical = 0
    /// Urgent — current session.
    case high = 1
    /// Standard — arrival order.
    case normal = 2
    /// Best-effort.
    case low = 3
}

/// Lifecycle status of an agent task or swarm result. (C# `AgentStatus`.)
public enum AgentStatus: Int, Sendable, Codable, CaseIterable {
    /// Created but not dispatched.
    case pending = 0
    /// Being executed.
    case running = 1
    /// Completed and all gates passed.
    case passed = 2
    /// Completed but produced an error.
    case failed = 3
    /// Halted by a gate or missing handler.
    case blocked = 4
}

// MARK: - AgentTask

/// A single unit of work dispatched to an agent swarm. (C# `AgentTask`.)
public struct AgentTask: Sendable, Equatable, Codable {
    /// Stable unique identifier.
    public let id: UUID
    /// Agent domain responsible for the task.
    public let role: AgentRole
    /// Human-readable description.
    public let description: String
    /// Execution urgency.
    public let priority: AgentPriority
    /// Arbitrary key-value inputs for the handler.
    public let inputs: [String: String]
    /// UTC creation timestamp.
    public let createdAt: Date

    public init(id: UUID, role: AgentRole, description: String, priority: AgentPriority,
                inputs: [String: String], createdAt: Date) {
        self.id = id
        self.role = role
        self.description = description
        self.priority = priority
        self.inputs = inputs
        self.createdAt = createdAt
    }

    /// Stamps a fresh task with a new `UUID` and the current time.
    /// (C# `AgentTask.Create`.)
    public static func create(role: AgentRole, description: String, priority: AgentPriority,
                              inputs: [String: String]? = nil) -> AgentTask {
        AgentTask(id: UUID(), role: role, description: description, priority: priority,
                  inputs: inputs ?? [:], createdAt: Date())
    }
}

// MARK: - AgentSwarmConfig

/// Tuning parameters governing swarm scheduling + quality gates.
/// (C# `AgentSwarmConfig`.)
public struct AgentSwarmConfig: Sendable, Equatable, Codable {
    /// Maximum tasks executing simultaneously.
    public let maxConcurrency: Int
    /// Max wall-clock seconds per task before it is cancelled + failed.
    public let taskTimeout: TimeInterval
    /// When true, a failing Review gate blocks downstream deployment.
    public let requireReviewPassBeforeDeploy: Bool
    /// When true, a failing Security gate blocks downstream deployment.
    public let requireSecurityPassBeforeDeploy: Bool

    public init(maxConcurrency: Int, taskTimeout: TimeInterval,
                requireReviewPassBeforeDeploy: Bool, requireSecurityPassBeforeDeploy: Bool) {
        self.maxConcurrency = maxConcurrency
        self.taskTimeout = taskTimeout
        self.requireReviewPassBeforeDeploy = requireReviewPassBeforeDeploy
        self.requireSecurityPassBeforeDeploy = requireSecurityPassBeforeDeploy
    }

    /// Production-safe defaults: 4 concurrent, 5-minute timeout, both gates on.
    public static var `default`: AgentSwarmConfig {
        AgentSwarmConfig(maxConcurrency: 4, taskTimeout: 5 * 60,
                         requireReviewPassBeforeDeploy: true, requireSecurityPassBeforeDeploy: true)
    }

    /// Device-aware defaults: `maxConcurrency` is sized via
    /// `DeviceTierDefaults.maxConcurrency` against `probe`; the rest matches
    /// `default`. (C# `AgentSwarmConfig.ForDevice`.)
    public static func forDevice(_ probe: DeviceProbe) -> AgentSwarmConfig {
        AgentSwarmConfig(
            maxConcurrency: DeviceTierDefaults.maxConcurrency(probe.classify(), cpuCores: probe.cpuCores),
            taskTimeout: 5 * 60,
            requireReviewPassBeforeDeploy: true,
            requireSecurityPassBeforeDeploy: true)
    }
}

// MARK: - Results

/// The outcome produced by an agent handler for a single `AgentTask`.
/// (C# `SwarmResult`.)
public struct SwarmResult: Sendable, Equatable, Codable {
    /// The `AgentTask.id` this result belongs to.
    public let taskId: UUID
    /// The role that produced this result.
    public let role: AgentRole
    /// Final lifecycle status.
    public let status: AgentStatus
    /// Human-readable output (diff, report, or error message).
    public let output: String
    /// Zero or more issue strings. Prefix with `[CRITICAL]` / `[HIGH]` to block.
    public let issues: [String]
    /// UTC completion timestamp.
    public let completedAt: Date

    public init(taskId: UUID, role: AgentRole, status: AgentStatus, output: String,
                issues: [String], completedAt: Date) {
        self.taskId = taskId
        self.role = role
        self.status = status
        self.output = output
        self.issues = issues
        self.completedAt = completedAt
    }
}

/// The verdict produced by `IAgentDispatcher.runQualityGate`. (C# `QualityGateResult`.)
public struct QualityGateResult: Sendable, Equatable, Codable {
    /// True when there are no blockers.
    public let passed: Bool
    /// Critical/high-severity issues that must be resolved before deploy.
    public let blockers: [String]
    /// Low-severity issues surfaced for visibility.
    public let warnings: [String]

    public init(passed: Bool, blockers: [String], warnings: [String]) {
        self.passed = passed
        self.blockers = blockers
        self.warnings = warnings
    }
}

// MARK: - IAgentDispatcher

/// Routes agent tasks to handlers and evaluates quality gates on results.
/// (C# `IAgentDispatcher`.)
public protocol IAgentDispatcher: Sendable {
    /// Dispatches `task` to its handler and returns the result.
    func dispatch(_ task: AgentTask) async -> SwarmResult
    /// Evaluates a completed result and determines whether it passes the gate.
    func runQualityGate(_ result: SwarmResult) async -> QualityGateResult
}

/// Errors raised by the local dispatcher.
public enum AgentDispatcherError: Error, Equatable {
    /// `dispatch` was called after `dispose()`.
    case disposed
}

/// In-process agent dispatcher. Routes tasks to async handler closures
/// registered per `AgentRole`. No external network calls. (C# `LocalAgentDispatcher`.)
///
/// Tasks dispatched to roles without a registered handler return
/// `.blocked` immediately. After `dispose()`, `dispatch` throws — but since the
/// protocol method is non-throwing, the disposed case surfaces as a `.blocked`
/// result; use `dispatchThrowing` when you need the explicit error.
public final class LocalAgentDispatcher: IAgentDispatcher, @unchecked Sendable {
    private let lock = NSLock()
    private var handlers: [AgentRole: @Sendable (AgentTask) async -> SwarmResult] = [:]
    private var disposed = false

    public init() {}

    /// Registers an async handler for `role`. Replaces any prior handler.
    public func registerHandler(_ role: AgentRole,
                                _ handler: @escaping @Sendable (AgentTask) async -> SwarmResult) {
        lock.lock(); defer { lock.unlock() }
        handlers[role] = handler
    }

    public func dispatch(_ task: AgentTask) async -> SwarmResult {
        (try? await dispatchThrowing(task)) ?? SwarmResult(
            taskId: task.id, role: task.role, status: .blocked,
            output: "Dispatcher disposed.",
            issues: ["[CRITICAL] Dispatcher was disposed before dispatch."],
            completedAt: Date())
    }

    /// Throwing variant that surfaces `AgentDispatcherError.disposed`.
    public func dispatchThrowing(_ task: AgentTask) async throws -> SwarmResult {
        lock.lock()
        if disposed { lock.unlock(); throw AgentDispatcherError.disposed }
        let handler = handlers[task.role]
        lock.unlock()

        if let handler = handler {
            return await handler(task)
        }
        // No handler — surface a blocked result with an actionable message.
        return SwarmResult(
            taskId: task.id, role: task.role, status: .blocked,
            output: "No handler registered for role \(task.role).",
            issues: ["Register a handler for AgentRole.\(task.role) before dispatching."],
            completedAt: Date())
    }

    /// Deterministic gate: any issue prefixed `[CRITICAL]` / `[HIGH]`
    /// (case-insensitive) is a blocker; all others are warnings.
    public func runQualityGate(_ result: SwarmResult) async -> QualityGateResult {
        var blockers: [String] = []
        var warnings: [String] = []
        for issue in result.issues {
            let upper = issue.uppercased()
            if upper.hasPrefix("[CRITICAL]") || upper.hasPrefix("[HIGH]") {
                blockers.append(issue)
            } else {
                warnings.append(issue)
            }
        }
        return QualityGateResult(passed: blockers.isEmpty, blockers: blockers, warnings: warnings)
    }

    /// Disposes the dispatcher. After disposal, `dispatchThrowing` throws.
    public func dispose() {
        lock.lock(); disposed = true; lock.unlock()
    }
}
