// MemoryGraph.swift
// Personal knowledge graph + HippoRAG multi-hop recall (Personalised PageRank).
// Ported from Circle.AI.Domain (MemoryItem/MemoryHit/IHippoRagStore) and
// Circle.AI.Companion (SqliteKnowledgeGraph, SqliteHippoRagStore) — the C#
// reference, mirroring the TS/Go/Python/Kotlin/Rust ports. In-memory; identical
// algorithms, no SQLite.

import Foundation

/// Errors raised by the memory-brain layer.
public enum BrainError: Error, Sendable {
    case invalidArgument(String)
}

// MARK: - Shared recall currency

/// One recallable memory with optional string metadata.
public struct MemoryItem: Sendable {
    public let id: String
    public let text: String
    public let metadata: [String: String]?
    public init(id: String, text: String, metadata: [String: String]? = nil) {
        self.id = id
        self.text = text
        self.metadata = metadata
    }
}

/// A recalled memory paired with its relevance score.
public struct MemoryHit: Sendable {
    public let item: MemoryItem
    public let score: Double
    public init(item: MemoryItem, score: Double) {
        self.item = item
        self.score = score
    }
}

// MARK: - Knowledge graph node + triple

public struct KnowledgeNode: Sendable {
    public let id: String
    public let kind: String
    public let name: String
    public let properties: [String: String]?
    public init(id: String, kind: String, name: String, properties: [String: String]? = nil) {
        self.id = id
        self.kind = kind
        self.name = name
        self.properties = properties
    }
}

public struct KnowledgeTriple: Sendable {
    public let subject: String
    public let predicate: String
    public let object: String
    public let source: String?
    public let confidence: Float
    public let recordedAt: Date
    public init(subject: String, predicate: String, object: String,
                source: String?, confidence: Float, recordedAt: Date) {
        self.subject = subject
        self.predicate = predicate
        self.object = object
        self.source = source
        self.confidence = confidence
        self.recordedAt = recordedAt
    }
}

/// HippoRAG-pattern memory + knowledge-graph + Personalised PageRank recall.
public protocol IHippoRagStore {
    var backendId: String { get }
    func index(_ item: MemoryItem) async throws
    func multiHopRecall(query: String, topK: Int) async throws -> [MemoryHit]
}

// MARK: - InMemoryKnowledgeGraph

/// In-memory personal knowledge graph. Triples are keyed by (subject, predicate,
/// object) — re-adding the same triple replaces its provenance, matching the C#
/// store's `INSERT OR REPLACE`.
public final class InMemoryKnowledgeGraph: @unchecked Sendable {
    private let lock = NSLock()
    private var nodes: [String: KnowledgeNode] = [:]
    private var triples: [String: KnowledgeTriple] = [:]

    public init() {}

    public func upsertNode(_ node: KnowledgeNode) {
        lock.lock(); defer { lock.unlock() }
        nodes[node.id] = node
    }

    public func getNode(_ id: String) -> KnowledgeNode? {
        lock.lock(); defer { lock.unlock() }
        return nodes[id]
    }

    public func addTriple(subject: String, predicate: String, object: String,
                          source: String?, confidence: Float) {
        precondition(!subject.isEmpty && !predicate.isEmpty && !object.isEmpty, "s/p/o required")
        precondition(confidence >= 0 && confidence <= 1, "confidence in [0,1]")
        let key = subject + "\u{0}" + predicate + "\u{0}" + object
        lock.lock(); defer { lock.unlock() }
        triples[key] = KnowledgeTriple(subject: subject, predicate: predicate, object: object,
                                       source: source, confidence: confidence, recordedAt: Date())
    }

    public func allTriples() -> [KnowledgeTriple] {
        lock.lock(); defer { lock.unlock() }
        return Array(triples.values)
    }

    public func readTriples(subject: String) -> [KnowledgeTriple] {
        lock.lock(); defer { lock.unlock() }
        return triples.values.filter { $0.subject == subject }
    }
}

