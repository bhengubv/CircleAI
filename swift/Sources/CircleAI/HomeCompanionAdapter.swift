// HomeCompanionAdapter.swift
//
// Port of CircleAI.Home.HomeCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the home domain
/// system prompt to every conversational call.
public final class HomeCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(HomeDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Home helpers

    /// C# `PlanMaintenanceAsync`.
    public func planMaintenance(homeType: String) async throws -> String {
        try await inner.agent(
            "Create an annual home maintenance schedule for a \(homeType). Include monthly, "
            + "quarterly, bi-annual, and annual tasks with estimated time and cost per task.")
    }

    /// C# `EstimateRenovationAsync`.
    public func estimateRenovation(scope: String, area: String) async throws -> String {
        try await inner.agent(
            "Estimate the cost and timeline for this renovation: \(scope) in \(area). Break down "
            + "labour, materials, and contingency. Identify potential hidden costs.")
    }

    /// C# `ScheduleMaintenanceAsync`.
    public func scheduleMaintenance(homeAge: String, climate: String) async throws -> String {
        try await inner.agent(
            "Generate a 12-month home maintenance schedule for a \(homeAge)-year-old home in "
            + "\(climate) climate. Monthly tasks + seasonal big-ticket items.")
    }

    /// C# `DiagnoseHomeIssueAsync`.
    public func diagnoseHomeIssue(symptom: String, location: String) async throws -> String {
        try await inner.agent(
            "Diagnose home issue: \(symptom) in \(location). List 5 likely causes ranked by "
            + "probability + a 1-minute check for each.")
    }

    /// C# `DesignRoomLayoutAsync`.
    public func designRoomLayout(roomDimensions: String, primaryUse: String, furnitureList: String) async throws -> String {
        try await inner.agent(
            "Design layout for \(roomDimensions) room, primary use: \(primaryUse). Furniture: "
            + "\(furnitureList). Cover circulation, lighting, focal point.")
    }

    /// C# `EstimateRenovationCostAsync`.
    public func estimateRenovationCost(scope: String, region: String, finishLevel: String) async throws -> String {
        try await inner.agent(
            "Estimate \(finishLevel)-finish renovation cost for: \(scope) in \(region). Range with "
            + "20% contingency + biggest cost drivers.")
    }
}
