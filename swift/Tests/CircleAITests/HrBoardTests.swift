// HrBoardTests.swift
//
// Exercises the HR records' Codable round-trips and the deterministic behaviour
// of InMemoryHRBoard — hiring + name-ordered listing, leave request/decision
// (incl. unknown-request throw), pending-leave filtering (case-insensitive
// "Pending"), and average performance rating (0.0 when none). Also checks the
// HRDomainContext constants. Mirrors CircleAI.HR/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class HrBoardTests: XCTestCase {

    private func emp(_ id: String, _ name: String) -> Employee {
        Employee(employeeId: id, name: name, role: "Dev", hiredOn: Date(timeIntervalSince1970: 0), salary: 100, currency: "ZAR")
    }

    func testEmployeeCodableRoundTrip() throws {
        let e = emp("e1", "Ada")
        XCTAssertEqual(try JSONDecoder().decode(Employee.self, from: try JSONEncoder().encode(e)), e)
    }

    func testLeaveRequestCodableRoundTrip() throws {
        let r = LeaveRequest(requestId: "r1", employeeId: "e1", kind: "Annual",
                             from: Date(timeIntervalSince1970: 1), to: Date(timeIntervalSince1970: 2), status: "Pending")
        XCTAssertEqual(try JSONDecoder().decode(LeaveRequest.self, from: try JSONEncoder().encode(r)), r)
    }

    func testHireGetAndEmployeesNameOrdered() {
        let b = InMemoryHRBoard()
        b.hire(emp("e2", "Zoe"))
        b.hire(emp("e1", "Ada"))
        XCTAssertEqual(b.getEmployee("e1")?.name, "Ada")
        XCTAssertNil(b.getEmployee("nope"))
        XCTAssertEqual(b.employees.map { $0.name }, ["Ada", "Zoe"])
    }

    func testLeaveDecisionAndPendingFilter() throws {
        let b = InMemoryHRBoard()
        b.request(LeaveRequest(requestId: "r1", employeeId: "e1", kind: "Annual",
                               from: Date(), to: Date(), status: "Pending"))
        b.request(LeaveRequest(requestId: "r2", employeeId: "e2", kind: "Sick",
                               from: Date(), to: Date(), status: "pending"))
        XCTAssertEqual(Set(b.pendingLeaves().map { $0.requestId }), ["r1", "r2"])
        try b.decideLeave(requestId: "r1", decision: "Approved")
        XCTAssertEqual(b.pendingLeaves().map { $0.requestId }, ["r2"])
    }

    func testDecideUnknownLeaveThrows() {
        let b = InMemoryHRBoard()
        XCTAssertThrowsError(try b.decideLeave(requestId: "ghost", decision: "Approved")) { err in
            XCTAssertEqual(err as? HRError, .unknownLeaveRequest("ghost"))
        }
    }

    func testAvgRatingForEmptyIsZeroAndMeanOtherwise() {
        let b = InMemoryHRBoard()
        XCTAssertEqual(b.avgRatingFor("e1"), 0.0)
        b.review(PerformanceReview(reviewId: "v1", employeeId: "e1", reviewedOn: Date(), ratingOutOf5: 4, notes: "good"))
        b.review(PerformanceReview(reviewId: "v2", employeeId: "e1", reviewedOn: Date(), ratingOutOf5: 2, notes: "ok"))
        b.review(PerformanceReview(reviewId: "v3", employeeId: "e2", reviewedOn: Date(), ratingOutOf5: 5, notes: "other"))
        XCTAssertEqual(b.avgRatingFor("e1"), 3.0, accuracy: 1e-9)
    }

    func testDomainContext() {
        XCTAssertTrue(HRDomainContext.systemPromptSnippet.contains("[DOMAIN: HR]"))
        XCTAssertEqual(HRDomainContext.complianceFlags, ["LRA_66_1995", "BCEA", "EEA", "Skills_Development_Act", "POPIA"])
        XCTAssertEqual(HRDomainContext.suggestedTools, ["hris", "document_editor", "analytics", "job_boards"])
    }
}
