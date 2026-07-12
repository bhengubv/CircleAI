// TelephonyToolResilience.swift
//
// Port of the CircleAI.Telephony tool-resilience layer:
//   • ToolCircuitBreaker.cs      — ToolCallPolicy, ToolBreakerState,
//                                  CircuitBreakerToolRegistry.
//   • StreamingToolProgress.cs   — ToolProgressUpdate, StreamingToolHandler,
//                                  IToolProgressSink, SpokenToolProgressSink,
//                                  RecordingToolProgressSink, StreamingToolRunner.
//
// NAMING: the C# tool DTOs `ToolDefinition` / `ToolInvocation` / `ToolResult`
// map to the `Telephony…`-prefixed Swift structs (see TelephonyToolCalling.swift),
// and `LocalToolHandler` → `TelephonyLocalToolHandler`. The registry protocol is
// `IToolCallRegistry` with an async, non-throwing `invoke(_:)` (no CancellationToken
// parameter). The circuit breaker therefore imposes its own timeout by racing the
// inner `invoke` against a sleep.
//
// CONCURRENCY:
//   • The C# `ConcurrentDictionary` breaker/policy tables → NSLock-guarded
//     dictionaries keyed by the lowercased tool name (OrdinalIgnoreCase).
//   • `CancellationTokenSource(timeout)` → a task-group race between the inner
//     invoke and `Task.sleep`; the loser is cancelled. Because `IToolCallRegistry.invoke`
//     does not accept a token, cancelling the racing group cannot abort an
//     in-flight local handler — the breaker still records the timeout and returns
//     the timeout result, matching the observable C# outcome (a slow tool trips
//     the breaker and yields a timeout `ToolResult`).
//   • `Interlocked.Increment` on the failure counter → NSLock-guarded Int.

import Foundation

// =====================================================================
// ToolCircuitBreaker.cs
// =====================================================================

/// Per-tool timeout + breaker thresholds. Port of the C# record
/// `CircleAI.Telephony.ToolCallPolicy`.
///
/// `timeout`: wall-clock ceiling for the call. Default 5 s. `failureThreshold`:
/// consecutive failures that trip the breaker. Default 3. `openDuration`: how
/// long the breaker stays open before half-opening. Default 30 s.
public struct ToolCallPolicy: Sendable, Equatable {
    public var timeout: TimeInterval?
    public var failureThreshold: Int
    public var openDuration: TimeInterval?

    public init(timeout: TimeInterval? = nil, failureThreshold: Int = 3, openDuration: TimeInterval? = nil) {
        self.timeout = timeout
        self.failureThreshold = failureThreshold
        self.openDuration = openDuration
    }

    public var timeoutOrDefault: TimeInterval { timeout ?? 5.0 }
    public var openDurationOrDefault: TimeInterval { openDuration ?? 30.0 }
}

/// Breaker state. Port of `CircleAI.Telephony.ToolBreakerState`.
///
/// C# ordinals in declaration order: Closed = 0, Open = 1, HalfOpen = 2.
public enum ToolBreakerState: Int, Sendable, Codable, CaseIterable {
    case closed = 0
    case open = 1
    case halfOpen = 2
}

/// Decorates an `IToolCallRegistry` with per-tool timeouts and a circuit
/// breaker. Port of `CircleAI.Telephony.CircuitBreakerToolRegistry`.
///
/// Pass a `clock` for deterministic tests. Each tool has its own breaker state —
/// a broken billing API doesn't cut off the order-lookup API.
public final class CircuitBreakerToolRegistry: IToolCallRegistry, @unchecked Sendable {

    /// Mutable breaker state for one tool. Guarded by the outer registry lock.
    private final class BreakerEntry {
        var consecutiveFailures: Int = 0
        var openedAt: Date = .distantPast
        var isOpen: Bool = false

        func currentState(_ now: Date, _ openDuration: TimeInterval) -> ToolBreakerState {
            if !isOpen { return .closed }
            if now.timeIntervalSince(openedAt) >= openDuration { return .halfOpen }
            return .open
        }

        func recordSuccess() {
            consecutiveFailures = 0
            isOpen = false
        }

        func recordFailure(_ threshold: Int, _ now: Date) {
            consecutiveFailures += 1
            if consecutiveFailures >= threshold {
                isOpen = true
                openedAt = now
            }
        }
    }

