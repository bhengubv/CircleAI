// CompanionSessionFactory.swift
//
// Port of CircleAI.Companion.CompanionSessionFactory (CompanionSessionFactory.cs).
//
// Creates CompanionSession instances with all collaborators resolved. Callers
// only need the factory — they never construct CompanionSession directly.
//
// The C# original resolves every optional service from an IServiceProvider.
// Swift has no ambient DI container, so the factory holds its collaborators
// explicitly (constructor-injected): the required trio the concrete
// CompanionSession needs (generator, episodic, recall), plus optional encoder /
// beliefs, plus an optional IIdentityProvider used to resolve a rich display
// name + preferred language for the identity (exactly as CreateAsync does in the
// reference).

import Foundation

/// Contract for creating per-identity, per-surface Companion sessions. Ported
/// from `ICompanionSessionFactory`.
public protocol ICompanionSessionFactory: AnyObject, Sendable {
    /// Creates a new `ICompanionSession` for the given identity and interface
    /// surface. Resolves display name / language from the identity provider when
    /// available.
    func create(identityId: String, interface: InterfaceKind) async throws -> ICompanionSession
}

/// Default `ICompanionSessionFactory`. Ported from `CompanionSessionFactory`.
public final class CompanionSessionFactory: ICompanionSessionFactory, @unchecked Sendable {
    private let generator: IChatGenerator
    private let episodic: IEpisodicMemoryStore
    private let recall: IRecall
    private let encoder: CompanionMemoryEncoder?
    private let beliefs: SelfBeliefStore?
    private let identity: IIdentityProvider?
    private let recallTopK: Int
    private let personaHints: String
    private let affectSummary: String
    private let activeGoals: [String]

    public init(
        generator: IChatGenerator,
        episodic: IEpisodicMemoryStore,
        recall: IRecall,
        encoder: CompanionMemoryEncoder? = nil,
        beliefs: SelfBeliefStore? = nil,
        identity: IIdentityProvider? = nil,
        recallTopK: Int = 5,
        personaHints: String = "",
        affectSummary: String = "",
        activeGoals: [String] = []
    ) {
        self.generator = generator
        self.episodic = episodic
        self.recall = recall
        self.encoder = encoder
        self.beliefs = beliefs
        self.identity = identity
        self.recallTopK = recallTopK
        self.personaHints = personaHints
        self.affectSummary = affectSummary
        self.activeGoals = activeGoals
    }

    public func create(identityId: String, interface: InterfaceKind) async throws -> ICompanionSession {
        precondition(!identityId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "identityId required")

        // Try to resolve a rich display name from the identity store; default to
        // the identity id (matching the reference).
        var displayName = identityId
        var preferredLang: String? = nil

        if let identity {
            if let resolved = try await identity.getCurrentIdentity() {
                displayName = resolved.displayName
                preferredLang = resolved.preferredLanguage
            }
        }

        let options = CompanionSessionOptions(
            sessionId: UUID().uuidString,
            identityId: identityId,
            interface: interface,
            displayName: displayName,
            preferredLanguage: preferredLang,
            personaHints: personaHints,
            affectSummary: affectSummary,
            activeGoals: activeGoals,
            recallTopK: recallTopK,
            appContext: nil)

        return CompanionSession(
            generator: generator,
            episodic: episodic,
            recall: recall,
            options: options,
            encoder: encoder,
            beliefs: beliefs)
    }
}
