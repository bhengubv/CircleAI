// EducationCompanionAdapter.swift
//
// Port of CircleAI.Education.EducationCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the education domain
/// system prompt to every conversational call.
public final class EducationCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(EducationDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Education helpers

    /// C# `CreateLessonPlanAsync`.
    public func createLessonPlan(subject: String, grade: String, topic: String, duration: String) async throws -> String {
        try await inner.agent(
            "Create a CAPS-aligned lesson plan for Grade \(grade) \(subject): \(topic). Duration: "
            + "\(duration). Include LTSM, activities, differentiation strategies, and assessment "
            + "criteria.")
    }

    /// C# `GenerateRubricAsync`.
    public func generateRubric(assessmentTask: String, grade: String) async throws -> String {
        try await inner.agent(
            "Generate an assessment rubric for Grade \(grade): \(assessmentTask). Include criteria, "
            + "descriptors for 4 performance levels, and weighting.")
    }

    /// C# `DesignLessonPlanAsync`.
    public func designLessonPlan(topic: String, gradeBand: String, minutes: Int) async throws -> String {
        try await inner.agent(
            "Design a \(minutes)-minute lesson plan on '\(topic)' for \(gradeBand). Include "
            + "objectives, hook, instruction, practice, exit ticket.")
    }

    /// C# `GenerateAssessmentAsync`.
    public func generateAssessment(topic: String, bloomsLevel: String, itemCount: Int) async throws -> String {
        try await inner.agent(
            "Generate \(itemCount) assessment items on '\(topic)' at Bloom's \(bloomsLevel) level. "
            + "Mix MCQ + short-answer + one performance task.")
    }

    /// C# `DiagnoseMisconceptionAsync`.
    public func diagnoseMisconception(topic: String, studentResponse: String) async throws -> String {
        try await inner.agent(
            "Diagnose the misconception in this student response on '\(topic)': \(studentResponse). "
            + "Identify the rule the student is following + a corrective move.")
    }

    /// C# `DraftParentUpdateAsync`.
    public func draftParentUpdate(studentName: String, period: String, progressNotes: String) async throws -> String {
        try await inner.agent(
            "Draft a parent update for \(studentName) covering \(period). Notes: \(progressNotes). "
            + "Warm, specific, actionable.")
    }
}
