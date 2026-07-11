// MicroAgents.swift
//
// Port of CircleAI.MicroAgents/ — a registry of small single-purpose agents
// routed by id, plus capability search + an invocation log.
//   • Contracts.cs           — MicroAgentDescriptor, MicroAgentResponse,
//                              IMicroAgent, IMicroAgentHost
//   • InMemoryMicroAgents.cs — FuncMicroAgent (lambda adapter)
//   • NullImplementations.cs — NullMicroAgent, InMemoryMicroAgentHost (real)
//   • MicroAgentHelpers.cs   — MicroAgentInvocation, MicroAgentSearch,
//                              MicroAgentInvocationLog
//
// Porting notes:
//   • `IReadOnlyDictionary<string, string>?` metadata → `[String: String]?`.
//   • `FuncMicroAgent` wraps an async closure so callers register lambdas.
//   • `InMemoryMicroAgentHost` lives in C#'s NullImplementations.cs as a REAL
//     (not null) implementation; kept as a real host here.
//   • `MicroAgentSearch` is a static utility (free functions in an enum).

import Foundation

// MARK: - Records

/// Describes a micro-agent's identity + advertised capabilities.
/// (C# `MicroAgentDescriptor`.)
public struct MicroAgentDescriptor: Sendable, Equatable, Codable {
    /// Agent identifier.
    public let agentId: String
    /// Human-readable description.
    public let description: String
    /// Capability tags.
    public let capabilities: [String]

    public init(agentId: String, description: String, capabilities: [String]) {
        self.agentId = agentId
        self.description = description
        self.capabilities = capabilities
    }
}

/// A micro-agent's response to an invocation. (C# `MicroAgentResponse`.)
public struct MicroAgentResponse: Sendable, Equatable, Codable {
    /// Agent that produced the response.
    public let agentId: String
    /// Output text.
    public let output: String
    /// Optional metadata bag.
    public let metadata: [String: String]?

    public init(agentId: String, output: String, metadata: [String: String]? = nil) {
        self.agentId = agentId
        self.output = output
        self.metadata = metadata
    }
}

/// A single logged invocation. (C# `MicroAgentInvocation`.)
public struct MicroAgentInvocation: Sendable, Equatable, Codable {
    public let agentId: String
    public let input: String
    public let responseText: String
    public let atUtc: Date

    public init(agentId: String, input: String, responseText: String, atUtc: Date) {
        self.agentId = agentId
        self.input = input
        self.responseText = responseText
        self.atUtc = atUtc
    }
}

// MARK: - Contracts

/// A single micro-agent. (C# `IMicroAgent`.)
public protocol IMicroAgent: Sendable {
    /// Agent identifier.
    var agentId: String { get }
    /// Backend identifier.
    var backendId: String { get }
    /// Descriptor advertising this agent's capabilities.
    var descriptor: MicroAgentDescriptor { get }
    /// Invokes the agent with `input`.
    func invoke(_ input: String) async -> MicroAgentResponse
}

/// Hosts a registry of micro-agents and routes invocations. (C# `IMicroAgentHost`.)
public protocol IMicroAgentHost: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Registers (or replaces, by id) an agent.
    func register(_ agent: any IMicroAgent)
    /// Lists all registered agents' descriptors.
    func list() -> [MicroAgentDescriptor]
    /// Invokes the agent with `agentId`, or returns `nil` when unknown.
    func invoke(agentId: String, input: String) async -> MicroAgentResponse?
}

// MARK: - FuncMicroAgent

/// Wraps an async closure in an `IMicroAgent` so callers can register lambdas
/// without authoring a new type. (C# `FuncMicroAgent`.)
public final class FuncMicroAgent: IMicroAgent, @unchecked Sendable {
    private let impl: @Sendable (String) async -> MicroAgentResponse

    public init(agentId: String, description: String, capabilities: [String]?,
                impl: @escaping @Sendable (String) async -> MicroAgentResponse) {
        precondition(!agentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "agentId required")
        self.agentId = agentId
        self.descriptor = MicroAgentDescriptor(agentId: agentId, description: description,
                                               capabilities: capabilities ?? [])
        self.impl = impl
    }

