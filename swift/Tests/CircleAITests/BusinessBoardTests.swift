// BusinessBoardTests.swift
//
// Exercises the Business records' Codable round-trips and the deterministic
// behaviour of InMemoryBusinessBoard — unit hierarchy (childrenOf), KPI
// recording + latest-value (NaN when absent), and quarter-target achievement
// (NaN when the target is missing or zero, else latest/target). Also checks the
// BusinessDomainContext constants. Mirrors CircleAI.Business/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class BusinessBoardTests: XCTestCase {

    func testBusinessUnitCodableRoundTrip() throws {
        let u = BusinessUnit(unitId: "u1", name: "Sales", parentUnitId: "root", kpiTags: ["rev", "conv"])
        XCTAssertEqual(try JSONDecoder().decode(BusinessUnit.self, from: try JSONEncoder().encode(u)), u)
    }

    func testKpiSampleCodableRoundTrip() throws {
        let s = KpiSample(unitId: "u1", metric: "rev", value: 1000, atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(KpiSample.self, from: try JSONEncoder().encode(s)), s)
    }

    func testAddGetAndChildrenOf() {
        let b = InMemoryBusinessBoard()
        b.add(BusinessUnit(unitId: "root", name: "Co", parentUnitId: "", kpiTags: []))
        b.add(BusinessUnit(unitId: "u1", name: "Sales", parentUnitId: "root", kpiTags: []))
        b.add(BusinessUnit(unitId: "u2", name: "Eng", parentUnitId: "root", kpiTags: []))
        b.add(BusinessUnit(unitId: "u3", name: "SDR", parentUnitId: "u1", kpiTags: []))
        XCTAssertEqual(b.getUnit("u1")?.name, "Sales")
        XCTAssertEqual(Set(b.childrenOf("root").map { $0.unitId }), ["u1", "u2"])
        XCTAssertEqual(b.childrenOf("u1").map { $0.unitId }, ["u3"])
    }

    func testLatestKpiIsMostRecentOrNaN() {
        let b = InMemoryBusinessBoard()
        XCTAssertTrue(b.latestKpi(unitId: "u1", metric: "rev").isNaN)
        b.record(KpiSample(unitId: "u1", metric: "rev", value: 10, atUtc: Date(timeIntervalSince1970: 1)))
        b.record(KpiSample(unitId: "u1", metric: "rev", value: 30, atUtc: Date(timeIntervalSince1970: 3)))
        b.record(KpiSample(unitId: "u1", metric: "rev", value: 20, atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.latestKpi(unitId: "u1", metric: "rev"), 30)
        // Different metric is still NaN.
        XCTAssertTrue(b.latestKpi(unitId: "u1", metric: "conv").isNaN)
    }

    func testTargetAchievement() {
        let b = InMemoryBusinessBoard()
        // No target → NaN.
        XCTAssertTrue(b.targetAchievement(unitId: "u1", metric: "rev", year: 2026, quarter: 2).isNaN)
        b.setTarget(QuarterTarget(unitId: "u1", metric: "rev", year: 2026, quarter: 2, target: 100))
        // Target present but no KPI → latest is NaN → NaN.
        XCTAssertTrue(b.targetAchievement(unitId: "u1", metric: "rev", year: 2026, quarter: 2).isNaN)
        b.record(KpiSample(unitId: "u1", metric: "rev", value: 75, atUtc: Date(timeIntervalSince1970: 1)))
        XCTAssertEqual(b.targetAchievement(unitId: "u1", metric: "rev", year: 2026, quarter: 2), 0.75, accuracy: 1e-9)
    }

    func testTargetAchievementZeroTargetIsNaN() {
        let b = InMemoryBusinessBoard()
        b.setTarget(QuarterTarget(unitId: "u1", metric: "rev", year: 2026, quarter: 1, target: 0))
        b.record(KpiSample(unitId: "u1", metric: "rev", value: 50, atUtc: Date()))
        XCTAssertTrue(b.targetAchievement(unitId: "u1", metric: "rev", year: 2026, quarter: 1).isNaN)
    }

    func testDomainContext() {
        XCTAssertTrue(BusinessDomainContext.systemPromptSnippet.contains("[DOMAIN: Business]"))
        XCTAssertEqual(BusinessDomainContext.complianceFlags, ["POPIA", "Commercial_Law", "GDPR_aware"])
        XCTAssertEqual(BusinessDomainContext.suggestedTools, ["calendar", "web_search", "document_editor", "task_manager"])
    }
}
