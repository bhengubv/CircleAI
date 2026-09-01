// CompanionHerJarvis.swift
//
// Four of the HER/Jarvis companion contracts and their real, working backings:
// episodic memory, identity sync, a personal knowledge graph, and live world
// knowledge.
//
// REAL BACKINGS, NOT NO-OPS. The point of these is that a host and a test both
// get BEHAVIOUR out of the box — term-frequency recall that actually ranks, a
// cursor that actually advances, a graph that actually walks. A production host
// that needs a cloud-scale variant swaps any of them behind the same protocol,
// but nobody has to write one to get something that works.
//
// Ported from src/CircleAI.Companion/HerJarvis{Contracts,RealImplementations}.cs.

import Foundation

// MARK: - Episodic memory

/// One lived experience.
public struct EpisodeRecord: Sendable, Equatable, Codable {
    public let id: String
    public let at: Date
    public let title: String
    public let contentJson: String

    public init(id: String, at: Date, title: String, contentJson: String) {
        self.id = id
        self.at = at
        self.title = title
        self.contentJson = contentJson
    }
}

public protocol IEpisodicMemory: Sendable {
    func record(_ episode: EpisodeRecord) async throws
    func recall(query: String, take: Int) async throws -> [EpisodeRecord]
}

public extension IEpisodicMemory {
    func recall(query: String) async throws -> [EpisodeRecord] {
        try await recall(query: query, take: 10)
    }
}

/// Recall by term-frequency overlap.
///
/// Deliberately not embeddings: this has to work on a phone with no model
/// loaded, and a dot product over word counts is something a Kirin 710 does in
/// microseconds. A host with an embedding index swaps it behind the protocol.
public final class TfEpisodicMemory: IEpisodicMemory, @unchecked Sendable {

    private let lock = NSLock()
    private var episodes: [String: EpisodeRecord] = [:]
    private var terms: [String: [String: Int]] = [:]

    public init() {}

    public func record(_ episode: EpisodeRecord) async throws {
        guard !episode.id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw HerJarvisError.invalidArgument("An id is required.")
        }
        // The TITLE is indexed alongside the content. It is the part a person
        // actually remembers, and leaving it out makes "the hospital thing"
        // find nothing.
        let tf = Self.termFrequency(episode.title + " " + episode.contentJson)
        lock.lock()
        episodes[episode.id] = episode
        terms[episode.id] = tf
        lock.unlock()
    }

    public func recall(query: String, take: Int) async throws -> [EpisodeRecord] {
        guard take > 0 else { throw HerJarvisError.invalidArgument("take must be greater than zero.") }

        let q = Self.termFrequency(query)
        guard !q.isEmpty else { return [] }

        lock.lock(); defer { lock.unlock() }

        var scored: [(episode: EpisodeRecord, score: Int)] = []
        for episode in episodes.values {
            let s = Self.score(q, terms[episode.id])
            if s > 0 { scored.append((episode, s)) }
        }

        // Ties broken by id so two equally-scoring episodes come back in the
        // same order every time. Without it a caller taking the top 1 gets a
        // different answer on each run and cannot reproduce anything.
        scored.sort { a, b in
            a.score != b.score ? a.score > b.score : a.episode.id < b.episode.id
        }
        return scored.prefix(take).map(\.episode)
    }

    /// Words of two characters or more, case-folded. One-character tokens are
    /// dropped because "a" and "I" match everything and rank nothing.
    static func termFrequency(_ text: String) -> [String: Int] {
        var out: [String: Int] = [:]
        var current = ""
        func flush() {
            if current.count >= 2 { out[current, default: 0] += 1 }
            current = ""
        }
        for ch in text.lowercased() {
            if ch.isLetter || ch.isNumber { current.append(ch) } else { flush() }
        }
        flush()
        return out
    }

    static func score(_ q: [String: Int], _ d: [String: Int]?) -> Int {
        guard let d else { return 0 }
        var s = 0
        for (term, count) in q {
            if let n = d[term] { s += count * n }
        }
        return s
    }
}

// MARK: - Identity sync

/// Push a delta, pull everything after a cursor.
public protocol IIdentitySync: Sendable {
    func push(deltaJson: String) async throws
    func pull(sinceCursor: String) async throws -> String
}

/// An append-only delta log with a monotonic cursor.
///
/// MONOTONIC AND NEVER REUSED, which is the whole contract: a puller that has
/// seen cursor 7 asks for everything after 7 and must not be handed something
/// it already has, or a device syncs the same change twice. Nothing is ever
/// removed from the log for the same reason.
public final class JsonIdentitySync: IIdentitySync, @unchecked Sendable {

    private let lock = NSLock()
    private var log: [(cursor: Int64, deltaJson: String)] = []
    private var next: Int64 = 0

    public init() {}

    public func push(deltaJson: String) async throws {
        lock.lock()
        next += 1
        log.append((next, deltaJson))
        lock.unlock()
    }

