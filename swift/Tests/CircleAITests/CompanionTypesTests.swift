// CompanionTypesTests.swift
// Verifies InterfaceKind has exactly 7 cases and validates struct construction
// for CompanionContext, CompanionTurn, and CompanionProactiveEvent.

import XCTest
import Foundation
@testable import CircleAI

final class CompanionTypesTests: XCTestCase {

    // ── InterfaceKind ─────────────────────────────────────────────────────────

    func testInterfaceKindHas7Cases() {
        XCTAssertEqual(InterfaceKind.allCases.count, 7)
    }

    func testInterfaceKindCases() {
        let cases = Set(InterfaceKind.allCases)
        XCTAssertTrue(cases.contains(.mobile))
        XCTAssertTrue(cases.contains(.wearable))
        XCTAssertTrue(cases.contains(.desktop))
        XCTAssertTrue(cases.contains(.web))
        XCTAssertTrue(cases.contains(.iot))
        XCTAssertTrue(cases.contains(.ambient))
        XCTAssertTrue(cases.contains(.headless))
    }

    func testInterfaceKindRawValues() {
        XCTAssertEqual(InterfaceKind.mobile.rawValue,   "mobile")
        XCTAssertEqual(InterfaceKind.wearable.rawValue, "wearable")
        XCTAssertEqual(InterfaceKind.desktop.rawValue,  "desktop")
        XCTAssertEqual(InterfaceKind.web.rawValue,      "web")
        XCTAssertEqual(InterfaceKind.iot.rawValue,      "iot")
        XCTAssertEqual(InterfaceKind.ambient.rawValue,  "ambient")
        XCTAssertEqual(InterfaceKind.headless.rawValue, "headless")
    }

    // ── CompanionContext ───────────────────────────────────────────────────────

    func testCompanionContextInit() {
        let now = Date()
        let ctx = CompanionContext(
            identityId: "id-1",
            displayName: "Sipho",
            preferredLanguage: "zu",
            interface: .mobile,
            personaHints: "[User preferences]\nKeep responses brief.\n",
            affectSummary: "[Affect state]\nYou are fully engaged — be enthusiastic and thorough.\n",
            recentMemorySnippets: ["User asked about budgeting.", "User asked about Zulu culture."],
            activeGoals: ["Save R5000 by June"],
            contextBuiltAt: now
        )
        XCTAssertEqual(ctx.identityId,               "id-1")
        XCTAssertEqual(ctx.displayName,              "Sipho")
        XCTAssertEqual(ctx.preferredLanguage,        "zu")
        XCTAssertEqual(ctx.interface,                .mobile)
        XCTAssertEqual(ctx.recentMemorySnippets.count, 2)
        XCTAssertEqual(ctx.activeGoals.count,          1)
        XCTAssertEqual(ctx.contextBuiltAt,           now)
    }

    func testCompanionContextNilLanguage() {
        let ctx = CompanionContext(
            identityId: "id-anon",
            displayName: "Guest",
            preferredLanguage: nil,
            interface: .headless,
            personaHints: "",
            affectSummary: "",
            recentMemorySnippets: [],
            activeGoals: []
        )
        XCTAssertNil(ctx.preferredLanguage)
        XCTAssertEqual(ctx.interface, .headless)
    }

    // ── CompanionTurn ─────────────────────────────────────────────────────────

    func testCompanionTurnUserRole() {
        let t = CompanionTurn(role: "user", content: "Hello B!")
        XCTAssertEqual(t.role,    "user")
        XCTAssertEqual(t.content, "Hello B!")
    }

    func testCompanionTurnAssistantRole() {
        let now = Date()
        let t = CompanionTurn(role: "assistant", content: "Hi! How can I help?", timestamp: now)
        XCTAssertEqual(t.role,      "assistant")
        XCTAssertEqual(t.timestamp, now)
    }

    func testCompanionTurnDefaultTimestamp() {
        let before = Date()
        let t = CompanionTurn(role: "user", content: "Test")
        let after = Date()
        // timestamp should be within the test's time window
        XCTAssertGreaterThanOrEqual(t.timestamp, before)
        XCTAssertLessThanOrEqual(t.timestamp, after)
    }

    // ── CompanionProactiveEvent ───────────────────────────────────────────────

    func testCompanionProactiveEventInit() {
        let now = Date()
        let evt = CompanionProactiveEvent(
            sessionId:   "sess-1",
            identityId:  "id-1",
            interface:   .mobile,
            message:     "Don't forget your goal check-in!",
            triggerName: "goal_checkin",
            generatedAt: now
        )
        XCTAssertEqual(evt.sessionId,   "sess-1")
        XCTAssertEqual(evt.identityId,  "id-1")
        XCTAssertEqual(evt.interface,   .mobile)
        XCTAssertEqual(evt.message,     "Don't forget your goal check-in!")
        XCTAssertEqual(evt.triggerName, "goal_checkin")
        XCTAssertEqual(evt.generatedAt, now)
    }