    private let inner: IToolCallRegistry
    private let defaultPolicy: ToolCallPolicy
    private let clock: @Sendable () -> Date
    private let lock = NSLock()
    /// Keyed by lowercased tool name (OrdinalIgnoreCase).
    private var policies: [String: ToolCallPolicy] = [:]
    private var breakers: [String: BreakerEntry] = [:]

    public init(
        inner: IToolCallRegistry,
        defaultPolicy: ToolCallPolicy? = nil,
        clock: (@Sendable () -> Date)? = nil
    ) {
        self.inner = inner
        self.defaultPolicy = defaultPolicy ?? ToolCallPolicy()
        self.clock = clock ?? { Date() }
    }

    /// Override the policy for a specific tool.
    public func setPolicy(_ toolName: String, _ policy: ToolCallPolicy) {
        lock.lock(); policies[toolName.lowercased()] = policy; lock.unlock()
    }

    /// Inspect the current breaker state for a tool.
    public func getState(_ toolName: String) -> ToolBreakerState {
        let key = toolName.lowercased()
        lock.lock(); defer { lock.unlock() }
        guard let entry = breakers[key] else { return .closed }
        let openDuration = (policies[key] ?? defaultPolicy).openDurationOrDefault
        return entry.currentState(clock(), openDuration)
    }

    // Pass-throughs to the wrapped registry.

    public var definitions: [TelephonyToolDefinition] { inner.definitions }

    public func registerLocal(
        _ definition: TelephonyToolDefinition,
        handler: @escaping TelephonyLocalToolHandler
    ) throws {
        try inner.registerLocal(definition, handler: handler)
    }

    public func registerWebhook(_ definition: TelephonyToolDefinition, webhook: URL) throws {
        try inner.registerWebhook(definition, webhook: webhook)
    }

    public func invoke(_ invocation: TelephonyToolInvocation) async -> TelephonyToolResult {
        let key = invocation.toolName.lowercased()
        let policy = getPolicy(key)

        // Resolve the breaker entry (create-if-absent) and evaluate its state.
        lock.lock()
        let entry: BreakerEntry
        if let existing = breakers[key] {
            entry = existing
        } else {
            entry = BreakerEntry()
            breakers[key] = entry
        }
        let state = entry.currentState(clock(), policy.openDurationOrDefault)
        lock.unlock()

        if state == .open {
            return TelephonyToolResult(
                callId: invocation.callId, succeeded: false, resultJson: "{}",
                error: "Tool '\(invocation.toolName)' is circuit-broken; retry after the breaker resets.")
        }

        // Race the inner invoke against the timeout. `IToolCallRegistry.invoke`
        // never throws, so the only "failure" branches are (a) a non-success
        // result and (b) the timeout winning the race. The inner task always
        // yields `.some(result)`; the timeout task yields `nil` as its sentinel,
        // so a `nil` winner unambiguously means "timed out".
        let timeout = policy.timeoutOrDefault
        let result: TelephonyToolResult? = await withTaskGroup(
            of: TelephonyToolResult?.self, returning: TelephonyToolResult?.self
        ) { group in
            group.addTask { .some(await self.inner.invoke(invocation)) }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64((timeout * 1_000_000_000).rounded()))
                return nil
            }
            // First branch to finish decides.
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }

        let now = clock()
        if let result {
            // Inner invoke won the race.
            lock.lock()
            if result.succeeded { entry.recordSuccess() }
            else { entry.recordFailure(policy.failureThreshold, now) }
            lock.unlock()
            return result
        }

        // Timeout branch won (result == nil).
        lock.lock()
        entry.recordFailure(policy.failureThreshold, now)
        lock.unlock()
        return TelephonyToolResult(
            callId: invocation.callId, succeeded: false, resultJson: "{}",
            error: "Tool '\(invocation.toolName)' timed out after \(Self.ms(timeout)) ms.")
    }

    private func getPolicy(_ key: String) -> ToolCallPolicy {
        lock.lock(); defer { lock.unlock() }
        return policies[key] ?? defaultPolicy
    }

    /// Mirror of C#'s `policy.TimeoutOrDefault.TotalMilliseconds` in the message.
    private static func ms(_ seconds: TimeInterval) -> String {
        // C# prints the double (e.g. "5000"); format without trailing zeros.
        let millis = seconds * 1000.0
        if millis == millis.rounded() {
            return String(Int(millis))
        }
        return String(millis)
    }
}