    public func pull(sinceCursor: String) async throws -> String {
        // An unparseable cursor is 0, not an error: a first-time puller sends
        // "" or "null", and refusing it would make the first sync the one that
        // fails.
        let since = Int64(sinceCursor) ?? 0

        lock.lock()
        let taken = log.filter { $0.cursor > since }
        let maxCursor = taken.last?.cursor ?? since
        let deltas = taken.map(\.deltaJson)
        lock.unlock()

        // The deltas are ALREADY JSON and are spliced in raw rather than
        // re-encoded: re-encoding would turn each one into a JSON string
        // containing JSON, and the puller would have to decode twice.
        return "{\"cursor\":\(maxCursor),\"deltas\":[\(deltas.joined(separator: ","))]}"
    }

    /// The highest cursor issued. Exposed so a caller can tell "nothing new"
    /// from "the log was reset", which a payload alone cannot show.
    public var currentCursor: Int64 {
        lock.lock(); defer { lock.unlock() }
        return next
    }
}

// MARK: - Personal knowledge graph

public struct KnowledgeRelation: Sendable, Equatable, Codable {
    public let fromId: String
    public let toId: String
    public let relation: String

    public init(fromId: String, toId: String, relation: String) {
        self.fromId = fromId
        self.toId = toId
        self.relation = relation
    }
}

public protocol IPersonalKnowledgeGraph: Sendable {
    func upsert(node: KnowledgeNode) async throws
    func upsert(relation: KnowledgeRelation) async throws
    func neighbours(of id: String) async throws -> [KnowledgeNode]
}

/// An adjacency list, in memory.
public final class AdjacencyPersonalKnowledgeGraph: IPersonalKnowledgeGraph, @unchecked Sendable {

    private let lock = NSLock()
    private var nodes: [String: KnowledgeNode] = [:]
    private var outEdges: [String: [KnowledgeRelation]] = [:]

    public init() {}

    public func upsert(node: KnowledgeNode) async throws {
        guard !node.id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw HerJarvisError.invalidArgument("An id is required.")
        }
        lock.lock(); nodes[node.id] = node; lock.unlock()
    }

    public func upsert(relation: KnowledgeRelation) async throws {
        lock.lock()
        var list = outEdges[relation.fromId] ?? []
        // UPSERT, not append: the same relation asserted twice is one edge, and
        // appending would make a node appear twice in its own neighbour list
        // for no reason a caller could see.
        list.removeAll { $0.toId == relation.toId && $0.relation == relation.relation }
        list.append(relation)
        outEdges[relation.fromId] = list
        lock.unlock()
    }

    public func neighbours(of id: String) async throws -> [KnowledgeNode] {
        guard !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw HerJarvisError.invalidArgument("An id is required.")
        }
        lock.lock(); defer { lock.unlock() }
        // An edge pointing at a node that was never upserted is SKIPPED rather
        // than being a hole in the array: a graph assembled from two sources
        // routinely has edges that arrive before their nodes.
        return (outEdges[id] ?? []).compactMap { nodes[$0.toId] }
    }
}

// MARK: - Live world knowledge

public struct WorldFact: Sendable, Equatable, Codable {
    public let topic: String
    public let summaryJson: String
    public let at: Date

    public init(topic: String, summaryJson: String, at: Date) {
        self.topic = topic
        self.summaryJson = summaryJson
        self.at = at
    }
}

public protocol ILiveWorldKnowledge: Sendable {
    func subscribe(topics: [String]) -> AsyncStream<WorldFact>
}

/// A topic broker.
///
/// A fact published to a topic NOBODY is subscribed to is dropped, deliberately.
/// The alternative is an unbounded buffer per topic filled by a feed that runs
/// whether anyone is listening or not, which on a phone is a memory leak with a
/// schedule.
public final class TopicLiveWorldKnowledge: ILiveWorldKnowledge, @unchecked Sendable {

    private let lock = NSLock()
    private var sinks: [String: [Int: AsyncStream<WorldFact>.Continuation]] = [:]
    private var nextId = 0

    public init() {}

    public func publish(_ fact: WorldFact) {
        lock.lock()
        let targets = Array((sinks[fact.topic] ?? [:]).values)
        lock.unlock()
        for c in targets { c.yield(fact) }
    }

    public func subscribe(topics: [String]) -> AsyncStream<WorldFact> {
        AsyncStream { continuation in
            lock.lock()
            nextId += 1
            let id = nextId
            for t in topics { sinks[t, default: [:]][id] = continuation }
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                for t in topics { self.sinks[t]?.removeValue(forKey: id) }
                self.lock.unlock()
            }
        }
    }

    /// How many live subscriptions a topic has. Exposed so a caller can tell
    /// "the feed is quiet" from "nobody is listening and every fact is being
    /// dropped" — which look identical from outside.
    public func subscriberCount(topic: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        return sinks[topic]?.count ?? 0
    }
}
