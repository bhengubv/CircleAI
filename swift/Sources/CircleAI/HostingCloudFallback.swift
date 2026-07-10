// HostingCloudFallback.swift
//
// Port of the CircleAI.Hosting.CloudFallback surface:
//   - CloudFallbackChain.cs      → IConfigurableChatGenerator, CloudFallbackChain
//   - BackupBrainOrchestrator.cs → BrainHealth, BrainStatus, BackupBrainPolicy,
//                                  BackupBrainOrchestrator
//   - a deterministic local fake generator for tests
//
// The concrete cloud generators (OpenAI/Anthropic/Gemini/Groq/Cerebras/Together/
// DeepSeek) are HTTP+SSE and are injected via `IConfigurableChatGenerator` rather
// than embedded — the SDK never bakes in provider keys. A deterministic local
// fake (`LocalDeterministicChatGenerator`) stands in for tests, and the
// composite routing (chain fall-through, mid-call failover, cool-down/half-open)
// is ported faithfully.

import Foundation

// =====================================================================
// IConfigurableChatGenerator
// =====================================================================

/// Reports whether a generator is currently in a state where it can serve calls.
/// Cloud generators expose this via the API-key check; on-device generators that
/// don't implement it are presumed always ready. Ported from
/// `IConfigurableChatGenerator`.
public protocol IConfigurableChatGenerator: IChatGenerator {
    /// True when the generator can serve calls (e.g. API key present).
    var isConfigured: Bool { get }
    /// Display name (e.g. "OpenAI · gpt-4o-mini").
    var engineLabel: String { get }
    /// Human-readable explanation of the current state.
    var statusMessage: String { get }
}

// =====================================================================
// LocalDeterministicChatGenerator (test fake)
// =====================================================================

/// Deterministic local fake `IConfigurableChatGenerator`. Echoes a scripted
/// reply (or the last user message, prefixed) so cloud-fallback composites can
/// be tested without network. When `isConfigured` is false it emits the C#
/// fail-soft frame `"[<engineLabel>: not configured]"` so the chain skips it.
public final class LocalDeterministicChatGenerator: IConfigurableChatGenerator, @unchecked Sendable {
    private let reply: String?
    private let shouldThrow: Bool
    public let engineLabel: String
    private let configured: Bool

    public init(engineLabel: String = "local-fake", reply: String? = nil,
                isConfigured: Bool = true, throwsOnCall: Bool = false) {
        self.engineLabel = engineLabel
        self.reply = reply
        self.configured = isConfigured
        self.shouldThrow = throwsOnCall
    }

    public var isConfigured: Bool { configured }
    public var statusMessage: String { configured ? "Ready · \(engineLabel)" : "\(engineLabel) not configured." }

    private func compose(_ messages: [ChatMessage]) -> String {
        if let reply = reply { return reply }
        let lastUser = messages.last { $0.role.caseInsensitiveCompare("user") == .orderedSame }?.content ?? ""
        return "\(engineLabel): \(lastUser)"
    }

    public func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        if shouldThrow { throw CloudFallbackError.generatorFailed(engineLabel) }
        if !configured { return "[\(engineLabel): not configured]" }
        return compose(messages)
    }

    public func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        let text = configured ? compose(messages) : "[\(engineLabel): not configured]"
        let fail = shouldThrow
        return AsyncStream { continuation in
            if fail {
                // Simulate a mid-stream fault by finishing without any frame; the
                // composite treats "no frame + ready" as a fault-and-move-on.
                continuation.finish(); return
            }
            continuation.yield(text)
            continuation.finish()
        }
    }
}

/// Errors surfaced by the cloud-fallback fakes.
public enum CloudFallbackError: Error, Equatable {
    case generatorFailed(String)
}

// =====================================================================
// CloudFallbackChain
// =====================================================================

/// Tries an ordered list of `IChatGenerator`s and serves from the first one
/// ready. A generator that yields a fail-soft "[… not configured]" frame doesn't
/// count as ready — the chain skips it. Generators that throw are also skipped.
/// Ported from `CloudFallbackChain`.
public final class CloudFallbackChain: IChatGenerator, @unchecked Sendable {
    private let generators: [IChatGenerator]

