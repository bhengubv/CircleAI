// Domain.swift
//
// Port of src/CircleAI.Domain/ — the domain-specialist contract surface plus
// deterministic in-memory backings:
//   • Contracts.cs              — Ingredient, FinanceSnippet, FinanceFinding,
//                                 SlideOutline, GeneratedPresentation,
//                                 JobApplicationDraft, MemoryItem, MemoryHit,
//                                 SwarmPeer, LoRATrainingSummary; IFoodEmbeddings,
//                                 IFinanceRetrieval, IFinancialAgent,
//                                 IPresentationGenerator, IJobSearchPipeline,
//                                 IMemPalaceStore, IHippoRagStore,
//                                 ISwarmCoordinator, IPersonalLoRA
//   • InMemoryDomain.cs         — the in-memory implementations + LoRAAdapterState
//   • NullImplementations.cs    — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable`. `float[]` → `[Float]`.
//   • `ValueTask` → `async throws`; `DateTimeOffset` → `Date`.
//   • `InMemoryFoodEmbeddings.EmbedAsync`'s unregistered-name fallback uses
//     `string.GetHashCode` in C# — which is process-randomised there, so exact
//     cross-run byte-parity is neither possible nor asserted. The Swift port uses
//     a stable FNV-1a hash so the fallback is at least deterministic within a run.
//   • Guards → `DomainError`.

import Foundation

// MARK: - Records

/// One ingredient with optional canonical form + quantity.
public struct Ingredient: Sendable, Equatable, Codable {
    public let name: String
    public let canonical: String?
    public let quantity: String?
    public init(name: String, canonical: String? = nil, quantity: String? = nil) {
        self.name = name
        self.canonical = canonical
        self.quantity = quantity
    }
}

/// A retrieved finance snippet (text + source + score).
public struct FinanceSnippet: Sendable, Equatable, Codable {
    public let text: String
    public let source: String
    public let score: Float
    public init(text: String, source: String, score: Float) {
        self.text = text
        self.source = source
        self.score = score
    }
}

/// A summarised finance finding with citations.
public struct FinanceFinding: Sendable, Equatable, Codable {
    public let subject: String
    public let summary: String
    public let citations: [String]
    public init(subject: String, summary: String, citations: [String]) {
        self.subject = subject
        self.summary = summary
        self.citations = citations
    }
}

/// One slide outline.
public struct SlideOutline: Sendable, Equatable, Codable {
    public let title: String
    public let body: String
    public let bullets: [String]?
    public init(title: String, body: String, bullets: [String]? = nil) {
        self.title = title
        self.body = body
        self.bullets = bullets
    }
}

/// A generated deck.
public struct GeneratedPresentation: Sendable, Equatable, Codable {
    public let slides: [SlideOutline]
    public let theme: String
    public let format: String
    public init(slides: [SlideOutline], theme: String, format: String) {
        self.slides = slides
        self.theme = theme
        self.format = format
    }
}

/// A drafted job application (resume + cover letter + matched skills).
public struct JobApplicationDraft: Sendable, Equatable, Codable {
    public let resumeText: String
    public let coverLetterText: String
    public let keyMatches: [String]
    public init(resumeText: String, coverLetterText: String, keyMatches: [String]) {
        self.resumeText = resumeText
        self.coverLetterText = coverLetterText
        self.keyMatches = keyMatches
    }
}

// NOTE: `MemoryItem` and `MemoryHit` (the CircleAI.Domain memory-recall
// currency) are already ported in MemoryGraph.swift and are reused here.
// `MemoryHit.score` is a `Double` there (MemoryGraph is the canonical port).

/// A swarm peer with a capability + health score.
public struct SwarmPeer: Sendable, Equatable, Codable {
    public let peerId: String
    public let capability: String
    public let health: Float
    public init(peerId: String, capability: String, health: Float) {
        self.peerId = peerId
        self.capability = capability
        self.health = health
    }
}

/// Summary of a LoRA training run.
public struct LoRATrainingSummary: Sendable, Equatable, Codable {
    public let adapterId: String
    public let stepsTrained: Int
    public let finalLoss: Float
    public init(adapterId: String, stepsTrained: Int, finalLoss: Float) {
        self.adapterId = adapterId
        self.stepsTrained = stepsTrained
        self.finalLoss = finalLoss
    }
}