// =====================================================================
// StreamingToolProgress.cs
// =====================================================================

/// One progress update from a streaming tool. Port of the C# record
/// `CircleAI.Telephony.ToolProgressUpdate`.
public struct ToolProgressUpdate: Sendable, Equatable {
    /// The tool-call id this update belongs to.
    public let callId: String
    /// 0..100 progress fraction.
    public let percentComplete: Float
    /// Optional status to speak to the caller.
    public let statusText: String?
    /// Server time the update was created.
    public let emittedAt: Date

    public init(callId: String, percentComplete: Float, statusText: String?, emittedAt: Date) {
        self.callId = callId
        self.percentComplete = percentComplete
        self.statusText = statusText
        self.emittedAt = emittedAt
    }
}

/// The sink a tool pushes progress updates into. Port of
/// `CircleAI.Telephony.IToolProgressSink`.
public protocol IToolProgressSink: Sendable {
    /// Emit one update. Implementations decide whether to forward to the caller.
    func emit(_ update: ToolProgressUpdate) async throws
}

/// Streaming tool handler — accepts a progress sink it can push updates into.
/// Port of the C# delegate `CircleAI.Telephony.StreamingToolHandler`
/// (`ValueTask<string>(string argumentsJson, IToolProgressSink, CancellationToken)`).
public typealias StreamingToolHandler =
    @Sendable (_ argumentsJson: String, _ progressSink: IToolProgressSink) async throws -> String

/// Default sink that throttles updates (≥ `minInterval` apart) and speaks each
/// via TTS to the active call session. Port of
/// `CircleAI.Telephony.SpokenToolProgressSink`.
public final class SpokenToolProgressSink: IToolProgressSink, @unchecked Sendable {
    private let session: ICallSession
    private let tts: BriefingSynthesiser
    private let minInterval: TimeInterval
    private let gate = NSLock()
    private var lastSpoken: Date = .distantPast
    private let clock: @Sendable () -> Date

    public init(
        session: ICallSession,
        tts: @escaping BriefingSynthesiser,
        minInterval: TimeInterval? = nil,
        clock: (@Sendable () -> Date)? = nil
    ) {
        self.session = session
        self.tts = tts
        self.minInterval = minInterval ?? 2.0
        self.clock = clock ?? { Date() }
    }

    public func emit(_ update: ToolProgressUpdate) async throws {
        guard let status = update.statusText, !status.isBlank else { return }

        let now = clock()
        var shouldSpeak = false
        gate.lock()
        shouldSpeak = now.timeIntervalSince(lastSpoken) >= minInterval
        if shouldSpeak { lastSpoken = now }
        gate.unlock()
        if !shouldSpeak { return }

        let audio = try await tts(status)
        if !audio.isEmpty {
            try await session.sendAudio(AudioFrame(pcm: audio, format: .pcm24000, offset: 0))
        }
    }
}

/// Sink that records updates for observability without speaking them. Port of
/// `CircleAI.Telephony.RecordingToolProgressSink`.
public final class RecordingToolProgressSink: IToolProgressSink, @unchecked Sendable {
    private let gate = NSLock()
    private var _updates: [ToolProgressUpdate] = []

    public init() {}

    public var updates: [ToolProgressUpdate] {
        gate.lock(); defer { gate.unlock() }
        return _updates
    }

    public func emit(_ update: ToolProgressUpdate) async throws {
        gate.lock(); _updates.append(update); gate.unlock()
    }
}

/// Run a streaming tool handler against a progress sink. Port of the C# static
/// class `CircleAI.Telephony.StreamingToolRunner`.
public enum StreamingToolRunner {
    public static func run(
        _ invocation: TelephonyToolInvocation,
        handler: @escaping StreamingToolHandler,
        sink: IToolProgressSink
    ) async -> TelephonyToolResult {
        do {
            let resultJson = try await handler(invocation.argumentsJson, sink)
            // C#: `resultJson ?? "{}"`. Swift handler returns non-nil String;
            // empty is preserved (matches the non-null path).
            return TelephonyToolResult(callId: invocation.callId, succeeded: true, resultJson: resultJson)
        } catch {
            return TelephonyToolResult(
                callId: invocation.callId, succeeded: false, resultJson: "{}",
                error: (error as? TelephonyError)?.description ?? "\(error)")
        }
    }
}
