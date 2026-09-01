// SafetyCompanionAdapter.swift
//
// Port of CircleAI.Safety.SafetyCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the safety domain
/// system prompt to every conversational call.
public final class SafetyCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(SafetyDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Safety helpers

    /// C# `CreateEmergencyPlanAsync`.
    public func createEmergencyPlan(householdSize: String, location: String) async throws -> String {
        try await inner.agent(
            "Create a personalised emergency preparedness plan for a \(householdSize)-person "
            + "household in \(location). Include evacuation routes, emergency contacts, go-bag "
            + "checklist, and 72-hour supply list.")
    }

    /// C# `AssessSecurityAsync`.
    public func assessSecurity(propertyType: String, concerns: String) async throws -> String {
        try await inner.agent(
            "Assess home security for a \(propertyType). Concerns: \(concerns). Identify "
            + "vulnerabilities and recommend physical, electronic, and procedural improvements.")
    }

    /// C# `ConductRiskAssessmentAsync`.
    public func conductRiskAssessment(activity: String, environment: String) async throws -> String {
        try await inner.agent(
            "Conduct a risk assessment for \(activity) in \(environment). Hazard, likelihood, "
            + "severity, controls.")
    }

    /// C# `DraftEmergencyResponseAsync`.
    public func draftEmergencyResponse(incidentType: String, siteContext: String) async throws -> String {
        try await inner.agent(
            "Draft emergency response steps for \(incidentType) at \(siteContext). Roles, "
            + "escalation, comms, debrief.")
    }

    /// C# `BriefSafetyToolboxAsync`.
    public func briefSafetyToolbox(task: String, topHazards: String) async throws -> String {
        try await inner.agent(
            "Brief a 5-min toolbox talk for task: \(task). Top hazards: \(topHazards). Controls, "
            + "PPE, sign-off.")
    }

    /// C# `ReviewIncidentReportAsync`.
    public func reviewIncidentReport(incidentNarrative: String) async throws -> String {
        try await inner.agent(
            "Review this incident narrative: \(incidentNarrative). Identify root cause, contributing "
            + "factors, corrective + preventive actions.")
    }
}