/// Internal state of a trained adapter.
public struct LoRAAdapterState: Sendable, Equatable, Codable {
    public let adapterId: String
    public let steps: Int
    public let finalLoss: Float
    public let trainedAtUtc: Date
    public init(adapterId: String, steps: Int, finalLoss: Float, trainedAtUtc: Date) {
        self.adapterId = adapterId
        self.steps = steps
        self.finalLoss = finalLoss
        self.trainedAtUtc = trainedAtUtc
    }
}

// MARK: - Errors

public enum DomainError: Error, Equatable, CustomStringConvertible {
    case topKOutOfRange
    case topicRequired
    case targetSlideCountOutOfRange
    case capabilityRequired
    case idRequired
    case adapterIdRequired
    case sampleRequired
    case adapterNotTrained(String)

    public var description: String {
        switch self {
        case .topKOutOfRange: return "topK out of range"
        case .topicRequired: return "topic required"
        case .targetSlideCountOutOfRange: return "targetSlideCount out of range"
        case .capabilityRequired: return "capability required"
        case .idRequired: return "Id required"
        case .adapterIdRequired: return "adapterId required"
        case .sampleRequired: return "at least one sample required"
        case .adapterNotTrained(let id): return "Adapter '\(id)' not trained."
        }
    }
}

// MARK: - Contracts

public protocol IFoodEmbeddings: Sendable {
    var backendId: String { get }
    func embed(_ ingredient: Ingredient) async throws -> [Float]
    func substitutes(_ ingredient: Ingredient, topK: Int) async throws -> [Ingredient]
}

public protocol IFinanceRetrieval: Sendable {
    var backendId: String { get }
    func retrieve(query: String, topK: Int) async throws -> [FinanceSnippet]
}

public protocol IFinancialAgent: Sendable {
    var backendId: String { get }
    func research(question: String) async throws -> [FinanceFinding]
}

public protocol IPresentationGenerator: Sendable {
    var backendId: String { get }
    func generate(topic: String, targetSlideCount: Int, theme: String?) async throws -> GeneratedPresentation
}

public protocol IJobSearchPipeline: Sendable {
    var backendId: String { get }
    func draftApplication(roleDescription: String, candidateProfileText: String) async throws -> JobApplicationDraft
}

public protocol IMemPalaceStore: Sendable {
    var backendId: String { get }
    func upsert(_ item: MemoryItem) async throws
    func recall(query: String, topK: Int) async throws -> [MemoryHit]
}

// NOTE: `IHippoRagStore` is already ported in MemoryGraph.swift and reused here.

public protocol ISwarmCoordinator: Sendable {
    var backendId: String { get }
    func listPeers() async throws -> [SwarmPeer]
    func chooseDelegate(capability: String) async throws -> String?
}

public protocol IPersonalLoRA: Sendable {
    var backendId: String { get }
    func train(adapterId: String, conversationSamples: [String]) async throws -> LoRATrainingSummary
    func loadAdapter(adapterId: String) async throws
    func unloadAdapter(adapterId: String) async throws
}

// MARK: - Food

