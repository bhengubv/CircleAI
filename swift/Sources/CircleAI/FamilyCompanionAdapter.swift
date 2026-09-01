// FamilyCompanionAdapter.swift
//
// Port of CircleAI.Family.FamilyCompanionAdapter.
//
// AN ADAPTER IS A DECORATOR, NOT A SESSION. Identity, history, context and
// feedback are forwarded to the inner session untouched; the only thing this
// type does is put the domain's system prompt in front of every
// conversational call, and offer the helpers that domain needs.
//
// The helper PROMPTS are generated from the C# so that not one clause of
// forty-six sets of them is lost in transcription; the forwarding above them
// is written once and reviewed once. See swift/tools/gen_adapters.py.
//
// One deliberate difference: C# currency format specifiers ({x:C}) are
// dropped, because they render against the machine's CURRENT CULTURE. A
// prompt that says R1 200,00 on one device and $1,200.00 on another is not
// the same prompt, and the model is being handed a number either way.

import Foundation

/// An `ICompanionSession` decorator that prepends the family domain
/// system prompt to every conversational call.
public final class FamilyCompanionAdapter: ICompanionSession, @unchecked Sendable {

    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) { self.inner = inner }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }

    public func send(_ message: String) async throws -> String {
        try await inner.send(enrich(message))
    }
    public func stream(_ message: String) -> AsyncStream<String> {
        inner.stream(enrich(message))
    }
    public func agent(_ instruction: String) async throws -> String {
        try await inner.agent(enrich(instruction))
    }

    private func enrich(_ m: String) -> String {
        "\(FamilyDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Family helpers

    /// C# `PlanFamilyActivityAsync`.
    public func planFamilyActivity(ages: String, budget: String, interests: String) async throws -> String {
        try await inner.agent(
            "Plan a family activity for children aged \(ages). Budget: \(budget). Interests: "
            + "\(interests). Include indoor and outdoor options with estimated cost and "
            + "age-appropriateness.")
    }

    /// C# `CreateFamilyBudgetAsync`.
    public func createFamilyBudget(income: String, expenses: String, goals: String) async throws -> String {
        try await inner.agent(
            "Create a family budget. Combined income: \(income). Expenses: \(expenses). Goals: "
            + "\(goals). Allocate to categories and identify savings opportunities.")
    }

    /// C# `PlanFamilyMealsAsync`.
    public func planFamilyMeals(familySize: String, dietaryNotes: String, daysCount: Int) async throws -> String {
        try await inner.agent(
            "Plan \(daysCount) days of family meals for \(familySize) people, dietary notes: "
            + "\(dietaryNotes). Include shopping list grouped by aisle.")
    }

    /// C# `MediateSiblingDisputeAsync`.
    public func mediateSiblingDispute(ages: String, dispute: String) async throws -> String {
        try await inner.agent(
            "Mediate a sibling dispute between ages \(ages): \(dispute). Step-by-step script "
            + "honouring each child's perspective.")
    }

    /// C# `DesignHouseholdChoreRotaAsync`.
    public func designHouseholdChoreRota(members: String, chores: String) async throws -> String {
        try await inner.agent(
            "Design a fair, age-appropriate chore rota. Members: \(members). Chores: \(chores). "
            + "Cover frequency and ownership.")
    }

    /// C# `CelebrateMilestoneAsync`.
    public func celebrateMilestone(milestone: String, memberName: String, budget: String) async throws -> String {
        try await inner.agent(
            "Plan a \(budget) milestone celebration for \(memberName): \(milestone). Ideas across "
            + "activity / food / memento / message.")
    }
}
