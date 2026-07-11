// Hr.swift
//
// Port of the HR vertical from src/CircleAI.HR/HRPrimitives.cs and the static
// domain-context constants from HRDomainContext.cs:
//   • Employee, LeaveRequest, PerformanceReview — domain records
//   • IHRBoard              — hiring, leave management, performance reviews
//   • InMemoryHRBoard       — deterministic in-memory impl
//   • HRDomainContext       — system-prompt snippet + flags
//
// The Companion-facing wrapper (HRCompanionAdapter) is intentionally NOT ported
// (it wraps the Companion session infrastructure, not board state).
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTime` → `Date`.
//   • `DecideLeave` on an unknown request throws → `HRError.unknownLeaveRequest`.
//   • `Employees` is ordered ascending by Name. `PendingLeaves` filters
//     case-insensitively on Status == "Pending".
//   • `AvgRatingFor` averages RatingOutOf5 for the employee; 0.0 when none.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// An employee.
public struct Employee: Sendable, Equatable, Codable {
    public let employeeId: String
    public let name: String
    public let role: String
    public let hiredOn: Date
    public let salary: Decimal
    public let currency: String

    public init(employeeId: String, name: String, role: String, hiredOn: Date, salary: Decimal, currency: String) {
        self.employeeId = employeeId
        self.name = name
        self.role = role
        self.hiredOn = hiredOn
        self.salary = salary
        self.currency = currency
    }
}

/// A leave request.
public struct LeaveRequest: Sendable, Equatable, Codable {
    public let requestId: String
    public let employeeId: String
    public let kind: String
    public let from: Date
    public let to: Date
    public let status: String

    public init(requestId: String, employeeId: String, kind: String, from: Date, to: Date, status: String) {
        self.requestId = requestId
        self.employeeId = employeeId
        self.kind = kind
        self.from = from
        self.to = to
        self.status = status
    }
}

/// A performance review.
public struct PerformanceReview: Sendable, Equatable, Codable {
    public let reviewId: String
    public let employeeId: String
    public let reviewedOn: Date
    public let ratingOutOf5: Int
    public let notes: String

    public init(reviewId: String, employeeId: String, reviewedOn: Date, ratingOutOf5: Int, notes: String) {
        self.reviewId = reviewId
        self.employeeId = employeeId
        self.reviewedOn = reviewedOn
        self.ratingOutOf5 = ratingOutOf5
        self.notes = notes
    }
}

// MARK: - Errors

public enum HRError: Error, Equatable, CustomStringConvertible {
    case unknownLeaveRequest(String)

    public var description: String {
        switch self {
        case .unknownLeaveRequest(let id): return "Unknown leave request \(id)"
        }
    }
}

// MARK: - Contract

/// Hiring, leave management, and performance reviews for the HR vertical.
public protocol IHRBoard: AnyObject, Sendable {
    func hire(_ e: Employee)
    func getEmployee(_ id: String) -> Employee?
    var employees: [Employee] { get }
    func request(_ r: LeaveRequest)
    func decideLeave(requestId: String, decision: String) throws
    func pendingLeaves() -> [LeaveRequest]
    func review(_ r: PerformanceReview)
    func avgRatingFor(_ employeeId: String) -> Double
}

// MARK: - InMemoryHRBoard

/// Deterministic in-memory `IHRBoard`. All state guarded by a single `NSLock`.
public final class InMemoryHRBoard: IHRBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var employeesMap: [String: Employee] = [:]
    private var leaves: [String: LeaveRequest] = [:]
    private var reviews: [PerformanceReview] = []

    public init() {}

    public func hire(_ e: Employee) {
        lock.lock(); defer { lock.unlock() }
        employeesMap[e.employeeId] = e
    }

    public func getEmployee(_ id: String) -> Employee? {
        lock.lock(); defer { lock.unlock() }
        return employeesMap[id]
    }

    public var employees: [Employee] {
        lock.lock(); defer { lock.unlock() }
        return employeesMap.values.sorted { $0.name < $1.name }
    }

    public func request(_ r: LeaveRequest) {
        lock.lock(); defer { lock.unlock() }
        leaves[r.requestId] = r
    }

    public func decideLeave(requestId: String, decision: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let r = leaves[requestId] else { throw HRError.unknownLeaveRequest(requestId) }
        leaves[requestId] = LeaveRequest(requestId: r.requestId, employeeId: r.employeeId, kind: r.kind,
                                         from: r.from, to: r.to, status: decision)
    }

    public func pendingLeaves() -> [LeaveRequest] {
        lock.lock(); defer { lock.unlock() }
        return leaves.values.filter { $0.status.caseInsensitiveCompare("Pending") == .orderedSame }
    }

    public func review(_ r: PerformanceReview) {
        lock.lock(); defer { lock.unlock() }
        reviews.append(r)
    }

    public func avgRatingFor(_ employeeId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let ratings = reviews.filter { $0.employeeId == employeeId }.map { Double($0.ratingOutOf5) }
        return ratings.isEmpty ? 0.0 : ratings.reduce(0.0, +) / Double(ratings.count)
    }
}

// MARK: - HRDomainContext

/// Static domain-context constants for the HR vertical.
public enum HRDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: HR] You are a human resources expert. Help with job description drafting, interview frameworks, performance review templates, disciplinary procedures, leave management, and people analytics. Apply South African labour law principles. Compliance: Labour Relations Act 66/1995, BCEA, EEA, Skills Development Act, POPIA."
    public static let complianceFlags: [String] = ["LRA_66_1995", "BCEA", "EEA", "Skills_Development_Act", "POPIA"]
    public static let suggestedTools: [String] = ["hris", "document_editor", "analytics", "job_boards"]
}
