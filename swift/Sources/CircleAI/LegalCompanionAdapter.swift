// LegalCompanionAdapter.swift
//
// Port of CircleAI.Legal.LegalCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the legal domain
/// system prompt to every conversational call.
public final class LegalCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(LegalDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Legal helpers

    /// C# `ReviewContractClausesAsync`.
    public func reviewContractClauses(contractText: String, focusArea: String) async throws -> String {
        try await inner.agent(
            "Review the following contract for \(focusArea) issues. Identify risky clauses, missing "
            + "protections, and suggest improvements:\n\(contractText)")
    }

    /// C# `DraftContractSummaryAsync`.
    public func draftContractSummary(contractText: String) async throws -> String {
        try await inner.agent(
            "Summarise this contract in plain language. Highlight key obligations, payment terms, IP "
            + "ownership, termination, and dispute resolution:\n\(contractText)")
    }

    /// C# `GenerateComplianceChecklistAsync`.
    public func generateComplianceChecklist(businessType: String, jurisdiction: String) async throws -> String {
        try await inner.agent(
            "Generate a compliance checklist for a \(businessType) operating in \(jurisdiction). "
            + "Cover company registration, tax, labour, data protection, and sector-specific "
            + "regulations.")
    }

    /// C# `SummariseContractAsync`.
    public func summariseContract(contractText: String, clientRole: String) async throws -> String {
        try await inner.agent(
            "Summarise this contract from the \(clientRole)'s perspective: \(contractText). "
            + "Highlight obligations, rights, risks, deadlines.")
    }

    /// C# `DraftClauseAsync`.
    public func draftClause(clauseType: String, position: String, jurisdiction: String) async throws -> String {
        try await inner.agent(
            "Draft a \(clauseType) clause favouring the \(position) in \(jurisdiction). "
            + "Plain-English notes alongside.")
    }

    /// C# `AssessMatterStrengthAsync`.
    public func assessMatterStrength(matterSummary: String) async throws -> String {
        try await inner.agent(
            "Assess this matter's merits: \(matterSummary). Cover liability theory, likely defences, "
            + "evidence gaps, settlement range. Not legal advice.")
    }

    /// C# `TrackDeadlineAsync`.
    public func trackDeadline(matterType: String, keyDate: String, jurisdiction: String) async throws -> String {
        try await inner.agent(
            "Identify all deadlines triggered by \(keyDate) for a \(matterType) matter in "
            + "\(jurisdiction). List date, action, statute reference.")
    }
}
