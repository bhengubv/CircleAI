// DocAnalytics.swift
//
// Port of src/CircleAI.DocAnalytics/:
//   • Contracts.cs                 — DocumentView, DocumentInsight records;
//                                     IDocumentTracker, IDocumentInsights
//   • InMemoryDocumentTracker.cs   — thread-safe in-memory tracker + insights
//   • NullImplementations.cs       — drop-all Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable, Equatable, Codable` with explicit init.
//   • `DateTimeOffset` → `Date`; `TimeSpan` → `TimeInterval` (seconds).
//   • `ValueTask`-returning methods become `async throws`.
//   • `ArgumentException` guards → `DocAnalyticsError`.
//   • ConcurrentDictionary + write-lock → a single `NSLock` around a
//     `[String: [DocumentView]]`.

import Foundation

// MARK: - Records

/// One recorded view of a document by a viewer.
public struct DocumentView: Sendable, Equatable, Codable {
    public let documentId: String
    public let viewerId: String
    public let atUtc: Date
    public let duration: TimeInterval
    public let pagesViewed: Int

    public init(documentId: String, viewerId: String, atUtc: Date, duration: TimeInterval, pagesViewed: Int) {
        self.documentId = documentId
        self.viewerId = viewerId
        self.atUtc = atUtc
        self.duration = duration
        self.pagesViewed = pagesViewed
    }
}

/// Aggregate insight computed over a document's views.
public struct DocumentInsight: Sendable, Equatable, Codable {
    public let documentId: String
    public let totalViews: Int
    public let uniqueViewers: Int
    public let avgDurationSeconds: Double

    public init(documentId: String, totalViews: Int, uniqueViewers: Int, avgDurationSeconds: Double) {
        self.documentId = documentId
        self.totalViews = totalViews
        self.uniqueViewers = uniqueViewers
        self.avgDurationSeconds = avgDurationSeconds
    }
}

// MARK: - Errors

public enum DocAnalyticsError: Error, Equatable, CustomStringConvertible {
    case documentIdRequired
    case documentIdArgRequired
    case topKOutOfRange
    case limitOutOfRange

    public var description: String {
        switch self {
        case .documentIdRequired: return "DocumentId required"
        case .documentIdArgRequired: return "documentId required"
        case .topKOutOfRange: return "topK out of range"
        case .limitOutOfRange: return "limit out of range"
        }
    }
}

// MARK: - Contracts

/// Records document views.
public protocol IDocumentTracker: Sendable {
    var backendId: String { get }
    func recordView(_ view: DocumentView) async throws
    func listViews(documentId: String) async throws -> [DocumentView]
}

/// Computes aggregate insights over recorded views.
public protocol IDocumentInsights: Sendable {
    var backendId: String { get }
    func compute(documentId: String) async throws -> DocumentInsight?
}

// MARK: - In-memory backend

/// Thread-safe in-memory document tracker + insights. Records every view in a
/// per-document list and computes insights on demand.
public final class InMemoryDocumentTracker: IDocumentTracker, IDocumentInsights, @unchecked Sendable {
    private let lock = NSLock()
    private var byDoc: [String: [DocumentView]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func recordView(_ view: DocumentView) async throws {
        if view.documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        byDoc[view.documentId, default: []].append(view)
    }

    public func listViews(documentId: String) async throws -> [DocumentView] {
        if documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdArgRequired
        }
        lock.lock(); defer { lock.unlock() }
        return byDoc[documentId] ?? []
    }

    public func compute(documentId: String) async throws -> DocumentInsight? {
        if documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdArgRequired
        }
        lock.lock(); defer { lock.unlock() }
        guard let views = byDoc[documentId], !views.isEmpty else { return nil }

        let total = views.count
        let unique = Set(views.map { $0.viewerId }).count
        let avgSeconds = views.reduce(0.0) { $0 + $1.duration } / Double(views.count)