    /// Build a chain. Order matters — the first ready generator wins, so put
    /// on-device first for sovereign-by-default.
    public init(_ generators: [IChatGenerator]) {
        self.generators = generators
    }

    public var chainGenerators: [IChatGenerator] { generators }

    public func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        for g in generators {
            if !Self.isReady(g) { continue }
            do {
                return try await g.generate(messages: messages, options: options)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                // Fall through to the next generator.
            }
        }
        return "[CloudFallbackChain: no configured generator could serve the request]"
    }

    public func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        AsyncStream { continuation in
            let task = Task {
                for g in self.generators {
                    if !Self.isReady(g) { continue }
                    var yielded = false
                    var faulted = false
                    for await chunk in g.stream(messages: messages, options: options) {
                        if Task.isCancelled { continuation.finish(); return }
                        if !yielded && Self.isFailSoftFrame(chunk) {
                            // Generator declined the call (e.g. no API key).
                            faulted = true
                            break
                        }
                        yielded = true
                        continuation.yield(chunk)
                    }
                    if yielded { continuation.finish(); return }
                    _ = faulted // either declined or produced nothing → try next
                }
                continuation.yield("[CloudFallbackChain: no configured generator could serve the request]")
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    private static func isReady(_ g: IChatGenerator) -> Bool {
        guard let c = g as? IConfigurableChatGenerator else { return true }
        return c.isConfigured
    }

    static func isFailSoftFrame(_ chunk: String) -> Bool {
        chunk.hasPrefix("[")
            && (chunk.range(of: "not configured", options: .caseInsensitive) != nil
                || chunk.range(of: "CloudFallbackChain", options: .caseInsensitive) != nil)
    }
}

// =====================================================================
// BackupBrainOrchestrator
// =====================================================================

/// Health state of one brain in the chain. Ported from `BrainHealth`.
public enum BrainHealth: Int, Sendable, Equatable {
    case healthy = 0
    case degraded = 1
    case coolingDown = 2
}

/// Snapshot of brain health for monitoring. Ported from `BrainStatus`.
public struct BrainStatus: Sendable, Equatable {
    public let label: String
    public let health: BrainHealth
    public let consecutiveFailures: Int

    public init(label: String, health: BrainHealth, consecutiveFailures: Int) {
        self.label = label
        self.health = health
        self.consecutiveFailures = consecutiveFailures
    }
}

/// Policy knobs for the orchestrator. Ported from `BackupBrainPolicy`.
public struct BackupBrainPolicy: Sendable {
    public let degradedAfterFailures: Int
    public let coolDownDuration: TimeInterval?
    public let maxRetriesPerTurn: Int

    public init(degradedAfterFailures: Int = 2, coolDownDuration: TimeInterval? = nil, maxRetriesPerTurn: Int = 3) {
        self.degradedAfterFailures = degradedAfterFailures
        self.coolDownDuration = coolDownDuration
        self.maxRetriesPerTurn = maxRetriesPerTurn
    }

    public var coolDownDurationOrDefault: TimeInterval { coolDownDuration ?? 30 }
}

/// Wraps an ordered set of brains; switches on failure, retries the primary on
/// cool-down (half-open). Differs from `CloudFallbackChain` (start-of-call
/// ordering) — this is between-turn failover. Ported from
/// `BackupBrainOrchestrator`.
public final class BackupBrainOrchestrator: IChatGenerator, @unchecked Sendable {

    private final class BrainEntry {
        let brain: IChatGenerator
        let gate = NSLock()
        var consecutive = 0
        var degradedSince = Date.distantPast
        var isDegraded = false
        init(_ brain: IChatGenerator) { self.brain = brain }

        func healthAt(_ now: Date, coolDown: TimeInterval) -> BrainHealth {
            if !isDegraded { return .healthy }
            if now.timeIntervalSince(degradedSince) >= coolDown { return .coolingDown }
            return .degraded
        }
        func recordSuccess() {
            gate.lock(); consecutive = 0; isDegraded = false; gate.unlock()
        }
        func recordFailure(threshold: Int, now: Date) {
            gate.lock()
            consecutive += 1
            if consecutive >= threshold { isDegraded = true; degradedSince = now }
            gate.unlock()
        }
    }

    private let brains: [BrainEntry]
    private let policy: BackupBrainPolicy
    private let clock: @Sendable () -> Date

    public init(_ brains: [IChatGenerator], policy: BackupBrainPolicy = BackupBrainPolicy(),
                clock: @escaping @Sendable () -> Date = { Date() }) {
        precondition(!brains.isEmpty, "At least one brain is required.")
        self.brains = brains.map { BrainEntry($0) }
        self.policy = policy
        self.clock = clock
    }

    public var statuses: [BrainStatus] {
        let now = clock()
        return brains.map { e in
            e.gate.lock()
            let h = e.healthAt(now, coolDown: policy.coolDownDurationOrDefault)
            let label = (e.brain as? IConfigurableChatGenerator)?.engineLabel ?? String(describing: type(of: e.brain))
            let consecutive = e.consecutive
            e.gate.unlock()
            return BrainStatus(label: label, health: h, consecutiveFailures: consecutive)
        }
    }

    public func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        let maxRetries = min(policy.maxRetriesPerTurn, brains.count)
        var tried = Set<ObjectIdentifier>()
        for _ in 0..<maxRetries {
            guard let pick = pickAvailable(skip: tried) else { break }
            tried.insert(ObjectIdentifier(pick))
            do {
                let result = try await pick.brain.generate(messages: messages, options: options)
                pick.recordSuccess()
                return result
            } catch {
                pick.recordFailure(threshold: policy.degradedAfterFailures, now: clock())
            }
        }
        return "[All brains failed.]"
    }

    public func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        AsyncStream { continuation in
            let task = Task {
                let maxRetries = min(self.policy.maxRetriesPerTurn, self.brains.count)
                var tried = Set<ObjectIdentifier>()
                for _ in 0..<maxRetries {
                    guard let pick = self.pickAvailable(skip: tried) else { break }
                    tried.insert(ObjectIdentifier(pick))
                    var streamedAny = false
                    var failed = false
                    for await chunk in self.iterateStreamSafe(pick, messages: messages, options: options) {
                        if Task.isCancelled { continuation.finish(); return }
                        if chunk == nil { failed = true; break }
                        streamedAny = true
                        continuation.yield(chunk!)
                    }
                    if failed {
                        pick.recordFailure(threshold: self.policy.degradedAfterFailures, now: self.clock())
                        if !streamedAny { continue }
                    }
                    if streamedAny {
                        pick.recordSuccess()
                        continuation.finish(); return
                    }
                }
                continuation.yield("[All brains failed.]")
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Yields each chunk, or a single `nil` sentinel on fault. Mirrors the C#
    /// `IterateStreamSafe` (`string?` with null-on-fault).
    private func iterateStreamSafe(_ pick: BrainEntry, messages: [ChatMessage],
                                   options: GenerationOptions?) -> AsyncStream<String?> {
        AsyncStream { continuation in
            let task = Task {
                for await chunk in pick.brain.stream(messages: messages, options: options) {
                    if Task.isCancelled { break }
                    continuation.yield(chunk)
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    private func pickAvailable(skip: Set<ObjectIdentifier>) -> BrainEntry? {
        let now = clock()
        for e in brains {
            if skip.contains(ObjectIdentifier(e)) { continue }
            e.gate.lock()
            let h = e.healthAt(now, coolDown: policy.coolDownDurationOrDefault)
            e.gate.unlock()
            if h == .healthy || h == .coolingDown { return e }
        }
        // None healthy — pick first untried brain anyway (degraded might recover).
        for e in brains {
            if !skip.contains(ObjectIdentifier(e)) { return e }
        }
        return nil
    }
}