/// Substitute-by-registered-name food embeddings.
public final class InMemoryFoodEmbeddings: IFoodEmbeddings, @unchecked Sendable {
    private let lock = NSLock()
    private var embeds: [String: [Float]] = [:]     // case-insensitive keys stored lowercased
    private var subs: [String: [Ingredient]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func registerEmbedding(_ name: String, _ v: [Float]) {
        lock.lock(); defer { lock.unlock() }
        embeds[name.lowercased()] = v
    }

    public func registerSubstitute(_ name: String, _ alt: Ingredient) {
        lock.lock(); defer { lock.unlock() }
        subs[name.lowercased(), default: []].append(alt)
    }

    public func embed(_ ingredient: Ingredient) async throws -> [Float] {
        lock.lock()
        let registered = embeds[ingredient.name.lowercased()]
        lock.unlock()
        if let registered { return registered }
        // Deterministic hash-based 8-dim vector when no embedding was registered.
        // (C# uses process-randomised GetHashCode; a stable FNV-1a keeps this
        // deterministic within a run — no test asserts exact cross-run values.)
        let h = InMemoryFoodEmbeddings.stableHash(ingredient.name.lowercased())
        var v = [Float](repeating: 0, count: 8)
        for k in 0..<8 {
            v[k] = Float((h >> (k * 4)) & 0xF) / 15.0
        }
        return v
    }

    public func substitutes(_ ingredient: Ingredient, topK: Int = 5) async throws -> [Ingredient] {
        if topK <= 0 { throw DomainError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        guard let list = subs[ingredient.name.lowercased()] else { return [] }
        return Array(list.prefix(topK))
    }

    /// FNV-1a 32-bit hash — stable across runs (unlike .NET's randomised GetHashCode).
    private static func stableHash(_ s: String) -> UInt32 {
        var hash: UInt32 = 2166136261
        for byte in s.utf8 {
            hash ^= UInt32(byte)
            hash = hash &* 16777619
        }
        return hash
    }
}

// MARK: - Finance

/// Substring-scored finance snippet retrieval.
public final class InMemoryFinanceRetrieval: IFinanceRetrieval, @unchecked Sendable {
    private let lock = NSLock()
    private var corpus: [FinanceSnippet] = []

    public init() {}
    public var backendId: String { "in-memory" }

    public func add(_ s: FinanceSnippet) {
        lock.lock(); defer { lock.unlock() }
        corpus.append(s)
    }

    public func retrieve(query: String, topK: Int = 5) async throws -> [FinanceSnippet] {
        if topK <= 0 { throw DomainError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let hits = corpus
            .filter { $0.text.range(of: query, options: .caseInsensitive) != nil }
            .sorted { $0.score > $1.score }
            .prefix(topK)
        return Array(hits)
    }
}

/// Multi-pass financial agent — decomposes the question, retrieves per
/// sub-question, groups findings by source and summarises each cluster.
public final class MultiPassFinancialAgent: IFinancialAgent, @unchecked Sendable {
    private let retr: any IFinanceRetrieval
    public init(_ r: any IFinanceRetrieval) { self.retr = r }
    public var backendId: String { "multi-pass" }

    public func research(question: String) async throws -> [FinanceFinding] {
        let subQuestions = MultiPassFinancialAgent.decompose(question)
        var findings: [FinanceFinding] = []
        for sub in subQuestions {
            let snippets = try await retr.retrieve(query: sub, topK: 5)
            if snippets.isEmpty { continue }
            // Group by source preserving first-seen order.
            var order: [String] = []
            var bySource: [String: [FinanceSnippet]] = [:]
            for s in snippets {
                if bySource[s.source] == nil { order.append(s.source) }
                bySource[s.source, default: []].append(s)
            }
            for source in order {
                let grp = bySource[source]!
                let summary = grp.sorted { $0.score > $1.score }.prefix(3).map { $0.text }.joined(separator: " | ")
                findings.append(FinanceFinding(subject: sub, summary: summary, citations: [source]))
            }
        }
        return findings
    }

    private static func decompose(_ question: String) -> [String] {
        var subs: [String] = [question]
        if question.range(of: " and ", options: .caseInsensitive) != nil {
            for part in question.components(separatedBy: " and ") {
                let trimmed = part.trimmingCharacters(in: .whitespaces)
                if trimmed.count > 6 { subs.append(trimmed) }
            }
        }
        if question.count > 60 {
            let first = (question.components(separatedBy: ",").first ?? question).trimmingCharacters(in: .whitespaces)
            subs.append(first)
        }
        // Distinct, preserving order.
        var seen = Set<String>()
        return subs.filter { seen.insert($0).inserted }
    }
}

// MARK: - Presentations

/// Template presentation generator.
public struct TemplatePresentationGenerator: IPresentationGenerator {
    public init() {}
    public var backendId: String { "template" }

    public func generate(topic: String, targetSlideCount: Int = 10, theme: String? = nil) async throws -> GeneratedPresentation {
        if topic.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DomainError.topicRequired }
        if targetSlideCount <= 0 { throw DomainError.targetSlideCountOutOfRange }
        var slides: [SlideOutline] = []
        slides.append(SlideOutline(title: topic, body: "Overview", bullets: ["What is \(topic)", "Why it matters", "What we'll cover"]))
        if targetSlideCount > 2 {
            for i in 2..<targetSlideCount {
                slides.append(SlideOutline(title: "\(topic) — Part \(i - 1)", body: "Detail for part \(i - 1)", bullets: ["Point A", "Point B", "Point C"]))
            }
        }
        slides.append(SlideOutline(title: "Conclusion", body: "Summary of \(topic)", bullets: ["Recap", "Next steps", "Questions"]))
        return GeneratedPresentation(slides: slides, theme: theme ?? "default", format: "markdown")
    }
}

// MARK: - Job search

/// Keyword-intersection job-application drafter.
public struct TemplateJobSearchPipeline: IJobSearchPipeline {
    public init() {}
    public var backendId: String { "template" }

    public func draftApplication(roleDescription: String, candidateProfileText: String) async throws -> JobApplicationDraft {
        let roleWords = TemplateJobSearchPipeline.extractKeyWords(roleDescription)
        let candSet = Set(TemplateJobSearchPipeline.extractKeyWords(candidateProfileText))
        // Intersect preserving role-word order (mirrors Enumerable.Intersect semantics
        // which yield in first-sequence order), case-insensitive (words already lowercased).
        var seen = Set<String>()
        let matches = Array(roleWords.filter { candSet.contains($0) && seen.insert($0).inserted }.prefix(10))
        let resume = "\(candidateProfileText.trimmingCharacters(in: .whitespacesAndNewlines))\n\nMatched skills: \(matches.joined(separator: ", "))"
        let cover = "Dear Hiring Team,\n\nI am applying because my background (\(matches.prefix(3).joined(separator: ", "))) fits the role.\n\nRegards."
        return JobApplicationDraft(resumeText: resume, coverLetterText: cover, keyMatches: matches)
    }

    private static func extractKeyWords(_ text: String) -> [String] {
        let seps = CharacterSet(charactersIn: " \n\r\t,.;:()")
        var seen = Set<String>()
        return text.components(separatedBy: seps)
            .filter { $0.count > 3 }
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            .filter { !$0.isEmpty && seen.insert($0).inserted }
    }
}

// MARK: - Memory upgrades

/// Substring-recency in-memory long-term memory (MemPalace pattern).
public final class InMemoryMemPalaceStore: IMemPalaceStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: MemoryItem] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func upsert(_ item: MemoryItem) async throws {
        if item.id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DomainError.idRequired }
        lock.lock(); defer { lock.unlock() }
        items[item.id] = item
    }

    public func recall(query: String, topK: Int = 5) async throws -> [MemoryHit] {
        if topK <= 0 { throw DomainError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let hits = items.values
            .map { MemoryHit(item: $0, score: InMemoryMemPalaceStore.score($0.text, query)) }
            .filter { $0.score > 0 }
            .sorted { $0.score > $1.score }
            .prefix(topK)
        return Array(hits)
    }

    // `MemoryHit.score` is `Double` (MemoryGraph's canonical port), so scoring
    // is computed as Double here (the C# used `float`; the value is the same).
    static func score(_ body: String, _ query: String) -> Double {
        if body.isEmpty || query.isEmpty { return 0 }
        let q = query.trimmingCharacters(in: .whitespaces)
        guard let range = body.range(of: q, options: .caseInsensitive) else { return 0 }
        let idx = body.distance(from: body.startIndex, to: range.lowerBound)
        return 1.0 / (1.0 + Double(idx))
    }
}

