// PersonalMentalCompanionAdapter.swift
//
// Port of CircleAI.Personal.Mental.PersonalMentalCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the personalmental domain
/// system prompt to every conversational call.
public final class PersonalMentalCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(PersonalMentalDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - PersonalMental helpers

    /// C# `CheckInAsync`.
    public func checkIn(mood: String) async throws -> String {
        try await inner.agent(
            "I am feeling: \(mood). Respond with empathy, validate my feeling, then gently offer one "
            + "evidence-based coping tool relevant to my current state.")
    }

    /// C# `GuideMindfulnessAsync`.
    public func guideMindfulness(duration: String) async throws -> String {
        try await inner.agent(
            "Guide me through a \(duration) mindfulness or breathing exercise. Use a calm, grounding "
            + "tone.")
    }

    /// C# `ReframeThoughtAsync`.
    public func reframeThought(distortedThought: String, context: String) async throws -> String {
        try await inner.agent(
            "Help reframe this thought: \(distortedThought). Context: \(context). Name the "
            + "distortion (CBT lens), offer a balanced alternative.")
    }

    /// C# `DesignCheckInRitualAsync`.
    public func designCheckInRitual(lifeStage: String, availableMinutes: String) async throws -> String {
        try await inner.agent(
            "Design a \(availableMinutes)-minute daily mental check-in for someone in \(lifeStage). "
            + "Make it sustainable for low-energy days.")
    }

    /// C# `PrepareTherapySessionAsync`.
    public func prepareTherapySession(sessionThemes: String, lastWeekEvents: String) async throws -> String {
        try await inner.agent(
            "Prepare for a therapy session on themes: \(sessionThemes). Recent events: "
            + "\(lastWeekEvents). List 3 top topics + one experiment to try.")
    }

    /// C# `GroundDuringPanicAsync`.
    public func groundDuringPanic(trigger: String, environment: String) async throws -> String {
        try await inner.agent(
            "Guide a grounding script for panic triggered by: \(trigger) in environment: "
            + "\(environment). 5-4-3-2-1 sensory anchor + breath.")
    }
}