    func testCompanionProactiveEventAllInterfaces() {
        // Verify we can construct proactive events for all 7 surfaces
        for kind in InterfaceKind.allCases {
            let evt = CompanionProactiveEvent(
                sessionId: "s",
                identityId: "i",
                interface: kind,
                message: "hello",
                triggerName: "test"
            )
            XCTAssertEqual(evt.interface, kind)
        }
    }

    // ── FeedbackPolarity (Memory module) ─────────────────────────────────────

    func testFeedbackPolarityRawValues() {
        XCTAssertEqual(FeedbackPolarity.positive.rawValue,   1)
        XCTAssertEqual(FeedbackPolarity.negative.rawValue,  -1)
        XCTAssertEqual(FeedbackPolarity.correction.rawValue, 0)
    }

    // ── GoalStatus & GoalPriority ─────────────────────────────────────────────

    func testGoalStatusCases() {
        XCTAssertEqual(GoalStatus.active.rawValue,    "active")
        XCTAssertEqual(GoalStatus.completed.rawValue, "completed")
        XCTAssertEqual(GoalStatus.abandoned.rawValue, "abandoned")
    }

    func testGoalPriorityCases() {
        XCTAssertEqual(GoalPriority.low.rawValue,    "low")
        XCTAssertEqual(GoalPriority.normal.rawValue, "normal")
        XCTAssertEqual(GoalPriority.high.rawValue,   "high")
    }

    func testGoalConstruction() {
        let now = Date()
        let g = Goal(
            id: "g-1",
            userId: "u-1",
            title: "Save R5000",
            description: "Save five thousand rand by end of June",
            status: .active,
            priority: .high,
            createdAt: now
        )
        XCTAssertEqual(g.id,       "g-1")
        XCTAssertEqual(g.userId,   "u-1")
        XCTAssertEqual(g.status,   .active)
        XCTAssertEqual(g.priority, .high)
        XCTAssertNil(g.dueAt)
        XCTAssertNil(g.completedAt)
        XCTAssertNil(g.notes)
    }

    // ── PersonaState.toSystemPromptHint ──────────────────────────────────────

    func testPersonaHintDefault() {
        let p = PersonaState()
        XCTAssertEqual(p.toSystemPromptHint(), "")
    }

    func testPersonaHintBriefCasual() {
        let p = PersonaState()
        p.verbosity = "brief"
        p.formality = "casual"
        let expected = "[User preferences]\nKeep responses brief.\nUse a casual, friendly tone.\n"
        XCTAssertEqual(p.toSystemPromptHint(), expected)
    }

    func testPersonaHintDetailedFormal() {
        let p = PersonaState()
        p.verbosity = "detailed"
        p.formality = "formal"
        let expected = "[User preferences]\nKeep responses detailed.\nMaintain a formal, professional tone.\n"
        XCTAssertEqual(p.toSystemPromptHint(), expected)
    }

    func testPersonaHintBriefNeutralWithLocale() {
        let p = PersonaState()
        p.verbosity = "brief"
        p.formality = "neutral"
        p.preferredLocale = "zu"
        let expected = "[User preferences]\nKeep responses brief.\nRespond in the language appropriate for locale zu.\n"
        XCTAssertEqual(p.toSystemPromptHint(), expected)
    }

    func testPersonaHintBalancedNeutralWithLocale() {
        let p = PersonaState()
        p.verbosity = "balanced"
        p.formality = "neutral"
        p.preferredLocale = "af"
        let expected = "[User preferences]\nRespond in the language appropriate for locale af.\n"
        XCTAssertEqual(p.toSystemPromptHint(), expected)
    }

    func testPersonaHintDetailedCasualWithLocale() {
        let p = PersonaState()
        p.verbosity = "detailed"
        p.formality = "casual"
        p.preferredLocale = "sw"
        let expected = "[User preferences]\nKeep responses detailed.\nUse a casual, friendly tone.\nRespond in the language appropriate for locale sw.\n"
        XCTAssertEqual(p.toSystemPromptHint(), expected)
    }

    // ── PersonaState satisfactionScore ───────────────────────────────────────

    func testSatisfactionScoreNilWhenInsufficient() {
        let p = PersonaState()
        p.positiveSignals = 3
        p.negativeSignals = 2
        XCTAssertNil(p.satisfactionScore)
    }

