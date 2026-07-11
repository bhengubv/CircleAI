// ConstructionBoardTests.swift
//
// Exercises the Construction records' Codable round-trips and the deterministic
// behaviour of InMemoryConstructionBoard — projects, tasks (open, due-asc,
// complete + unknown throw), costs, spend, and remaining budget. Also checks
// the ConstructionDomainContext constants. Mirrors CircleAI.Construction/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class ConstructionBoardTests: XCTestCase {

    func testCostEntryCodableRoundTrip() throws {
        let c = CostEntry(entryId: "e1", projectId: "p1", category: "labour", amount: Decimal(string: "12500.50")!, atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(CostEntry.self, from: try JSONEncoder().encode(c)), c)
    }

    func testOpenTasksDueOrderedAndCompleteThrows() throws {
        let b = InMemoryConstructionBoard()
        b.create(Project(projectId: "p1", name: "House", startOn: Date(timeIntervalSince1970: 0), endOn: nil, budget: Decimal(string: "1000000")!, currency: "ZAR"))
        b.add(ConstructionTask(constructionTaskId: "t1", projectId: "p1", description: "foundation", dueOn: Date(timeIntervalSince1970: 30), completed: false))
        b.add(ConstructionTask(constructionTaskId: "t2", projectId: "p1", description: "walls", dueOn: Date(timeIntervalSince1970: 10), completed: false))
        b.add(ConstructionTask(constructionTaskId: "t3", projectId: "p1", description: "roof", dueOn: Date(timeIntervalSince1970: 20), completed: true)) // done
        XCTAssertEqual(b.openConstructionTasksFor(projectId: "p1").map { $0.constructionTaskId }, ["t2", "t1"])
        try b.complete(taskId: "t1")
        XCTAssertEqual(b.openConstructionTasksFor(projectId: "p1").map { $0.constructionTaskId }, ["t2"])
        XCTAssertThrowsError(try b.complete(taskId: "ghost")) { XCTAssertEqual($0 as? ConstructionError, .unknownTask("ghost")) }
    }

    func testSpendAndRemainingBudget() throws {
        let b = InMemoryConstructionBoard()
        b.create(Project(projectId: "p1", name: "House", startOn: Date(timeIntervalSince1970: 0), endOn: nil, budget: Decimal(string: "100000")!, currency: "ZAR"))
        b.recordCost(CostEntry(entryId: "e1", projectId: "p1", category: "labour", amount: Decimal(string: "30000")!, atUtc: Date(timeIntervalSince1970: 1)))
        b.recordCost(CostEntry(entryId: "e2", projectId: "p1", category: "materials", amount: Decimal(string: "25000")!, atUtc: Date(timeIntervalSince1970: 2)))
        b.recordCost(CostEntry(entryId: "e3", projectId: "other", category: "misc", amount: Decimal(string: "9999")!, atUtc: Date(timeIntervalSince1970: 3)))
        XCTAssertEqual(b.spendFor(projectId: "p1"), Decimal(string: "55000")!)
        XCTAssertEqual(try b.remainingBudget(projectId: "p1"), Decimal(string: "45000")!)
        XCTAssertThrowsError(try b.remainingBudget(projectId: "ghost")) { XCTAssertEqual($0 as? ConstructionError, .unknownProject("ghost")) }
    }

    func testDomainContext() {
        XCTAssertTrue(ConstructionDomainContext.systemPromptSnippet.contains("[DOMAIN: Construction]"))
        XCTAssertEqual(ConstructionDomainContext.complianceFlags, ["OHS_Act", "NHBRC_Act", "CIDB_Act", "National_Building_Regs", "POPIA"])
        XCTAssertEqual(ConstructionDomainContext.suggestedTools, ["project_scheduler", "document_editor", "map", "analytics"])
    }
}
