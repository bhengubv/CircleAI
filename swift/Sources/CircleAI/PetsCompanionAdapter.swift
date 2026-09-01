// PetsCompanionAdapter.swift
//
// Port of CircleAI.Pets.PetsCompanionAdapter.
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

/// An `ICompanionSession` decorator that prepends the pets domain
/// system prompt to every conversational call.
public final class PetsCompanionAdapter: ICompanionSession, @unchecked Sendable {

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
        "\(PetsDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // MARK: - Pets helpers

    /// C# `TriageSymptomAsync`.
    public func triageSymptom(species: String, breed: String, symptom: String) async throws -> String {
        try await inner.agent(
            "Triage this pet health concern. Species: \(species). Breed: \(breed). Symptom: "
            + "\(symptom). Indicate urgency level and whether immediate vet care is needed.")
    }

    /// C# `CreateTrainingPlanAsync`.
    public func createTrainingPlan(species: String, age: String, behaviour: String) async throws -> String {
        try await inner.agent(
            "Create a positive reinforcement training plan for a \(age) \(species) to address: "
            + "\(behaviour). Include daily session structure, reward strategy, and realistic timeline.")
    }

    /// C# `AdviseDietAsync`.
    public func adviseDiet(species: String, lifeStage: String, healthNotes: String) async throws -> String {
        try await inner.agent(
            "Advise diet for \(lifeStage) \(species). Health notes: \(healthNotes). Cover "
            + "composition, portions, transitions, treats.")
    }

    /// C# `PlanTravelWithPetAsync`.
    public func planTravelWithPet(species: String, destination: String, transport: String) async throws -> String {
        try await inner.agent(
            "Plan \(transport) travel to \(destination) with \(species). Documents, crate, breaks, "
            + "stress reduction.")
    }
}
