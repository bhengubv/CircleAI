// RealEstateCompanionAdapter.swift
//
// Port of CircleAI.RealEstate.RealEstateCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the realestate domain
/// system prompt to every conversational call.
public final class RealEstateCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(RealEstateDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - RealEstate helpers

    /// C# `ComparePropertiesAsync`.
    public func compareProperties(prop1: String, prop2: String) async throws -> String {
        try await inner.agent(
            "Compare these two properties and recommend which offers better investment "
            + "value:\nProperty 1:\n\(prop1)\nProperty 2:\n\(prop2)")
    }

    /// C# `DraftLeaseAsync`.
    public func draftLease(landlordName: String, tenantName: String, address: String, monthlyRent: Decimal, months: Int) async throws -> String {
        try await inner.agent(
            "Draft a residential lease agreement. Landlord: \(landlordName). Tenant: \(tenantName). "
            + "Property: \(address). Rent: \(monthlyRent)/month. Term: \(months) months. Include "
            + "deposit, maintenance, and termination clauses per Rental Housing Act.")
    }

    /// C# `ValuePropertyAsync`.
    public func valueProperty(propertyDescription: String, suburb: String, comparableSales: String) async throws -> String {
        try await inner.agent(
            "Estimate value for \(propertyDescription) in \(suburb). Comps: \(comparableSales). "
            + "Range, drivers, market caveats.")
    }

    /// C# `DraftListingAsync`.
    public func draftListing(propertyDescription: String, targetBuyer: String) async throws -> String {
        try await inner.agent(
            "Draft a property listing for \(propertyDescription) targeting \(targetBuyer). Headline, "
            + "hero paragraph, features, lifestyle close.")
    }

    /// C# `AnalyseOfferAsync`.
    public func analyseOffer(offerAmount: String, listingPrice: String, marketConditions: String) async throws -> String {
        try await inner.agent(
            "Analyse offer \(offerAmount) vs list \(listingPrice) in market: \(marketConditions). "
            + "Counter strategy, negotiation levers.")
    }

    /// C# `PrepareViewingAsync`.
    public func prepareViewing(propertyType: String, targetSegment: String) async throws -> String {
        try await inner.agent(
            "Plan an open viewing for \(propertyType) aimed at \(targetSegment). Staging, route, "
            + "FAQs, follow-up cadence.")
    }
}
