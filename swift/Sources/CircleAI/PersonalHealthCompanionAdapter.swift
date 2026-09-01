// PersonalHealthCompanionAdapter.swift
//
// Port of CircleAI.Personal.Health.PersonalHealthCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the personalhealth domain
/// system prompt to every conversational call.
public final class PersonalHealthCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(PersonalHealthDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - PersonalHealth helpers

    /// C# `PrepareAppointmentAsync`.
    public func prepareAppointment(symptoms: String, medHistory: String) async throws -> String {
        try await inner.agent(
            "Help me prepare for a doctor appointment. Symptoms: \(symptoms). Relevant history: "
            + "\(medHistory). Draft a concise symptom summary and list of questions to ask the doctor.")
    }

    /// C# `ExplainHealthTermAsync`.
    public func explainHealthTerm(term: String) async throws -> String {
        try await inner.agent(
            "Explain the medical term or concept in plain language: \(term). Make it accessible to a "
            + "non-medical person.")
    }

    /// C# `InterpretVitalsAsync`.
    public func interpretVitals(vitalsJson: String, age: String, baselineNotes: String) async throws -> String {
        try await inner.agent(
            "Interpret vitals \(vitalsJson) for age \(age). Baseline: \(baselineNotes). Flag "
            + "normal/borderline/concerning. Defer diagnosis to clinician.")
    }

    /// C# `DesignSleepPlanAsync`.
    public func designSleepPlan(currentPattern: String, targetWakeTime: String) async throws -> String {
        try await inner.agent(
            "Design a sleep improvement plan from \(currentPattern) towards waking at "
            + "\(targetWakeTime). Cover light, caffeine, wind-down, environment.")
    }

    /// C# `PrepareForAppointmentAsync`.
    public func prepareForAppointment(concern: String, appointmentType: String) async throws -> String {
        try await inner.agent(
            "Prepare for a \(appointmentType) about: \(concern). Pre-visit checklist: symptoms log, "
            + "questions, medication list, decisions to make.")
    }

    /// C# `TrackHabitImpactAsync`.
    public func trackHabitImpact(habit: String, vitalsBeforeAfter: String) async throws -> String {
        try await inner.agent(
            "Analyse impact of \(habit) on vitals: \(vitalsBeforeAfter). Confounders, signal "
            + "strength, what to keep measuring.")
    }
}