// NOTE: `InMemoryHippoRagStore` (the CircleAI.Domain HippoRAG multi-hop store)
// is already ported in MemoryGraph.swift and is not re-declared here.

// MARK: - Swarm

/// Health-ranked in-memory swarm coordinator.
public final class InMemorySwarmCoordinator: ISwarmCoordinator, @unchecked Sendable {
    private let lock = NSLock()
    private var peers: [String: SwarmPeer] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func register(_ p: SwarmPeer) {
        lock.lock(); defer { lock.unlock() }
        peers[p.peerId] = p
    }

    public func listPeers() async throws -> [SwarmPeer] {
        lock.lock(); defer { lock.unlock() }
        return Array(peers.values)
    }

    public func chooseDelegate(capability: String) async throws -> String? {
        if capability.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DomainError.capabilityRequired }
        lock.lock(); defer { lock.unlock() }
        let pick = peers.values
            .filter { $0.capability.caseInsensitiveCompare(capability) == .orderedSame }
            .sorted { $0.health > $1.health }
            .first
        return pick?.peerId
    }
}

// MARK: - Personal LoRA

/// In-memory adapter manager with a simulated training loop.
public final class InMemoryPersonalLoRA: IPersonalLoRA, @unchecked Sendable {
    private let lock = NSLock()
    private var adapters: [String: LoRAAdapterState] = [:]
    private var loaded: Set<String> = []

    public init() {}
    public var backendId: String { "in-memory" }

    public func train(adapterId: String, conversationSamples: [String]) async throws -> LoRATrainingSummary {
        if adapterId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DomainError.adapterIdRequired }
        if conversationSamples.isEmpty { throw DomainError.sampleRequired }

