// CompanionMemoryEncoder.swift
// Background writer: turn → knowledge graph + attributed beliefs, off the hot
// path. Ported from Circle.AI.Companion (CompanionMemoryEncoder).
//
// `enqueue` is synchronous and non-blocking; a full queue drops rather than
// blocks (DropWrite). `close()` drains the buffered turns. Draining is gated on
// close (like the Go/Kotlin/Rust ports) so drop-on-full stays deterministic under
// genuine concurrency; every observable outcome matches the reference.

import Foundation

public final class CompanionMemoryEncoder: @unchecked Sendable {
    private struct EncodeJob {
        let userText: String
        let assistantText: String
        let episodeId: String
    }

    private let extractor: IKnowledgeGraphExtractor
    private let graph: InMemoryKnowledgeGraph
    private let beliefExtractor: IBeliefExtractor?
    private let beliefs: SelfBeliefStore?
    private let capacity: Int

    private let lock = NSLock()
    private var queue: [EncodeJob] = []
    private var closed = false

    /// First error hit while draining, if any (diagnostics).
    public private(set) var lastError: Error?

    public init(extractor: IKnowledgeGraphExtractor, graph: InMemoryKnowledgeGraph,
                beliefExtractor: IBeliefExtractor? = nil, beliefs: SelfBeliefStore? = nil, capacity: Int = 256) {
        self.extractor = extractor
        self.graph = graph
        self.beliefExtractor = beliefExtractor
        self.beliefs = beliefs
        self.capacity = max(1, capacity)
    }

    /// Hand a turn to the encoder. Non-blocking; returns immediately.
    public func enqueue(userText: String, assistantText: String, episodeId: String) {
        if episodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return }
        lock.lock(); defer { lock.unlock() }
        if closed { return }
        if queue.count >= capacity { return } // DropWrite: never block a turn
        queue.append(EncodeJob(userText: userText, assistantText: assistantText, episodeId: episodeId))
    }

    /// Stops accepting work and drains the queued turns into the graph + beliefs.
    public func close() async {
        lock.lock()
        closed = true
        let jobs = queue
        queue.removeAll()
        lock.unlock()

        for job in jobs {
            do {
                // Give the memory node a readable name so recall hands back the
                // actual exchange, not an opaque id.
                graph.upsertNode(KnowledgeNode(id: job.episodeId, kind: "memory", name: job.userText, properties: [:]))

                let triples = try await extractor.extractFromTurn(
                    userText: job.userText, assistantText: job.assistantText, sourceEpisodeId: job.episodeId)
                for t in triples {
                    graph.addTriple(subject: t.subject, predicate: t.predicate, object: t.object,
                                    source: t.source, confidence: t.confidence)
                }

                // Attributed beliefs — a third party's fact never becomes the user's.
                if let be = beliefExtractor, let store = beliefs {
                    for b in try await be.extract(text: job.userText, source: job.episodeId) {
                        store.record(b)
                    }
                }
            } catch {
                if lastError == nil { lastError = error }
            }
        }
    }
}
