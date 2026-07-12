// OrchestrationLoki.swift
//
// Port of the remaining CircleAI.Orchestration types on top of the base
// dispatch surface already in Orchestration.swift.
//   • LokiOrchestrator.cs            — LokiOrchestrator (semaphore-bounded
//                                      swarm + per-result quality gate)
//   • IncidentTrigger.cs             — IncidentTrigger (memory-entry → tasks,
//                                      anomaly-signal → task)
//   • SecurityOrchestrationBridge.cs — SecurityOrchestrationBridge
//
// Porting notes:
//   • `IAsyncEnumerable<SwarmResult> RunSwarmAsync` → `AsyncStream<SwarmResult>`.
//     C# acquires a `SemaphoreSlim(MaxConcurrency)` slot BEFORE launching each
//     task, collects the launched `Task`s in submission order, then awaits them
//     in that same order — so results are emitted in submission order, not
//     completion order. The Swift port reproduces this exactly with an
//     `AsyncSemaphore` gate and an ordered array of child `Task`s awaited in
//     turn. The per-task timeout is modelled with a race between the dispatch
//     and a `Task.sleep`, yielding the C# timeout `SwarmResult` on expiry.
//   • `AnomalySignal.Confidence` is `Float` in the Swift port, so the dispatch
//     threshold is `Float` here (C# used `double` 0.30).
//   • `IncidentTrigger.FromMemoryEntry` reads `EpisodicMemoryEntry.tags`
//     (optional dictionary) and formats `recordedAt` as ISO-8601 ("O").
//   • `SecurityOrchestrationBridge` runs the inner watchdog response and the
//     agent dispatch in parallel; the agent path is fire-and-forget and never
//     blocks or crashes the runtime response (matches C# `ContinueWith`).

import Foundation

// MARK: - AsyncSemaphore

/// A minimal async counting semaphore. `wait()` suspends until a permit is
/// free; `signal()` releases one. Models the `SemaphoreSlim(maxConcurrency)`
/// the C# orchestrator uses to bound in-flight dispatches.
final class AsyncSemaphore: @unchecked Sendable {
    private let lock = NSLock()
    private var permits: Int
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(value: Int) {
        precondition(value >= 1, "semaphore value must be >= 1")
        self.permits = value
    }

    func wait() async {
        lock.lock()
        if permits > 0 {
            permits -= 1
            lock.unlock()
            return
        }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            waiters.append(continuation)
            lock.unlock()
        }
    }

    func signal() {
        lock.lock()
        if !waiters.isEmpty {
            let next = waiters.removeFirst()
            lock.unlock()
            next.resume()
        } else {
            permits += 1
            lock.unlock()
        }
    }
}

// MARK: - LokiOrchestrator

/// Host-side orchestrator. Accepts `AgentTask` items, dispatches them through an
/// `IAgentDispatcher`, enforces quality gates, and exposes results as an
/// `AsyncStream`. (C# `LokiOrchestrator`.)
///
/// Task execution is bounded by `AgentSwarmConfig.maxConcurrency`. After each
/// task completes, the quality gate is evaluated; gate failures are re-emitted
/// as `.blocked` results with the gate's blocker messages appended to `issues`.
public final class LokiOrchestrator: @unchecked Sendable {
    private let dispatcher: any IAgentDispatcher
    private let config: AgentSwarmConfig

    /// Initialises with a dispatcher and optional config (defaults to
    /// `AgentSwarmConfig.default`).
    public init(dispatcher: any IAgentDispatcher, config: AgentSwarmConfig? = nil) {
        self.dispatcher = dispatcher
        self.config = config ?? .default
    }

