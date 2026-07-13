// AutonomousBizBoardTests.swift
//
// Exercises the AutonomousBiz port: the revenue loop (publish + history read
// with `since`, subscriber fan-out + dispose), the treasury running balance
// (currency-filtered, case-insensitive), the append-only decision log
// (newest-first with limit), and the null backends. Mirrors CircleAI.AutonomousBiz/*.

import XCTest
import Foundation
@testable import CircleAI

final class AutonomousBizBoardTests: XCTestCase {

    private func rev(_ id: String, _ amount: Decimal, _ ccy: String = "ZAR",
                     _ at: TimeInterval) -> RevenueEvent {
        RevenueEvent(eventId: id, amount: amount, currency: ccy, source: "s",
                     atUtc: Date(timeIntervalSince1970: at))
    }

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testRevenueEventCodableRoundTrip() throws {
        let e = rev("e1", 19, "ZAR", 5)
        XCTAssertEqual(try JSONDecoder().decode(RevenueEvent.self, from: try JSONEncoder().encode(e)), e)
    }

    // ── Revenue loop ────────────────────────────────────────────────────────────

    func testRevenueLoopHistoryHonoursSince() async {
        let loop = InMemoryRevenueLoop()
        XCTAssertEqual(loop.backendId, "in-memory")
        loop.publish(rev("e1", 10, "ZAR", 1))
        loop.publish(rev("e2", 20, "ZAR", 5))
        let all = await loop.read(since: Date(timeIntervalSince1970: 0))
        XCTAssertEqual(all.count, 2)
        let recent = await loop.read(since: Date(timeIntervalSince1970: 3))
        XCTAssertEqual(recent.map { $0.eventId }, ["e2"])
    }

    func testRevenueLoopFanOutAndDispose() async {
        let loop = InMemoryRevenueLoop()
        let collector = AmountCollector()
        let sub = loop.subscribe { e in await collector.add(e.eventId) }
        XCTAssertEqual(loop.subscriberCount, 1)
        loop.publish(rev("e1", 5, "ZAR", 1))
        // Handlers run on detached tasks — poll until delivered.
        await waitUntil { await collector.count == 1 }
        sub.dispose()
        XCTAssertEqual(loop.subscriberCount, 0)
        loop.publish(rev("e2", 5, "ZAR", 2))
        // No new delivery after dispose.
        try? await Task.sleep(nanoseconds: 20_000_000)
        let ids = await collector.ids
        XCTAssertEqual(ids, ["e1"])
    }

    // ── Treasury ──────────────────────────────────────────────────────────────────

    func testTreasurySumsMatchingCurrency() async {
        let loop = InMemoryRevenueLoop()
        loop.publish(rev("e1", 10, "ZAR", 1))
        loop.publish(rev("e2", 5, "zar", 2))   // case-insensitive match
        loop.publish(rev("e3", 100, "USD", 3)) // different currency — excluded
        let treasury = InMemoryTreasury(loop: loop, currency: "ZAR")
        let snap = await treasury.getSnapshot()
        XCTAssertEqual(snap.balance, 15)
        XCTAssertEqual(snap.currency, "ZAR")
    }

    func testTreasuryDefaultsToZar() async {
        let loop = InMemoryRevenueLoop()
        loop.publish(rev("e1", 42, "ZAR", 1))
        let snap = await InMemoryTreasury(loop: loop).getSnapshot()
        XCTAssertEqual(snap.balance, 42)
    }

    // ── Decision log ────────────────────────────────────────────────────────────

    func testDecisionLogNewestFirstWithLimit() async {
        let log = InMemoryDecisionLog()
        for i in 0..<4 {
            await log.append(AutonomousDecision(decisionId: "d\(i)", rationale: "r", chosenAction: "a",
                                                atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        let recent = await log.read(limit: 2)
        XCTAssertEqual(recent.map { $0.decisionId }, ["d3", "d2"])
        let all = await log.read()
        XCTAssertEqual(all.count, 4)  // default limit
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullBackends() async {
        let snap = await NullTreasury.instance.getSnapshot()
        XCTAssertEqual(snap.balance, 0)
        let sub = NullRevenueLoop.instance.subscribe { _ in }
        sub.dispose()
        let revEmpty = await NullRevenueLoop.instance.read(since: Date(timeIntervalSince1970: 0))
        XCTAssertTrue(revEmpty.isEmpty)
        await NullDecisionLog.instance.append(AutonomousDecision(decisionId: "d", rationale: "r",
                                                                 chosenAction: "a", atUtc: Date()))
        let logEmpty = await NullDecisionLog.instance.read()
        XCTAssertTrue(logEmpty.isEmpty)
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private func waitUntil(_ cond: @escaping () async -> Bool, timeoutMs: Int = 1000) async {
        let deadline = Date().addingTimeInterval(Double(timeoutMs) / 1000)
        while Date() < deadline {
            if await cond() { return }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
    }

    private actor AmountCollector {
        var ids: [String] = []
        func add(_ id: String) { ids.append(id) }
        var count: Int { ids.count }
    }
}
