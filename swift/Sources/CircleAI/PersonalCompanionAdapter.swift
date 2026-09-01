// PersonalCompanionAdapter.swift
//
// Port of CircleAI.Personal.PersonalCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the personal domain
/// system prompt to every conversational call.
public final class PersonalCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(PersonalDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Personal helpers

    /// C# `SetGoalAsync`.
    public func setGoal(goal: String) async throws -> String {
        try await inner.agent(
            "Help me set a SMART goal for: \(goal). Break it into weekly milestones and suggest how "
            + "to track progress.")
    }

    /// C# `MakeDecisionAsync`.
    public func makeDecision(decision: String, options: String) async throws -> String {
        try await inner.agent(
            "Help me decide: \(decision). Options: \(options). Use a pros/cons framework, identify "
            + "the most important criteria, and give a clear recommendation.")
    }

    /// C# `SetWeeklyIntentionsAsync`.
    public func setWeeklyIntentions(longTermGoals: String, thisWeekContext: String) async throws -> String {
        try await inner.agent(
            "Set 3 weekly intentions aligned to: \(longTermGoals). Context this week: "
            + "\(thisWeekContext). Each: outcome + one daily anchor.")
    }

    /// C# `DraftDifficultMessageAsync`.
    public func draftDifficultMessage(recipient: String, topic: String, outcomeWanted: String) async throws -> String {
        try await inner.agent(
            "Draft a difficult message to \(recipient) about: \(topic). Outcome: \(outcomeWanted). "
            + "NVC-style: observation, feeling, need, request.")
    }

    /// C# `DesignRoutineHabitAsync`.
    public func designRoutineHabit(habit: String, currentLifestyle: String) async throws -> String {
        try await inner.agent(
            "Design a sustainable routine for habit: \(habit). Current lifestyle: "
            + "\(currentLifestyle). Cue, action, reward, slip recovery.")
    }

    /// C# `ReviewWeekAsync`.
    public func reviewWeek(accomplishments: String, challenges: String) async throws -> String {
        try await inner.agent(
            "Lead a week review. Accomplishments: \(accomplishments). Challenges: \(challenges). "
            + "Surface insight + one experiment for next week.")
    }
}
