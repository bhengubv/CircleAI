// PersonalFinanceCompanionAdapter.swift
//
// Port of CircleAI.Personal.Finance.PersonalFinanceCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the personalfinance domain
/// system prompt to every conversational call.
public final class PersonalFinanceCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(PersonalFinanceDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - PersonalFinance helpers

    /// C# `BuildBudgetAsync`.
    public func buildBudget(income: String, expenses: String) async throws -> String {
        try await inner.agent(
            "Build a monthly budget. Income: \(income). Expenses: \(expenses). Apply the 50/30/20 "
            + "rule, identify savings opportunities, and flag over-spending categories.")
    }

    /// C# `CreateDebtPlanAsync`.
    public func createDebtPlan(debts: String) async throws -> String {
        try await inner.agent(
            "Create a debt elimination plan using the avalanche method (highest interest "
            + "first):\n\(debts)\nShow monthly payment schedule, total interest saved, and debt-free "
            + "date.")
    }

    /// C# `AnalyseSpendingAsync`.
    public func analyseSpending(categoryBreakdown: String, monthlyIncome: String) async throws -> String {
        try await inner.agent(
            "Analyse spending \(categoryBreakdown) against income \(monthlyIncome). Identify 2 leaks "
            + "+ a realistic redirect target.")
    }

    /// C# `DesignSavingsGoalAsync`.
    public func designSavingsGoal(goal: String, targetAmount: Decimal, monthsAvailable: Int) async throws -> String {
        try await inner.agent(
            "Plan to save \(targetAmount) for '\(goal)' in \(monthsAvailable) months. Monthly target "
            + "+ behavioural commitment device.")
    }

    /// C# `ExplainTaxImpactAsync`.
    public func explainTaxImpact(scenario: String, jurisdiction: String) async throws -> String {
        try await inner.agent(
            "Explain tax impact of: \(scenario) in \(jurisdiction). Likely treatment, paperwork, "
            + "optimisation lever. Not tax advice.")
    }

    /// C# `ReviewInvestmentMixAsync`.
    public func reviewInvestmentMix(portfolio: String, riskAppetite: String, horizonYears: Int) async throws -> String {
        try await inner.agent(
            "Review investment mix: \(portfolio) against \(riskAppetite) appetite, "
            + "\(horizonYears)-year horizon. Coverage, concentration, fee drag.")
    }
}
