// HealthcareCompanionAdapter.swift
//
// Port of CircleAI.Healthcare.HealthcareCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the healthcare domain
/// system prompt to every conversational call.
public final class HealthcareCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(HealthcareDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Healthcare helpers

    /// C# `DocumentClinicalNoteAsync`.
    public func documentClinicalNote(patientVisitSummary: String) async throws -> String {
        try await inner.agent(
            "Format this patient visit summary into a structured SOAP clinical "
            + "note:\n\(patientVisitSummary)")
    }

    /// C# `SuggestIcd10CodesAsync`.
    public func suggestIcd10Codes(diagnosis: String) async throws -> String {
        try await inner.agent(
            "Suggest relevant ICD-10-CM codes for the following diagnosis/condition: \(diagnosis). "
            + "Include primary and secondary codes with descriptions.")
    }

    /// C# `DraftPatientCommunicationAsync`.
    public func draftPatientCommunication(purpose: String, patientContext: String) async throws -> String {
        try await inner.agent(
            "Draft a clear, empathetic patient communication for: \(purpose). Patient context: "
            + "\(patientContext). Keep language accessible (Grade 8 reading level).")
    }

    /// C# `TriageSymptomsAsync`.
    public func triageSymptoms(patientAge: String, symptoms: String, duration: String) async throws -> String {
        try await inner.agent(
            "Triage symptoms for \(patientAge)-year-old: \(symptoms), duration \(duration). Output "
            + "urgency (emergency/urgent/routine), red flags, next step. Defer diagnosis to clinician.")
    }

    /// C# `ExplainMedicationAsync`.
    public func explainMedication(medication: String, indication: String) async throws -> String {
        try await inner.agent(
            "Explain \(medication) prescribed for \(indication) to a patient. Cover purpose, dose "
            + "schedule, common side effects, when to call.")
    }

    /// C# `DraftReferralLetterAsync`.
    public func draftReferralLetter(fromProvider: String, toSpecialty: String, clinicalSummary: String) async throws -> String {
        try await inner.agent(
            "Draft a referral letter from \(fromProvider) to \(toSpecialty). Clinical summary: "
            + "\(clinicalSummary). Include reason, history, exam, ask.")
    }

    /// C# `CounselOnAdherenceAsync`.
    public func counselOnAdherence(medication: String, patientConcerns: String) async throws -> String {
        try await inner.agent(
            "Counsel on adherence to \(medication). Patient concerns: \(patientConcerns). Address "
            + "each with evidence + practical strategies.")
    }
}