    func testSatisfactionScoreComputed() {
        let p = PersonaState()
        p.positiveSignals = 8
        p.negativeSignals = 2
        XCTAssertEqual(p.satisfactionScore!, 0.8, accuracy: 1e-9)
    }

    func testSatisfactionScoreExactly10Signals() {
        let p = PersonaState()
        p.positiveSignals = 6
        p.negativeSignals = 4
        XCTAssertNotNil(p.satisfactionScore)
        XCTAssertEqual(p.satisfactionScore!, 0.6, accuracy: 1e-9)
    }

    // ── SyncDeliveryMode ──────────────────────────────────────────────────────

    func testSyncDeliveryModeCases() {
        let cases = SyncDeliveryMode.allCases
        XCTAssertEqual(cases.count, 4)
        XCTAssertTrue(cases.contains(.realtime))
        XCTAssertTrue(cases.contains(.reliable))
        XCTAssertTrue(cases.contains(.dtn))
        XCTAssertTrue(cases.contains(.localStore))
    }

    func testSyncDomainKeys() {
        XCTAssertEqual(SyncDomainKeys.memoryEpisodic, "memory.episodic")
        XCTAssertEqual(SyncDomainKeys.affectState,    "affect.state")
        XCTAssertEqual(SyncDomainKeys.persona,        "persona")
        XCTAssertEqual(SyncDomainKeys.goals,          "goals")
        XCTAssertEqual(SyncDomainKeys.identity,       "identity")
    }

    // ── SyncDelta construction ────────────────────────────────────────────────

    func testSyncDeltaInit() {
        let payload = Data([0x01, 0x02, 0x03])
        let now = Date()
        let delta = SyncDelta(
            ownerId: "owner-1",
            sourceDeviceId: "dev-1",
            targetDeviceId: "",
            domainKey: SyncDomainKeys.affectState,
            payload: payload,
            sequence: 42,
            deliveryMode: .reliable,
            ttl: 3600,
            createdAt: now
        )
        XCTAssertEqual(delta.ownerId,        "owner-1")
        XCTAssertEqual(delta.targetDeviceId, "")    // broadcast
        XCTAssertEqual(delta.sequence,       42)
        XCTAssertEqual(delta.deliveryMode,   .reliable)
        XCTAssertEqual(delta.ttl,            3600)
        XCTAssertEqual(delta.payload,        payload)
        XCTAssertEqual(delta.createdAt,      now)
    }

    // ── GenerationOptions defaults ────────────────────────────────────────────

    func testGenerationOptionsDefaults() {
        let opts = GenerationOptions()
        XCTAssertEqual(opts.maxTokens,   512)
        XCTAssertEqual(opts.temperature, 0.7, accuracy: 1e-6)
        XCTAssertEqual(opts.topP,        0.9, accuracy: 1e-6)
        XCTAssertEqual(opts.topK,        40)
        XCTAssertNil(opts.seed)
        XCTAssertNil(opts.stopSequences)
    }

    func testGenerationOptionsCustom() {
        let opts = GenerationOptions(
            maxTokens: 1024,
            temperature: 0.2,
            topP: 0.95,
            topK: 20,
            seed: 42,
            stopSequences: ["</s>", "[INST]"]
        )
        XCTAssertEqual(opts.maxTokens,        1024)
        XCTAssertEqual(opts.seed,             42)
        XCTAssertEqual(opts.stopSequences?.count, 2)
    }

    // ── ToolDefinition & ToolParameter ───────────────────────────────────────

    func testToolDefinitionInit() {
        let param = ToolParameter(type: "string", description: "The city name")
        let tool = ToolDefinition(
            name: "get_weather",
            description: "Returns current weather for a city",
            parameters: ["city": param],
            requiredParameters: ["city"]
        )
        XCTAssertEqual(tool.name,                  "get_weather")
        XCTAssertEqual(tool.parameters.count,      1)
        XCTAssertEqual(tool.requiredParameters[0], "city")
        XCTAssertNil(param.enumValues)
    }

    func testToolParameterWithEnum() {
        let p = ToolParameter(type: "string", description: "Unit", enumValues: ["celsius", "fahrenheit"])
        XCTAssertEqual(p.enumValues?.count, 2)
    }

    // ── ToolResult factories ──────────────────────────────────────────────────

    func testToolResultOk() {
        let r = ToolResult.ok(toolName: "get_weather", result: "25°C")
        XCTAssertTrue(r.success)
        XCTAssertNil(r.error)
        XCTAssertEqual(r.toolName, "get_weather")
    }

    func testToolResultFailure() {
        let r = ToolResult.failure(toolName: "get_weather", error: "City not found")
        XCTAssertFalse(r.success)
        XCTAssertEqual(r.error, "City not found")
    }
}