    /// Runs a swarm of tasks concurrently up to `maxConcurrency`. Results are
    /// emitted in submission order; gate failures become `.blocked`.
    public func runSwarm(_ tasks: [AgentTask]) -> AsyncStream<SwarmResult> {
        let dispatcher = self.dispatcher
        let config = self.config
        return AsyncStream<SwarmResult>(bufferingPolicy: .unbounded) { continuation in
            Task {
                let semaphore = AsyncSemaphore(value: max(1, config.maxConcurrency))
                var running: [Task<SwarmResult, Never>] = []
                running.reserveCapacity(tasks.count)

                for task in tasks {
                    await semaphore.wait()
                    running.append(Task {
                        let result = await Self.runOne(task, dispatcher: dispatcher, config: config)
                        semaphore.signal()
                        return result
                    })
                }

                for runningTask in running {
                    let result = await runningTask.value
                    let gate = await dispatcher.runQualityGate(result)
                    if !gate.passed
                        && (config.requireReviewPassBeforeDeploy
                            || config.requireSecurityPassBeforeDeploy) {
                        continuation.yield(SwarmResult(
                            taskId: result.taskId, role: result.role, status: .blocked,
                            output: result.output, issues: result.issues + gate.blockers,
                            completedAt: result.completedAt))
                    } else {
                        continuation.yield(result)
                    }
                }
                continuation.finish()
            }
        }
    }

    /// Dispatches one task with a timeout race. On timeout / thrown error the
    /// task surfaces as a failed `SwarmResult` so remaining tasks still emit.
    private static func runOne(_ task: AgentTask, dispatcher: any IAgentDispatcher,
                               config: AgentSwarmConfig) async -> SwarmResult {
        await withTaskGroup(of: SwarmResult?.self) { group in
            group.addTask {
                await dispatcher.dispatch(task)
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(max(0, config.taskTimeout) * 1_000_000_000))
                if Task.isCancelled { return nil }
                return SwarmResult(
                    taskId: task.id, role: task.role, status: .failed,
                    output: "Task timed out.",
                    issues: ["[HIGH] Task exceeded configured timeout."],
                    completedAt: Date())
            }
            // First to finish wins; cancel the loser.
            let first = await group.next() ?? nil
            group.cancelAll()
            let result = first ?? SwarmResult(
                taskId: task.id, role: task.role, status: .failed,
                output: "Task produced no result.",
                issues: ["[HIGH] Dispatch produced no result."],
                completedAt: Date())
            return result
        }
    }
}

// MARK: - IncidentTrigger

/// Maps a recorded `EpisodicMemoryEntry` or an `AnomalySignal` to the agent
/// tasks that should be triggered. (C# `IncidentTrigger`.)
public enum IncidentTrigger {
    /// Tag keys identifying a crash / unhandled-error incident (case-insensitive).
    private static let crashTags: Set<String> = ["crash", "exception", "unhandled_error", "oom", "null_reference"]

    /// Tag keys that, alongside a crash signal, warrant a security investigation.
    private static let securityTags: Set<String> = ["auth_failure", "permission_denied", "token_expired", "injection", "overflow"]

    private static let iso8601: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// Inspects an episodic memory entry and returns the tasks to trigger.
    /// Always includes one Operations task on a crash tag; adds a Security task
    /// when a security tag is also present. Empty when not an incident.
    public static func fromMemoryEntry(_ entry: EpisodicMemoryEntry) -> [AgentTask] {
        let tags = entry.tags ?? [:]
        let lowerKeys = Set(tags.keys.map { $0.lowercased() })
        let isCrash = !lowerKeys.isDisjoint(with: crashTags)
        guard isCrash else { return [] }

        var tasks: [AgentTask] = []
        tasks.append(AgentTask.create(
            role: .operations,
            description: "ops-incident: diagnose crash recorded at \(iso8601.string(from: entry.recordedAt))",
            priority: .high,
            inputs: [
                "episode_id": entry.id.uuidString,
                "user_text": entry.userText,
                "assistant_text": entry.assistantText,
                "app_context": entry.appContext ?? "",
            ]))

        let isSecurity = !lowerKeys.isDisjoint(with: securityTags)
        if isSecurity {
            tasks.append(AgentTask.create(
                role: .security,
                description: "ops-security: investigate security incident from episode \(entry.id.uuidString)",
                priority: .critical,
                inputs: [
                    "episode_id": entry.id.uuidString,
                    "app_context": entry.appContext ?? "",
                    "tags": tags.keys.joined(separator: ","),
                ]))
        }
        return tasks
    }

