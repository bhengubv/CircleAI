// CompanionSession.swift
// The conscious loop: a concrete ICompanionSession that recalls from fused
// memory, persists each turn, and encodes it into the graph off the hot path.
// Ported from Circle.AI.Companion (CompanionSession) — the C# reference.

import Foundation

/// Construction-time configuration for a `CompanionSession`.
public struct CompanionSessionOptions: Sendable {
    public var sessionId: String
    public var identityId: String
    public var interface: InterfaceKind
    public var displayName: String
    public var preferredLanguage: String?
    public var personaHints: String
    public var affectSummary: String
    public var activeGoals: [String]
    public var recallTopK: Int
    public var appContext: String?

    public init(sessionId: String, identityId: String, interface: InterfaceKind,
                displayName: String = "", preferredLanguage: String? = nil,
                personaHints: String = "", affectSummary: String = "",
                activeGoals: [String] = [], recallTopK: Int = 5, appContext: String? = nil) {
        self.sessionId = sessionId
        self.identityId = identityId
        self.interface = interface
        self.displayName = displayName
        self.preferredLanguage = preferredLanguage
        self.personaHints = personaHints
        self.affectSummary = affectSummary
        self.activeGoals = activeGoals
        self.recallTopK = recallTopK
        self.appContext = appContext
    }
}

/// A companion session that thinks with fused memory and remembers what it learns.
public final class CompanionSession: ICompanionSession, @unchecked Sendable {
    public let sessionId: String
    public let identityId: String
    public let interface: InterfaceKind

    private let generator: IChatGenerator
    private let episodic: IEpisodicMemoryStore
    private let recallEngine: IRecall
    private let encoder: CompanionMemoryEncoder?
    private let beliefs: SelfBeliefStore?
    private let options: CompanionSessionOptions

    private let lock = NSLock()
    private var historyList: [CompanionTurn] = []
    private var context: CompanionContext

    public init(generator: IChatGenerator, episodic: IEpisodicMemoryStore, recall: IRecall,
                options: CompanionSessionOptions,
                encoder: CompanionMemoryEncoder? = nil, beliefs: SelfBeliefStore? = nil) {
        self.generator = generator
        self.episodic = episodic
        self.recallEngine = recall
        self.encoder = encoder
        self.beliefs = beliefs
        self.options = options
        self.sessionId = options.sessionId
        self.identityId = options.identityId
        self.interface = options.interface
        self.context = CompanionContext(
            identityId: options.identityId, displayName: options.displayName,
            preferredLanguage: options.preferredLanguage, interface: options.interface,
            personaHints: options.personaHints, affectSummary: options.affectSummary,
            recentMemorySnippets: [], activeGoals: options.activeGoals, contextBuiltAt: Date())
    }

    public var history: [CompanionTurn] {
        lock.lock(); defer { lock.unlock() }
        return historyList
    }

    // Synchronous, lock-guarded state accessors — safe to call from async contexts
    // (the lock is never held across an await).
    private func historySnapshot() -> [CompanionTurn] {
        lock.lock(); defer { lock.unlock() }
        return historyList
    }

    private func commitTurn(_ user: CompanionTurn, _ assistant: CompanionTurn, _ snippets: [String]) {
        lock.lock(); defer { lock.unlock() }
        historyList.append(user)
        historyList.append(assistant)
        context = buildContext(snippets)
    }

    private func setContext(_ snippets: [String]) {
        lock.lock(); defer { lock.unlock() }
        context = buildContext(snippets)
    }

    public func send(_ message: String) async throws -> String {
        let prepared = try await prepare(message)
        let reply = try await generator.generate(messages: prepared.messages, options: nil)
        try await recordTurn(userText: message, reply: reply, snippets: prepared.snippets)
        return reply
    }

