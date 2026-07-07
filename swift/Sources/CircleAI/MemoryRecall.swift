// MemoryRecall.swift
// Fused associative recall (Reciprocal Rank Fusion). Ported from
// Circle.AI.Companion (IRecall, FusedRecall) — the C# reference.
//
// Fuses episodic cosine similarity + graph association (Personalised PageRank)
// into one ranked context. RRF combines ranked lists by position, so it needs no
// shared score scale: each source contributes 1 / (k + rank). Cold-start is
// automatic — an empty graph means only episodic contributes.

import Foundation

/// Unified memory recall — the most relevant memories for a turn.
public protocol IRecall {
    func recall(query: String, queryEmbedding: [Float]?, topK: Int) async throws -> [MemoryHit]
}

/// Tuning for `FusedRecall`.
public struct FusedRecallOptions: Sendable {
    public var candidatePoolSize: Int
    public var rrfK: Int
    public var graphConfidenceThreshold: Float
    public init(candidatePoolSize: Int = 20, rrfK: Int = 60, graphConfidenceThreshold: Float = 0.4) {
        self.candidatePoolSize = candidatePoolSize
        self.rrfK = rrfK
        self.graphConfidenceThreshold = graphConfidenceThreshold
    }
}

/// Reciprocal-Rank-Fusion recall over episodic similarity + graph association.
public final class FusedRecall: IRecall, @unchecked Sendable {
    private let episodic: IEpisodicMemoryStore
    private let graph: IHippoRagStore?
    private let options: FusedRecallOptions

    public init(episodic: IEpisodicMemoryStore, graph: IHippoRagStore? = nil,
                options: FusedRecallOptions = FusedRecallOptions()) {
        self.episodic = episodic
        self.graph = graph
        self.options = options
    }

    public func recall(query: String, queryEmbedding: [Float]?, topK: Int = 5) async throws -> [MemoryHit] {
        guard topK > 0 else { throw BrainError.invalidArgument("topK must be positive") }
        let pool = options.candidatePoolSize

        // Fast path: episodic similarity (or recency when the embedding is nil).
        let episodicHits = try await episodic.search(queryEmbedding: queryEmbedding, topK: pool)

        // Slow path: graph association — optional and best-effort. Empty query
        // can't seed a walk; a failing graph degrades to pure episodic.
        var graphHits: [MemoryHit] = []
        if let g = graph, !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            do { graphHits = try await g.multiHopRecall(query: query, topK: pool) }
            catch { graphHits = [] }
        }

        // Reciprocal Rank Fusion, keyed by normalised text so a memory surfaced by
        // both sources reinforces rather than duplicates.
        let k = Double(options.rrfK)
        var fused: [String: (item: MemoryItem, score: Double)] = [:]
        var order: [String] = []

        func accumulate(_ item: MemoryItem, _ oneBasedRank: Int) {
            let key = normaliseKey(item.text)
            if key.isEmpty { return }
            let contribution = 1.0 / (k + Double(oneBasedRank))
            if var existing = fused[key] {
                existing.score += contribution
                fused[key] = existing
            } else {
                fused[key] = (item, contribution)
                order.append(key)
            }
        }

        for (i, e) in episodicHits.enumerated() { accumulate(adaptEpisodic(e), i + 1) }
        for (i, h) in graphHits.enumerated() {
            if isBelowConfidence(h, options.graphConfidenceThreshold) { continue }
            accumulate(h.item, i + 1)
        }

        // Sort by score desc; equal scores keep first-seen (insertion) order.
        let sortedKeys = order.enumerated().sorted { lhs, rhs in
            let ls = fused[lhs.element]!.score, rs = fused[rhs.element]!.score
            if ls != rs { return ls > rs }
            return lhs.offset < rhs.offset
        }.map { $0.element }

        return sortedKeys.prefix(topK).map { MemoryHit(item: fused[$0]!.item, score: fused[$0]!.score) }
    }
}

private func isBelowConfidence(_ hit: MemoryHit, _ threshold: Float) -> Bool {
    guard let md = hit.item.metadata, let raw = md["confidence"] else { return false }
    guard let c = Float(raw), c.isFinite else { return false }
    return c < threshold
}

private func adaptEpisodic(_ e: EpisodicMemoryEntry) -> MemoryItem {
    let iso = ISO8601DateFormatter()
    var meta: [String: String] = ["source": "episodic", "recordedAt": iso.string(from: e.recordedAt)]
    if !e.assistantText.isEmpty { meta["assistantText"] = e.assistantText }
    if let ac = e.appContext, !ac.isEmpty { meta["appContext"] = ac }
    return MemoryItem(id: e.id.uuidString, text: e.userText, metadata: meta)
}

/// Lowercase + collapse internal whitespace so equivalent texts fuse to one key.
private func normaliseKey(_ text: String) -> String {
    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
    if trimmed.isEmpty { return "" }
    var out = ""
    var prevSpace = false
    for ch in trimmed {
        if ch.isWhitespace {
            if !prevSpace { out.append(" "); prevSpace = true }
        } else {
            out += String(ch).lowercased()
            prevSpace = false
        }
    }
    return out
}