        return DocumentInsight(
            documentId: documentId,
            totalViews: total,
            uniqueViewers: unique,
            avgDurationSeconds: avgSeconds)
    }

    // ── Synchronous analytics extras (concrete-only; C# ArgumentException guards
    //    become throwing Swift functions; property getters are non-throwing) ──

    /// Number of distinct documents with at least one recorded view (matches
    /// C#'s `DocumentCount`).
    public var documentCount: Int {
        lock.lock(); defer { lock.unlock() }
        return byDoc.count
    }

    /// Total views recorded across every tracked document (matches C#'s
    /// `TotalViews`).
    public var totalViews: Int {
        lock.lock(); defer { lock.unlock() }
        return byDoc.values.reduce(0) { $0 + $1.count }
    }

    /// Drop all recorded views for a document. Returns true if anything was
    /// removed (matches C#'s `Clear`; throws on a blank id like C#'s
    /// ArgumentException).
    @discardableResult
    public func clear(documentId: String) throws -> Bool {
        if documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdArgRequired
        }
        lock.lock(); defer { lock.unlock() }
        return byDoc.removeValue(forKey: documentId) != nil
    }

    /// The most-viewed documents, highest first, capped at `topK` (default 5).
    /// Matches C#'s `TopDocuments` → `OrderByDescending(Views).Take(topK)`;
    /// throws on `topK <= 0` like C#'s ArgumentOutOfRangeException.
    public func topDocuments(topK: Int = 5) throws -> [(documentId: String, views: Int)] {
        if topK <= 0 { throw DocAnalyticsError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let ranked = byDoc
            .map { (documentId: $0.key, views: $0.value.count) }
            .sorted { $0.views > $1.views }
        return Array(ranked.prefix(topK))
    }

    /// Most recent views for a document, newest first, capped at `limit`
    /// (default 20). Matches C#'s `RecentViews` → `OrderByDescending(AtUtc)
    /// .Take(limit)`; throws on a blank id or `limit <= 0` like C#.
    public func recentViews(documentId: String, limit: Int = 20) throws -> [DocumentView] {
        if documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdArgRequired
        }
        if limit <= 0 { throw DocAnalyticsError.limitOutOfRange }
        lock.lock(); defer { lock.unlock() }
        guard let views = byDoc[documentId] else { return [] }
        return Array(views.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }

    /// Sum of pages viewed across every recorded view of a document (0 when the
    /// document is unknown). Matches C#'s `TotalPagesViewed`; throws on a blank
    /// id like C#.
    public func totalPagesViewed(documentId: String) throws -> Int {
        if documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdArgRequired
        }
        lock.lock(); defer { lock.unlock() }
        return (byDoc[documentId] ?? []).reduce(0) { $0 + $1.pagesViewed }
    }

    /// The viewer who spent the most cumulative time on a document, or nil when
    /// there are no views. Matches C#'s `MostEngagedViewer` (groups by viewerId
    /// ordinally by total duration; ties keep first-appearance order); throws on
    /// a blank id like C#.
    public func mostEngagedViewer(documentId: String) throws -> String? {
        if documentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DocAnalyticsError.documentIdArgRequired
        }
        lock.lock(); defer { lock.unlock() }
        guard let views = byDoc[documentId], !views.isEmpty else { return nil }
        var order: [String] = []            // viewerIds in first-seen order
        var totals: [String: Double] = [:]
        for v in views {
            if totals[v.viewerId] == nil { order.append(v.viewerId) }
            totals[v.viewerId, default: 0] += v.duration
        }
        return order.enumerated()
            .sorted { a, b in
                let ta = totals[a.element]!, tb = totals[b.element]!
                if ta != tb { return ta > tb }
                return a.offset < b.offset
            }
            .first!
            .element
    }
}

// MARK: - Null backends

/// Fail-closed tracker: records nothing, lists nothing.
public final class NullDocumentTracker: IDocumentTracker, @unchecked Sendable {
    public static let instance = NullDocumentTracker()
    public init() {}
    public var backendId: String { "null" }
    public func recordView(_ view: DocumentView) async throws {}
    public func listViews(documentId: String) async throws -> [DocumentView] { [] }
}

/// Fail-closed insights: always nil.
public final class NullDocumentInsights: IDocumentInsights, @unchecked Sendable {
    public static let instance = NullDocumentInsights()
    public init() {}
    public var backendId: String { "null" }
    public func compute(documentId: String) async throws -> DocumentInsight? { nil }
}