    public func stream(_ message: String) -> AsyncStream<String> {
        AsyncStream { continuation in
            let task = Task {
                do {
                    let prepared = try await self.prepare(message)
                    var reply = ""
                    for await chunk in self.generator.stream(messages: prepared.messages, options: nil) {
                        reply += chunk
                        continuation.yield(chunk)
                    }
                    try await self.recordTurn(userText: message, reply: reply, snippets: prepared.snippets)
                } catch {
                    // Stream ends on error.
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func agent(_ instruction: String) async throws -> String {
        // Pilot: no tool-execution loop yet — agentic tool calling is a later slice.
        return try await send(instruction)
    }

    public func getContext() -> CompanionContext {
        lock.lock(); defer { lock.unlock() }
        return context
    }

    public func refreshContext() async throws {
        let hits = try await recallEngine.recall(query: "", queryEmbedding: nil, topK: options.recallTopK)
        let snippets = hits.map { $0.item.text }
        setContext(snippets)
    }

    public func signalFeedback(positive: Bool, note: String?) async throws {
        // Pilot: accepted but not yet routed to a feedback store / affect update.
    }

    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> {
        AsyncStream { $0.finish() }
    }

    // MARK: - internals

    private struct Prepared {
        let messages: [ChatMessage]
        let snippets: [String]
    }

    private func prepare(_ message: String) async throws -> Prepared {
        // Recall runs BEFORE the current turn is persisted — it draws on prior
        // memory, never echoes the message back.
        let hits = try await recallEngine.recall(query: message, queryEmbedding: nil, topK: options.recallTopK)
        let snippets = hits.map { $0.item.text }

        var messages: [ChatMessage] = [ChatMessage(role: "system", content: buildSystemPrompt(snippets))]
        for turn in historySnapshot() { messages.append(ChatMessage(role: turn.role, content: turn.content)) }
        messages.append(ChatMessage(role: "user", content: message))
        return Prepared(messages: messages, snippets: snippets)
    }

    private func recordTurn(userText: String, reply: String, snippets: [String]) async throws {
        let episodeId = UUID()
        let entry = EpisodicMemoryEntry(
            id: episodeId, recordedAt: Date(), userText: userText, assistantText: reply,
            appContext: options.appContext, embedding: nil, tags: nil)
        try await episodic.add(entry)

        // Off the hot path: fill the graph + form attributed beliefs for next time.
        encoder?.enqueue(userText: userText, assistantText: reply, episodeId: episodeId.uuidString)

        let now = Date()
        commitTurn(
            CompanionTurn(role: "user", content: userText, timestamp: now),
            CompanionTurn(role: "assistant", content: reply, timestamp: now),
            snippets)
    }

    private func buildSystemPrompt(_ snippets: [String]) -> String {
        var parts: [String] = []
        let ph = options.personaHints.trimmingCharacters(in: .whitespacesAndNewlines)
        if !ph.isEmpty { parts.append(ph) }
        let af = options.affectSummary.trimmingCharacters(in: .whitespacesAndNewlines)
        if !af.isEmpty { parts.append(af) }

        let facts = userFacts()
        if !facts.isEmpty {
            parts.append("[What you know about the user]\n" + facts.map { "- " + $0 }.joined(separator: "\n"))
        }
        if !snippets.isEmpty {
            parts.append("[Relevant memories]\n" + snippets.map { "- " + $0 }.joined(separator: "\n"))
        }
        return parts.joined(separator: "\n\n")
    }

    private func userFacts() -> [String] {
        guard let b = beliefs else { return [] }
        return b.selfFacts().map { $0.object }
    }

    private func buildContext(_ snippets: [String]) -> CompanionContext {
        CompanionContext(
            identityId: identityId, displayName: options.displayName,
            preferredLanguage: options.preferredLanguage, interface: interface,
            personaHints: options.personaHints, affectSummary: options.affectSummary,
            recentMemorySnippets: snippets, activeGoals: options.activeGoals, contextBuiltAt: Date())
    }
}