// MARK: - InMemoryHippoRagStore (Personalised PageRank)

/// Real HippoRAG recall over an `InMemoryKnowledgeGraph`, seeded from the query's
/// terms. Three precision guarantees carried from the C# reference: no-seed → empty,
/// seeds excluded from results, confidence-weighted edge spread.
public final class InMemoryHippoRagStore: IHippoRagStore, @unchecked Sendable {
    private let kg: InMemoryKnowledgeGraph
    private let walkIterations: Int
    private let damping: Double

    public init(_ kg: InMemoryKnowledgeGraph, walkIterations: Int = 32, damping: Double = 0.85) {
        self.kg = kg
        self.walkIterations = walkIterations
        self.damping = damping
    }

    public var backendId: String { "inmemory-hippo-ppr" }

    public func index(_ item: MemoryItem) async throws {
        kg.addTriple(subject: item.id, predicate: "memory_text", object: item.text,
                     source: item.id, confidence: 1.0)
        if let md = item.metadata {
            for (k, v) in md {
                kg.addTriple(subject: item.id, predicate: k, object: v, source: item.id, confidence: 0.9)
            }
        }
    }

    public func multiHopRecall(query: String, topK: Int) async throws -> [MemoryHit] {
        guard topK > 0 else { throw BrainError.invalidArgument("topK must be positive") }
        guard !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw BrainError.invalidArgument("query required")
        }

        let triples = kg.allTriples()
        if triples.isEmpty { return [] }

        var outgoing: [String: [(String, Float)]] = [:]
        var allNodes = Set<String>()
        for t in triples {
            allNodes.insert(t.subject)
            allNodes.insert(t.object)
            outgoing[t.subject, default: []].append((t.object, t.confidence))
        }

        let queryTerms = Set(splitAlnum(query).map { $0.lowercased() })
        let seedNodes = allNodes.filter { queryTerms.contains($0.lowercased()) }
        // Precision guarantee 1: no genuine association → return nothing.
        if seedNodes.isEmpty { return [] }

        var rank: [String: Double] = [:]
        for n in allNodes { rank[n] = 0 }
        let seedMass = 1.0 / Double(seedNodes.count)
        for s in seedNodes { rank[s] = seedMass }

        for _ in 0..<walkIterations {
            var next: [String: Double] = [:]
            for n in allNodes { next[n] = 0 }
            for seed in seedNodes { next[seed]! += (1 - damping) * seedMass }
            for (node, mass) in rank {
                if mass <= 0 { continue }
                guard let nbrs = outgoing[node], !nbrs.isEmpty else {
                    for seed in seedNodes { next[seed]! += damping * mass / Double(seedNodes.count) }
                    continue
                }
                var totalConf: Float = 0
                for (_, c) in nbrs { totalConf += c }
                for (nbr, c) in nbrs {
                    let weight = totalConf > 0 ? Double(c) / Double(totalConf) : 1.0 / Double(nbrs.count)
                    next[nbr]! += damping * mass * weight
                }
            }
            rank = next
        }

        // Precision guarantee 2: exclude the seeds — the query's own terms.
        let seedSet = Set(seedNodes)
        let ranked = rank
            .filter { $0.value > 0 && !seedSet.contains($0.key) }
            .sorted { $0.value > $1.value }
            .prefix(topK)
        return ranked.map { key, value in
            let node = kg.getNode(key)
            let item = MemoryItem(id: key, text: node?.name ?? key, metadata: node?.properties)
            return MemoryHit(item: item, score: value)
        }
    }
}

/// Split on runs of non-ASCII-alphanumeric characters (mirrors C# `[^A-Za-z0-9]+`).
func splitAlnum(_ s: String) -> [String] {
    s.split(whereSeparator: { c in !(c.isASCII && (c.isLetter || c.isNumber)) }).map(String.init)
}
