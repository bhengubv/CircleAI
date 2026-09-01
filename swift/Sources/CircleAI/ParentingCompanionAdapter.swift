// ParentingCompanionAdapter.swift
//
// Port of CircleAI.Parenting.ParentingCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the parenting domain
/// system prompt to every conversational call.
public final class ParentingCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(ParentingDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Parenting helpers

    /// C# `AdviseOnBehaviourAsync`.
    public func adviseOnBehaviour(childAge: String, behaviour: String, context: String) async throws -> String {
        try await inner.agent(
            "Advise on managing this behaviour in a \(childAge)-year-old: \(behaviour). Context: "
            + "\(context). Use positive discipline principles and suggest age-appropriate strategies.")
    }

    /// C# `DraftSchoolEmailAsync`.
    public func draftSchoolEmail(purpose: String, teacherName: String) async throws -> String {
        try await inner.agent(
            "Draft a professional, respectful email to teacher \(teacherName) regarding: \(purpose). "
            + "Balance parental advocacy with collaborative tone.")
    }

    /// C# `RespondToBehaviourAsync`.
    public func respondToBehaviour(childAge: String, behaviour: String, context: String) async throws -> String {
        try await inner.agent(
            "Respond to \(childAge)-year-old \(behaviour) in context: \(context). Provide a calm "
            + "script + the developmental rationale.")
    }

    /// C# `DesignRoutineAsync`.
    public func designRoutine(childAge: String, targetWindow: String) async throws -> String {
        try await inner.agent(
            "Design a \(targetWindow) routine for a \(childAge)-year-old. Cover transitions, sensory "
            + "needs, choice points.")
    }

    /// C# `MilestoneCheckInAsync`.
    public func milestoneCheckIn(childAge: String, observations: String) async throws -> String {
        try await inner.agent(
            "Sanity-check milestones for \(childAge): \(observations). Flag what's normal-range vs "
            + "worth-discussing-with-pediatrician.")
    }

    /// C# `PrepareSchoolConferenceAsync`.
    public func prepareSchoolConference(childName: String, grade: String, concerns: String) async throws -> String {
        try await inner.agent(
            "Prepare \(childName)'s parent-teacher conference (\(grade)). Concerns: \(concerns). "
            + "Draft questions + advocacy points.")
    }
}
