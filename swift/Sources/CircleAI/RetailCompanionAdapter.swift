// RetailCompanionAdapter.swift
//
// Port of CircleAI.Retail.RetailCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the retail domain
/// system prompt to every conversational call.
public final class RetailCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(RetailDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Retail helpers

    /// C# `AnalyseStockHealthAsync`.
    public func analyseStockHealth(sku: String, onHand: Int, weeklySales: Int) async throws -> String {
        try await inner.agent(
            "Analyse stock health for SKU \(sku): \(onHand) units on hand, \(weeklySales) weekly "
            + "sales. Recommend reorder point, safety stock, and EOQ.")
    }

    /// C# `PlanPromotionAsync`.
    public func planPromotion(objective: String, constraints: String) async throws -> String {
        try await inner.agent(
            "Plan a retail promotion. Objective: \(objective). Constraints: \(constraints). Include "
            + "mechanics, discount level, marketing channels, and success metrics.")
    }

    /// C# `OptimiseProductMixAsync`.
    public func optimiseProductMix(topSellersJson: String, slowMoversJson: String) async throws -> String {
        try await inner.agent(
            "Recommend product mix changes from sellers: \(topSellersJson) and slow: "
            + "\(slowMoversJson). Cover ranging, replenishment, markdown.")
    }

    /// C# `DesignPromotionAsync`.
    public func designPromotion(goal: String, category: String, budget: Decimal) async throws -> String {
        try await inner.agent(
            "Design a \(goal) promotion for \(category) on \(budget) budget. Mechanic, channel mix, "
            + "expected lift, guardrails.")
    }

    /// C# `HandleStockoutAsync`.
    public func handleStockout(sku: String, demandSignal: String, leadTimeDays: Int) async throws -> String {
        try await inner.agent(
            "Handle stockout of \(sku) (demand: \(demandSignal), lead \(leadTimeDays)d). Recovery "
            + "options + customer comms.")
    }

    /// C# `ReviewDailyTradingAsync`.
    public func reviewDailyTrading(salesByCategory: String, targetRevenue: Decimal) async throws -> String {
        try await inner.agent(
            "Review today's trading: \(salesByCategory) vs target \(targetRevenue). Wins, misses, "
            + "tomorrow's adjustments.")
    }
}
