// ElderlyCompanionAdapter.swift
//
// Port of CircleAI.Elderly.ElderlyCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the elderly domain
/// system prompt to every conversational call.
public final class ElderlyCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(ElderlyDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Elderly helpers

    /// C# `CreateMedScheduleAsync`.
    public func createMedSchedule(medications: String) async throws -> String {
        try await inner.agent(
            "Create a clear, simple medication schedule for these "
            + "prescriptions:\n\(medications)\nInclude time of day, food requirements, and what to do "
            + "if a dose is missed.")
    }

    /// C# `LocateSupportAsync`.
    public func locateSupport(need: String, location: String) async throws -> String {
        try await inner.agent(
            "Find elderly support services for: \(need) in \(location). Include government services, "
            + "NGOs, and contact details.")
    }

    /// C# `ReviewMedicationListAsync`.
    public func reviewMedicationList(medicationList: String, conditions: String) async throws -> String {
        try await inner.agent(
            "Review this medication list for \(conditions): \(medicationList). Flag potential "
            + "interactions, redundancies, and timing issues. Defer prescribing to clinician.")
    }

    /// C# `SuggestFallPreventionAsync`.
    public func suggestFallPrevention(livingArrangement: String, mobilityNotes: String) async throws -> String {
        try await inner.agent(
            "Suggest fall-prevention measures for \(livingArrangement). Mobility: \(mobilityNotes). "
            + "Cover home modifications, footwear, exercise, vision.")
    }

    /// C# `DraftCheckInPromptsAsync`.
    public func draftCheckInPrompts(residentName: String, interestProfile: String) async throws -> String {
        try await inner.agent(
            "Draft 5 warm, dignified check-in conversation prompts for \(residentName). Interests: "
            + "\(interestProfile). Avoid talk-down language.")
    }

    /// C# `SummariseCarerHandoverAsync`.
    public func summariseCarerHandover(shiftNotes: String) async throws -> String {
        try await inner.agent(
            "Summarise these shift notes for the next carer: \(shiftNotes). SBAR format (Situation, "
            + "Background, Assessment, Recommendation).")
    }
}