    /// Maps a confirmed `AnomalySignal` to a Security `AgentTask`, or `nil` when
    /// below `dispatchThreshold`. Confidence drives priority; high-severity
    /// vectors are bumped one rank toward Critical.
    public static func fromAnomalySignal(_ signal: AnomalySignal,
                                         dispatchThreshold: Float = 0.30) -> AgentTask? {
        if signal.confidence < dispatchThreshold { return nil }

        var priority: AgentPriority
        if signal.confidence >= 0.85 {
            priority = .critical
        } else if signal.confidence >= 0.60 {
            priority = .high
        } else {
            priority = .normal
        }

        let isHighSeverityVector: Bool
        switch signal.vector {
        case .controlFlowDrift, .privilegeEscalation, .networkPivot, .stateCorruption:
            isHighSeverityVector = true
        default:
            isHighSeverityVector = false
        }

        // priority ordering: critical=0 < high=1 < normal=2 < low=3.
        // "bump one rank" = decrease the numeric value toward critical.
        if isHighSeverityVector && priority.rawValue > AgentPriority.critical.rawValue {
            let bumped = max(AgentPriority.critical.rawValue, priority.rawValue - 1)
            priority = AgentPriority(rawValue: bumped) ?? priority
        }

        var inputs = signal.evidence
        inputs["signal_id"] = signal.id.uuidString
        inputs["vector"] = threatVectorName(signal.vector)
        inputs["confidence"] = String(format: "%.3f", signal.confidence)
        inputs["affected_module"] = signal.affectedModule
        inputs["description"] = signal.description
        inputs["detected_at"] = IncidentTrigger.iso8601.string(from: signal.detectedAt)

        let pct = Int((signal.confidence * 100).rounded())
        return AgentTask.create(
            role: .security,
            description: "ops-security: anomaly \(threatVectorName(signal.vector)) in \(signal.affectedModule) (confidence \(pct)%)",
            priority: priority,
            inputs: inputs)
    }

    /// Stable member-name string for a `ThreatVector` (matches C# `.ToString()`).
    private static func threatVectorName(_ v: ThreatVector) -> String {
        switch v {
        case .memoryAnomaly: return "MemoryAnomaly"
        case .controlFlowDrift: return "ControlFlowDrift"
        case .privilegeEscalation: return "PrivilegeEscalation"
        case .biometricSpoofAttempt: return "BiometricSpoofAttempt"
        case .networkPivot: return "NetworkPivot"
        case .stateCorruption: return "StateCorruption"
        case .agentPatchRejected: return "AgentPatchRejected"
        case .unknown: return "Unknown"
        }
    }
}

// MARK: - SecurityOrchestrationBridge

/// Wraps an `ISecurityWatchdog` so every anomaly signal also dispatches an
/// ops-security `AgentTask` to a `LokiOrchestrator`. Runtime response and agent
/// dispatch proceed in parallel; neither blocks the other. (C#
/// `SecurityOrchestrationBridge`.)
public final class SecurityOrchestrationBridge: ISecurityWatchdog, @unchecked Sendable {
    private let inner: any ISecurityWatchdog
    private let orchestrator: LokiOrchestrator
    private let dispatchThreshold: Float

    /// Creates a bridge delegating immune-system responses to `inner` and
    /// dispatching ops-security agents via `orchestrator`.
    public init(inner: any ISecurityWatchdog, orchestrator: LokiOrchestrator,
                dispatchThreshold: Float = 0.30) {
        self.inner = inner
        self.orchestrator = orchestrator
        self.dispatchThreshold = dispatchThreshold
    }

    public func onAnomalyDetected(_ signal: AnomalySignal,
                                  checkpoint: SecurityCheckpoint?) async throws -> SecurityResponse {
        // Fire-and-forget the agent dispatch in parallel; it must never block or
        // crash the runtime response.
        let orchestrator = self.orchestrator
        let threshold = self.dispatchThreshold
        Task {
            guard let task = IncidentTrigger.fromAnomalySignal(signal, dispatchThreshold: threshold) else { return }
            for await _ in orchestrator.runSwarm([task]) {
                // Results are observable through orchestrator subscriptions host-side.
            }
        }

        // Await the watchdog so the caller gets the runtime response immediately.
        return try await inner.onAnomalyDetected(signal, checkpoint: checkpoint)
    }

    public func streamSignals() -> AsyncStream<AnomalySignal> {
        inner.streamSignals()
    }
}