    public let agentId: String
    public var backendId: String { "func" }
    public let descriptor: MicroAgentDescriptor

    public func invoke(_ input: String) async -> MicroAgentResponse { await impl(input) }
}

// MARK: - InMemoryMicroAgentHost

/// Real in-memory host that keeps a registry of agents and routes invocations.
/// (C# `InMemoryMicroAgentHost` — a real, not null, implementation.)
public final class InMemoryMicroAgentHost: IMicroAgentHost, @unchecked Sendable {
    private let lock = NSLock()
    private var agents: [String: any IMicroAgent] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func register(_ agent: any IMicroAgent) {
        lock.lock(); agents[agent.agentId] = agent; lock.unlock()
    }

    public func list() -> [MicroAgentDescriptor] {
        lock.lock(); let snap = Array(agents.values); lock.unlock()
        return snap.map { $0.descriptor }
    }

    public func invoke(agentId: String, input: String) async -> MicroAgentResponse? {
        lock.lock(); let agent = agents[agentId]; lock.unlock()
        guard let agent = agent else { return nil }
        return await agent.invoke(input)
    }
}

// MARK: - NullMicroAgent

/// No-op micro-agent — echoes an empty output. (C# `NullMicroAgent`.)
public final class NullMicroAgent: IMicroAgent, @unchecked Sendable {
    public static let instance = NullMicroAgent()
    public init() {}
    public var agentId: String { "null" }
    public var backendId: String { "null" }
    public var descriptor: MicroAgentDescriptor {
        MicroAgentDescriptor(agentId: "null", description: "No-op micro agent", capabilities: [])
    }
    public func invoke(_ input: String) async -> MicroAgentResponse {
        MicroAgentResponse(agentId: agentId, output: "")
    }
}

// MARK: - MicroAgentSearch

/// Capability filter + free-text search over descriptors. (C# `MicroAgentSearch`.)
public enum MicroAgentSearch {
    /// Agents whose descriptor advertises `capability` (case-insensitive),
    /// ordered by agent id.
    public static func byCapability(_ all: [MicroAgentDescriptor], capability: String) -> [MicroAgentDescriptor] {
        precondition(!capability.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "capability required")
        return all
            .filter { d in d.capabilities.contains { $0.caseInsensitiveCompare(capability) == .orderedSame } }
            .sorted { $0.agentId < $1.agentId }
    }

    /// Free-text search over id / description / capabilities (case-insensitive
    /// substring), limited to `topK`. (C# `MicroAgentSearch.Search`.)
    public static func search(_ all: [MicroAgentDescriptor], query: String, topK: Int = 10) -> [MicroAgentDescriptor] {
        precondition(topK > 0, "topK must be positive")
        let q = query.lowercased()
        let matched = all.filter { d in
            d.agentId.lowercased().contains(q) ||
            d.description.lowercased().contains(q) ||
            d.capabilities.contains { $0.lowercased().contains(q) }
        }
        return Array(matched.prefix(topK))
    }
}

// MARK: - MicroAgentInvocationLog

/// Keeps an in-memory invocation log. (C# `MicroAgentInvocationLog`.)
public final class MicroAgentInvocationLog: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [MicroAgentInvocation] = []

    public init() {}

    /// Appends an invocation.
    public func append(_ i: MicroAgentInvocation) {
        lock.lock(); items.append(i); lock.unlock()
    }

    /// Most-recent invocations for `agentId`, newest first, capped at `limit`.
    public func forAgent(_ agentId: String, limit: Int = 50) -> [MicroAgentInvocation] {
        precondition(limit > 0, "limit must be positive")
        lock.lock(); defer { lock.unlock() }
        return Array(items.filter { $0.agentId == agentId }
            .sorted { $0.atUtc > $1.atUtc }
            .prefix(limit))
    }

    /// Total invocations logged.
    public var totalInvocations: Int {
        lock.lock(); defer { lock.unlock() }
        return items.count
    }
}
