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
