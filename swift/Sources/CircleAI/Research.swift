// Research.swift
//
// Port of src/CircleAI.Research/:
//   • Contracts.cs                 — ResearchPaper, Citation records;
//                                     IResearchCorpus, IPaperRetrieval,
//                                     ICitationGraph
//   • InMemoryResearch.cs          — substring-scored corpus, byte-store
//                                     retrieval, adjacency-list citation graph
//   • NullImplementations.cs       — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable`. `ReadOnlyMemory<byte>` → `[UInt8]`
//     (ResearchPaper/Citation are fully value-typed → also Equatable/Codable).
//   • `DateTimeOffset` → `Date`; `ValueTask` → `async throws`.
//   • Corpus search score: +3 title match, +1 abstract, +1 any author
//     (all case-insensitive substring); keeps score > 0, orders descending by
//     score, `Take(topK)`. Ties preserve C#'s stable OrderByDescending — Swift's
//     `sorted` is not guaranteed stable, so ties are broken by paperId to keep
//     output deterministic.
//   • Guards → `ResearchError`.

import Foundation

// MARK: - Records

/// A research paper with metadata + abstract.
public struct ResearchPaper: Sendable, Equatable, Codable {
    public let paperId: String
    public let title: String
    public let authors: [String]
    public let abstractText: String
    public let publishedAtUtc: Date
    public let doi: String?

    public init(paperId: String, title: String, authors: [String], abstractText: String, publishedAtUtc: Date, doi: String?) {
        self.paperId = paperId
        self.title = title
        self.authors = authors
        self.abstractText = abstractText
        self.publishedAtUtc = publishedAtUtc
        self.doi = doi
    }
}

/// A directed citation from one paper to another, with the citing context.
public struct Citation: Sendable, Equatable, Codable {
    public let fromPaperId: String
    public let toPaperId: String
    public let context: String

    public init(fromPaperId: String, toPaperId: String, context: String) {
        self.fromPaperId = fromPaperId
        self.toPaperId = toPaperId
        self.context = context
    }
}

// MARK: - Errors

public enum ResearchError: Error, Equatable, CustomStringConvertible {
    case paperIdRequired
    case topKOutOfRange

    public var description: String {
        switch self {
        case .paperIdRequired: return "paperId required"
        case .topKOutOfRange: return "topK out of range"
        }
    }
}

// MARK: - Contracts

public protocol IResearchCorpus: Sendable {
    var backendId: String { get }
    func get(paperId: String) async throws -> ResearchPaper?
    func search(query: String, topK: Int) async throws -> [ResearchPaper]
}

public protocol IPaperRetrieval: Sendable {
    var backendId: String { get }
    func fetchFullText(paperId: String) async throws -> [UInt8]?
}

public protocol ICitationGraph: Sendable {
    var backendId: String { get }
    func forwardCitations(paperId: String) async throws -> [Citation]
    func backwardCitations(paperId: String) async throws -> [Citation]
}

// MARK: - In-memory backends

/// Substring-scored in-memory research corpus.
public final class InMemoryResearchCorpus: IResearchCorpus, @unchecked Sendable {
    private let lock = NSLock()
    private var papers: [String: ResearchPaper] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func add(_ paper: ResearchPaper) {
        lock.lock(); defer { lock.unlock() }
        papers[paper.paperId] = paper
    }

    public func get(paperId: String) async throws -> ResearchPaper? {
        if paperId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ResearchError.paperIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        return papers[paperId]
    }

    public func search(query: String, topK: Int = 10) async throws -> [ResearchPaper] {
        if topK <= 0 { throw ResearchError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let scored = papers.values
            .map { (paper: $0, score: InMemoryResearchCorpus.score($0, query)) }
            .filter { $0.score > 0 }
            .sorted { a, b in
                if a.score != b.score { return a.score > b.score }
                return a.paper.paperId < b.paper.paperId
            }
            .prefix(topK)
            .map { $0.paper }
        return Array(scored)
    }

    private static func score(_ p: ResearchPaper, _ q: String) -> Int {
        var s = 0
        if p.title.range(of: q, options: .caseInsensitive) != nil { s += 3 }
        if p.abstractText.range(of: q, options: .caseInsensitive) != nil { s += 1 }
        if p.authors.contains(where: { $0.range(of: q, options: .caseInsensitive) != nil }) { s += 1 }
        return s
    }
}

/// Byte-store full-text retrieval.
public final class InMemoryPaperRetrieval: IPaperRetrieval, @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [String: [UInt8]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func add(paperId: String, fullText: [UInt8]) {
        lock.lock(); defer { lock.unlock() }
        texts[paperId] = fullText
    }

    public func fetchFullText(paperId: String) async throws -> [UInt8]? {
        if paperId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ResearchError.paperIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        return texts[paperId]
    }
}

/// Adjacency-list citation graph (forward + backward indices).
public final class InMemoryCitationGraph: ICitationGraph, @unchecked Sendable {
    private let lock = NSLock()
    private var forward: [String: [Citation]] = [:]
    private var backward: [String: [Citation]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func link(_ c: Citation) {
        lock.lock(); defer { lock.unlock() }
        forward[c.fromPaperId, default: []].append(c)
        backward[c.toPaperId, default: []].append(c)
    }

    public func forwardCitations(paperId: String) async throws -> [Citation] {
        if paperId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ResearchError.paperIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        return forward[paperId] ?? []
    }

    public func backwardCitations(paperId: String) async throws -> [Citation] {
        if paperId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw ResearchError.paperIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        return backward[paperId] ?? []
    }
}

// MARK: - Null backends

public struct NullResearchCorpus: IResearchCorpus {
    public static let instance = NullResearchCorpus()
    public init() {}
    public var backendId: String { "null" }
    public func get(paperId: String) async throws -> ResearchPaper? { nil }
    public func search(query: String, topK: Int = 10) async throws -> [ResearchPaper] { [] }
}

public struct NullPaperRetrieval: IPaperRetrieval {
    public static let instance = NullPaperRetrieval()
    public init() {}
    public var backendId: String { "null" }
    public func fetchFullText(paperId: String) async throws -> [UInt8]? { nil }
}

public struct NullCitationGraph: ICitationGraph {
    public static let instance = NullCitationGraph()
    public init() {}
    public var backendId: String { "null" }
    public func forwardCitations(paperId: String) async throws -> [Citation] { [] }
    public func backwardCitations(paperId: String) async throws -> [Citation] { [] }
}
