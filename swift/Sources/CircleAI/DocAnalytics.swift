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

    public var description: String {
        switch self {
        case .documentIdRequired: return "DocumentId required"
        case .documentIdArgRequired: return "documentId required"
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
