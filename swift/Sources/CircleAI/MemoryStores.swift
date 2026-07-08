// MemoryStores.swift
// Concrete in-memory episodic store. Ported from Circle.AI.Memory
// (InMemoryEpisodicStore) — the C# reference. Cosine=dot similarity search,
// recency fallback, FIFO capacity cap. In-memory; persistence is a later slice.

import Foundation

/// In-memory `IEpisodicMemoryStore` with FIFO capacity eviction.
public final class InMemoryEpisodicStore: IEpisodicMemoryStore, @unchecked Sendable {
    private let lock = NSLock()
    private var entries: [EpisodicMemoryEntry] = []
    private let maxEntries: Int

    public init(maxEntries: Int = 1000) {
        precondition(maxEntries > 0, "maxEntries must be positive")
        self.maxEntries = maxEntries
    }

    public func add(_ entry: EpisodicMemoryEntry) async throws {
        lock.lock(); defer { lock.unlock() }
        entries.append(entry)
        while entries.count > maxEntries { entries.removeFirst() }
    }

    public func search(queryEmbedding: [Float]?, topK: Int) async throws -> [EpisodicMemoryEntry] {
        lock.lock(); let snapshot = entries; lock.unlock()

        if let qe = queryEmbedding, !qe.isEmpty {
            // Cosine similarity (both vectors L2-normalised → cosine == dot),
            // only against entries whose embedding matches the query dimension.
            let scored = snapshot
                .filter { $0.embedding != nil && $0.embedding!.count == qe.count }
                .map { (entry: $0, score: cosineDot(qe, $0.embedding!)) }
                .sorted { $0.score > $1.score }
            return Array(scored.prefix(topK).map { $0.entry })
        } else {
            let sorted = snapshot.sorted { $0.recordedAt > $1.recordedAt }
            return Array(sorted.prefix(topK))
        }
    }

    public func getRecent(count: Int) async throws -> [EpisodicMemoryEntry] {
        lock.lock(); let snapshot = entries; lock.unlock()
        let sorted = snapshot.sorted { $0.recordedAt > $1.recordedAt }
        return Array(sorted.prefix(count))
    }

    public func count() async throws -> Int {
        lock.lock(); defer { lock.unlock() }
        return entries.count
    }

    public func pruneOlderThan(cutoff: Date) async throws -> Int {
        lock.lock(); defer { lock.unlock() }
        let before = entries.count
        entries.removeAll { $0.recordedAt < cutoff }
        return before - entries.count
    }
}

/// Dot product of two equal-length, L2-normalised vectors (== cosine similarity).
func cosineDot(_ a: [Float], _ b: [Float]) -> Float {
    var dot: Float = 0
    for i in 0..<a.count { dot += a[i] * b[i] }
    return dot
}

/// In-memory `IPersonaStore`. Keyed by userId; `load` returns a fresh default
/// `PersonaState` (stamped with the requested userId) when no persona has been
/// persisted for that user. Ported from Circle.AI.Memory — the C# reference.
public final class InMemoryPersonaStore: IPersonaStore, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [String: PersonaState] = [:]

    public init() {}

    // Synchronous, lock-guarded accessors — safe to call from async contexts
    // (the lock is never held across an await).
    private func fetch(_ userId: String) -> PersonaState? {
        lock.lock(); defer { lock.unlock() }
        return store[userId]
    }

    private func put(_ persona: PersonaState) {
        lock.lock(); defer { lock.unlock() }
        store[persona.userId] = persona
    }

    public func load(userId: String) async throws -> PersonaState {
        if let existing = fetch(userId) { return existing }
        let fresh = PersonaState()
        fresh.userId = userId
        return fresh
    }

    public func save(_ persona: PersonaState) async throws {
        put(persona)
    }
}

/// In-memory `IFeedbackStore`. Ported from Circle.AI.Memory
/// (InMemoryFeedbackStore) — the C# reference. Data is lost on process exit;
/// for tests and headless CLI use. Capacity is capped (FIFO eviction).
public final class InMemoryFeedbackStore: IFeedbackStore, @unchecked Sendable {
    private let lock = NSLock()
    private var signals: [FeedbackSignal] = []
    private let maxSignals: Int

    /// - Parameter maxSignals: Cap on stored signals; when exceeded the oldest
    ///   are evicted (FIFO). Default 10000.
    public init(maxSignals: Int = 10_000) {
        precondition(maxSignals > 0, "maxSignals must be positive")
        self.maxSignals = maxSignals
    }

    public func add(_ signal: FeedbackSignal) async throws {
        lock.lock(); defer { lock.unlock() }
        signals.append(signal)
        while signals.count > maxSignals { signals.removeFirst() }
    }

    public func getRecent(count: Int) async throws -> [FeedbackSignal] {
        lock.lock(); let snapshot = signals; lock.unlock()
        let sorted = snapshot.sorted { $0.recordedAt > $1.recordedAt }
        return Array(sorted.prefix(count))
    }

    public func count() async throws -> Int {
        lock.lock(); defer { lock.unlock() }
        return signals.count
    }

    public func positiveRatio() async throws -> Double? {
        lock.lock(); defer { lock.unlock() }
        if signals.isEmpty { return nil }
        let pos = signals.filter { $0.polarity == .positive }.count
        return Double(pos) / Double(signals.count)
    }
}
