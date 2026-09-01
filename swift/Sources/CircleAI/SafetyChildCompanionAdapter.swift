// SafetyChildCompanionAdapter.swift
//
// Port of CircleAI.Safety.Child.SafetyChildCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the safetychild domain
/// system prompt to every conversational call.
public final class SafetyChildCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(SafetyChildDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - SafetyChild helpers

    /// C# `SetDigitalRulesAsync`.
    public func setDigitalRules(childAge: String) async throws -> String {
        try await inner.agent(
            "Create age-appropriate digital safety rules for a \(childAge)-year-old. Include screen "
            + "time limits, app/platform permissions, online communication rules, and how to report "
            + "concerning content.")
    }

    /// C# `EducateOnlineRisksAsync`.
    public func educateOnlineRisks(childAge: String) async throws -> String {
        try await inner.agent(
            "Explain online safety concepts appropriate for a \(childAge)-year-old. Cover: stranger "
            + "danger online, personal information sharing, cyberbullying, and who to tell if "
            + "something feels wrong. Use simple, non-scary language.")
    }

    /// C# `DesignSafetyConversationAsync`.
    public func designSafetyConversation(childAge: String, topic: String) async throws -> String {
        try await inner.agent(
            "Design an age-appropriate safety conversation for \(childAge) on: \(topic). Concrete "
            + "examples, scripts they can use, role-play prompt.")
    }

    /// C# `AssessOnlineRiskAsync`.
    public func assessOnlineRisk(platform: String, childAge: String, behaviour: String) async throws -> String {
        try await inner.agent(
            "Assess online risk on \(platform) for \(childAge)-year-old showing \(behaviour). "
            + "Specific risks + parent-action checklist.")
    }

    /// C# `VerifyTrustedAdultsAsync`.
    public func verifyTrustedAdults(contactList: String) async throws -> String {
        try await inner.agent(
            "Help vet trusted-adult ring from: \(contactList). Criteria to apply, questions to ask "
            + "the child.")
    }

    /// C# `DraftSchoolNotificationAsync`.
    public func draftSchoolNotification(concern: String, evidence: String) async throws -> String {
        try await inner.agent(
            "Draft a school notification about: \(concern). Evidence: \(evidence). Calm, factual, "
            + "requesting specific action.")
    }
}
