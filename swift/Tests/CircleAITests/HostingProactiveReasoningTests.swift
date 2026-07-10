// HostingProactiveReasoningTests.swift
//
// Verifies IdleTrigger / ScheduleTrigger firing semantics and the
// ProactiveReasoningService first-trigger-wins check + message generation.

import XCTest
@testable import CircleAI

final class HostingProactiveReasoningTests: XCTestCase {

    private func ctx(idle: TimeInterval, goals: [Goal] = []) -> ProactiveContext {
        ProactiveContext(userId: "u", nowUtc: Date(), timeSinceLastInteraction: idle,
                         affectState: nil, activeGoals: goals)
    }

    // ── IdleTrigger ─────────────────────────────────────────────────────────

    func testIdleTriggerFiresPastThreshold() async throws {
        let t = IdleTrigger(idleThreshold: 3600)
        let metPast = try await t.isMet(ctx(idle: 3601))
        XCTAssertTrue(metPast)
        let metBefore = try await t.isMet(ctx(idle: 3599))
        XCTAssertFalse(metBefore)
        XCTAssertEqual(t.name, "idle")
    }

    // ── ScheduleTrigger ─────────────────────────────────────────────────────

    func testScheduleTriggerFiresInsideWindowOncePerDay() async throws {
        // Build a trigger for the current local time so we're inside its 5-min window.
        var cal = Calendar(identifier: .gregorian); cal.timeZone = .current
        let now = Date()
        let comps = cal.dateComponents([.hour, .minute], from: now)
        let trigger = ScheduleTrigger(triggerTime: TimeOfDay(hour: comps.hour!, minute: comps.minute!))
        let c = ProactiveContext(userId: "u", nowUtc: now, timeSinceLastInteraction: 0, affectState: nil, activeGoals: [])
        let firstFire = try await trigger.isMet(c)
        XCTAssertTrue(firstFire, "should fire inside its window")
        // Second call same day → should NOT fire again.
        let secondFire = try await trigger.isMet(c)
        XCTAssertFalse(secondFire, "fires at most once per calendar day")
    }

    func testScheduleTriggerSilentOutsideWindow() async throws {
        var cal = Calendar(identifier: .gregorian); cal.timeZone = .current
        let now = Date()
        let comps = cal.dateComponents([.hour, .minute], from: now)
        // Trigger 30 minutes from now → we're well outside its 5-min window.
        let future = TimeOfDay(hour: comps.hour!, minute: comps.minute!).adding(minutes: 30)
        let trigger = ScheduleTrigger(triggerTime: future)
        let c = ProactiveContext(userId: "u", nowUtc: now, timeSinceLastInteraction: 0, affectState: nil, activeGoals: [])
        let fired = try await trigger.isMet(c)
        XCTAssertFalse(fired)
    }

    // ── ProactiveReasoningService ───────────────────────────────────────────

    func testCheckFiresFirstMatchingTriggerAndRaisesEvent() async throws {
        let butler = FakeButler(reply: "check-in!")
        let alwaysA = AlwaysTrigger(name: "A")
        let alwaysB = AlwaysTrigger(name: "B")
        let svc = ProactiveReasoningService(butler: butler, goalStore: nil, affectStore: nil,
                                            triggers: [alwaysA, alwaysB])
        let box = MessageBox()
        svc.onProactiveMessageReady { args in box.record(args) }

        try await svc.check(userId: "u")
        let msgs = box.snapshot()
        XCTAssertEqual(msgs.count, 1)
        XCTAssertEqual(msgs[0].message, "check-in!")
        XCTAssertEqual(msgs[0].triggerName, "A", "only the first matching trigger fires")
        XCTAssertEqual(butler.asked.count, 1)
    }

    func testCheckWithNoTriggersDoesNothing() async throws {
        let butler = FakeButler(reply: "x")
        let svc = ProactiveReasoningService(butler: butler, goalStore: nil, affectStore: nil, triggers: [])
        try await svc.check(userId: "u")
        XCTAssertTrue(butler.asked.isEmpty)
    }

    func testCheckSkipsNonMatchingTriggers() async throws {
        let butler = FakeButler(reply: "x")
        let never = NeverTrigger()
        let svc = ProactiveReasoningService(butler: butler, goalStore: nil, affectStore: nil, triggers: [never])
        let box = MessageBox()
        svc.onProactiveMessageReady { args in box.record(args) }
        try await svc.check(userId: "u")
        XCTAssertTrue(box.snapshot().isEmpty)
        XCTAssertTrue(butler.asked.isEmpty)
    }

    func testBuildProactivePromptMentionsGoalsAndAway() {
        let g = Goal(id: "1", userId: "u", title: "Learn Swift", description: "", status: .active,
                     priority: .normal, createdAt: Date(), progress: 0)
        let prompt = ProactiveReasoningService.buildProactivePrompt(
            userId: "u", timeSinceLastInteraction: 2 * 3600, activeGoals: [g])
        XCTAssertTrue(prompt.contains("approximately 2 hours"))
        XCTAssertTrue(prompt.contains("Learn Swift"))
        XCTAssertTrue(prompt.contains("check-in"))
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    final class AlwaysTrigger: ITriggerCondition, @unchecked Sendable {
        let name: String
        init(name: String) { self.name = name }
        func isMet(_ context: ProactiveContext) async throws -> Bool { true }
    }
    final class NeverTrigger: ITriggerCondition, @unchecked Sendable {
        var name: String { "never" }
        func isMet(_ context: ProactiveContext) async throws -> Bool { false }
    }
    final class MessageBox: @unchecked Sendable {
        private let lock = NSLock()
        private var msgs: [ProactiveMessageEventArgs] = []
        func record(_ m: ProactiveMessageEventArgs) { lock.lock(); msgs.append(m); lock.unlock() }
        func snapshot() -> [ProactiveMessageEventArgs] { lock.lock(); defer { lock.unlock() }; return msgs }
    }
}