        // Simulated training loop — loss decreases logarithmically with sample count.
        let steps = conversationSamples.count
        let totalChars = conversationSamples.reduce(0) { $0 + $1.count }
        let finalLoss = Float(1.0 / (1.0 + log(1.0 + Double(steps))) + 1.0 / (1.0 + Double(totalChars) / 1000.0))
        let state = LoRAAdapterState(adapterId: adapterId, steps: steps, finalLoss: finalLoss, trainedAtUtc: Date())
        lock.lock()
        adapters[adapterId] = state
        lock.unlock()
        return LoRATrainingSummary(adapterId: adapterId, stepsTrained: steps, finalLoss: finalLoss)
    }

    public func loadAdapter(adapterId: String) async throws {
        if adapterId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DomainError.adapterIdRequired }
        lock.lock(); defer { lock.unlock() }
        if adapters[adapterId] == nil { throw DomainError.adapterNotTrained(adapterId) }
        loaded.insert(adapterId)
    }

    public func unloadAdapter(adapterId: String) async throws {
        if adapterId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DomainError.adapterIdRequired }
        lock.lock(); defer { lock.unlock() }
        loaded.remove(adapterId)
    }

    public func isLoaded(_ adapterId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return loaded.contains(adapterId)
    }

    public func stateOf(_ adapterId: String) -> LoRAAdapterState? {
        lock.lock(); defer { lock.unlock() }
        return adapters[adapterId]
    }
}

// MARK: - Null backends

public struct NullFoodEmbeddings: IFoodEmbeddings {
    public static let instance = NullFoodEmbeddings()
    public init() {}
    public var backendId: String { "null" }
    public func embed(_ ingredient: Ingredient) async throws -> [Float] { [Float](repeating: 0, count: 300) }
    public func substitutes(_ ingredient: Ingredient, topK: Int = 5) async throws -> [Ingredient] { [] }
}

public struct NullFinanceRetrieval: IFinanceRetrieval {
    public static let instance = NullFinanceRetrieval()
    public init() {}
    public var backendId: String { "null" }
    public func retrieve(query: String, topK: Int = 5) async throws -> [FinanceSnippet] { [] }
}

public struct NullFinancialAgent: IFinancialAgent {
    public static let instance = NullFinancialAgent()
    public init() {}
    public var backendId: String { "null" }
    public func research(question: String) async throws -> [FinanceFinding] { [] }
}

public struct NullPresentationGenerator: IPresentationGenerator {
    public static let instance = NullPresentationGenerator()
    public init() {}
    public var backendId: String { "null" }
    public func generate(topic: String, targetSlideCount: Int = 10, theme: String? = nil) async throws -> GeneratedPresentation {
        GeneratedPresentation(slides: [], theme: theme ?? "default", format: "json")
    }
}

public struct NullJobSearchPipeline: IJobSearchPipeline {
    public static let instance = NullJobSearchPipeline()
    public init() {}
    public var backendId: String { "null" }
    public func draftApplication(roleDescription: String, candidateProfileText: String) async throws -> JobApplicationDraft {
        JobApplicationDraft(resumeText: "", coverLetterText: "", keyMatches: [])
    }
}

public struct NullMemPalaceStore: IMemPalaceStore {
    public static let instance = NullMemPalaceStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ item: MemoryItem) async throws {}
    public func recall(query: String, topK: Int = 5) async throws -> [MemoryHit] { [] }
}

public struct NullHippoRagStore: IHippoRagStore {
    public static let instance = NullHippoRagStore()
    public init() {}
    public var backendId: String { "null" }
    public func index(_ item: MemoryItem) async throws {}
    public func multiHopRecall(query: String, topK: Int = 5) async throws -> [MemoryHit] { [] }
}

public struct NullSwarmCoordinator: ISwarmCoordinator {
    public static let instance = NullSwarmCoordinator()
    public init() {}
    public var backendId: String { "null" }
    public func listPeers() async throws -> [SwarmPeer] { [] }
    public func chooseDelegate(capability: String) async throws -> String? { nil }
}

public struct NullPersonalLoRA: IPersonalLoRA {
    public static let instance = NullPersonalLoRA()
    public init() {}
    public var backendId: String { "null" }
    public func train(adapterId: String, conversationSamples: [String]) async throws -> LoRATrainingSummary {
        LoRATrainingSummary(adapterId: adapterId, stepsTrained: 0, finalLoss: 0)
    }
    public func loadAdapter(adapterId: String) async throws {}
    public func unloadAdapter(adapterId: String) async throws {}
}
