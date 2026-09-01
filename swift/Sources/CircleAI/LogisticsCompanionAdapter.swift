// LogisticsCompanionAdapter.swift
//
// Port of CircleAI.Logistics.LogisticsCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the logistics domain
/// system prompt to every conversational call.
public final class LogisticsCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(LogisticsDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Logistics helpers

    /// C# `OptimiseRouteAsync`.
    public func optimiseRoute(origin: String, destinations: String, constraints: String) async throws -> String {
        try await inner.agent(
            "Optimise delivery routes from \(origin) to: \(destinations). Constraints: "
            + "\(constraints). Minimise total distance and time while respecting load limits and "
            + "delivery windows.")
    }

    /// C# `PrepareCustomsDocAsync`.
    public func prepareCustomsDoc(shipmentDetails: String, incoterm: String) async throws -> String {
        try await inner.agent(
            "Prepare a customs documentation checklist for: \(shipmentDetails). Incoterm: "
            + "\(incoterm). Include required forms, HS codes guidance, and SARS requirements.")
    }

    /// C# `DraftCustomsDeclarationAsync`.
    public func draftCustomsDeclaration(goodsDescription: String, fromCountry: String, toCountry: String) async throws -> String {
        try await inner.agent(
            "Draft a customs declaration outline for: \(goodsDescription) from \(fromCountry) to "
            + "\(toCountry). HS code lookup, duty, docs list.")
    }

    /// C# `DiagnoseDelayAsync`.
    public func diagnoseDelay(shipmentDetails: String, delayCause: String) async throws -> String {
        try await inner.agent(
            "Diagnose this shipment delay: \(shipmentDetails), cause: \(delayCause). List recovery "
            + "options + customer comms template.")
    }

    /// C# `PlanWarehouseSlottingAsync`.
    public func planWarehouseSlotting(skuVelocityList: String, warehouseLayout: String) async throws -> String {
        try await inner.agent(
            "Plan warehouse slotting for SKUs: \(skuVelocityList) in layout: \(warehouseLayout). "
            + "Optimise for pick-distance + ergonomics.")
    }
}
