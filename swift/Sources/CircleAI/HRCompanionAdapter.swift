// HRCompanionAdapter.swift
//
// Port of CircleAI.HR.HRCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the hr domain
/// system prompt to every conversational call.
public final class HRCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(HRDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - HR helpers

    /// C# `DraftJobDescriptionAsync`.
    public func draftJobDescription(role: String, requirements: String) async throws -> String {
        try await inner.agent(
            "Draft a compelling, legally compliant job description for: \(role). Requirements: "
            + "\(requirements). Include purpose, responsibilities, qualifications, and EEA statement.")
    }

    /// C# `GeneratePerformanceReviewAsync`.
    public func generatePerformanceReview(employeeName: String, role: String, achievements: String) async throws -> String {
        try await inner.agent(
            "Generate a structured performance review for \(employeeName) (\(role)). Achievements: "
            + "\(achievements). Include ratings, development areas, and SMART goals.")
    }

    /// C# `AdviseOnDisciplinaryAsync`.
    public func adviseOnDisciplinary(misconduct: String, employeeHistory: String) async throws -> String {
        try await inner.agent(
            "Advise on disciplinary action for: \(misconduct). Employee history: \(employeeHistory). "
            + "Apply LRA progressive discipline principles and recommend appropriate sanction.")
    }

    /// C# `StructureInterviewLoopAsync`.
    public func structureInterviewLoop(role: String, hoursAvailable: Int) async throws -> String {
        try await inner.agent(
            "Structure an interview loop for \(role) in \(hoursAvailable) hours. Map each stage to a "
            + "competency, name the evaluator role.")
    }

    /// C# `WritePerformanceFeedbackAsync`.
    public func writePerformanceFeedback(employeeName: String, strengths: String, growthAreas: String) async throws -> String {
        try await inner.agent(
            "Write performance feedback for \(employeeName). Strengths: \(strengths). Growth: "
            + "\(growthAreas). SBI format, specific, future-focused.")
    }

    /// C# `HandleSensitiveHrIssueAsync`.
    public func handleSensitiveHrIssue(situation: String, jurisdiction: String) async throws -> String {
        try await inner.agent(
            "Suggest first-response plan for HR situation: \(situation) in \(jurisdiction). Cover "
            + "legal hold, witness, documentation, escalation path.")
    }
}
