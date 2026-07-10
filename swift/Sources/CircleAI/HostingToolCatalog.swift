// HostingToolCatalog.swift
//
// Port of the CircleAI.Hosting.Tools catalog surface:
//   - Tools/IToolDescriptor.cs → ToolDescriptor, ToolExecutionResult
//   - Tools/IToolCatalog.cs    → IToolCatalog, IToolProvider, IToolExecutor
//   - Tools/InMemoryToolCatalog.cs → InMemoryToolCatalog + importFrom extension
//
// The searchable registry of every tool the host knows about. Keyword-substring
// search scored name(5) / tags(3) / description(2), ordered desc then by name.

import Foundation

// =====================================================================
// ToolDescriptor + ToolExecutionResult
// =====================================================================

/// Describes one tool callable by an LLM. Data-only; execution lives in
/// `IToolExecutor`. Ported from `ToolDescriptor`.
public struct ToolDescriptor: Sendable, Equatable {
    public let name: String
    public let description: String
    public let provider: String
    public let jsonSchema: String
    public let authScheme: String
    public let tags: [String]?
    public let examples: [String]?

    public init(
        name: String,
        description: String,
        provider: String,
        jsonSchema: String = "",
        authScheme: String = "none",
        tags: [String]? = nil,
        examples: [String]? = nil
    ) {
        self.name = name
        self.description = description
        self.provider = provider
        self.jsonSchema = jsonSchema
        self.authScheme = authScheme
        self.tags = tags
        self.examples = examples
    }
}

/// Result of one tool execution. Ported from `ToolExecutionResult`.
public struct ToolExecutionResult: @unchecked Sendable {
    public let success: Bool
    public let result: Any?
    public let error: String?
    public let durationMs: Int64

    public init(success: Bool, result: Any? = nil, error: String? = nil, durationMs: Int64 = 0) {
        self.success = success
        self.result = result
        self.error = error
        self.durationMs = durationMs
    }
}

// =====================================================================
// IToolCatalog / IToolProvider / IToolExecutor
// =====================================================================

/// The CircleAI tool catalog. Searchable by name, tag, and free-form query.
/// Ported from `IToolCatalog`.
public protocol IToolCatalog: AnyObject, Sendable {
    /// How many tools are currently registered.
    var count: Int { get }
    /// Register or replace one tool. Idempotent for same name.
    func upsert(_ descriptor: ToolDescriptor) async
    /// Remove a tool by name. Idempotent. Returns whether one was removed.
    @discardableResult
    func remove(name: String) async -> Bool
    /// Get exactly one descriptor by name, or nil when unknown.
    func get(name: String) async -> ToolDescriptor?
    /// Enumerate every registered descriptor (stable order within a process).
    func list() -> [ToolDescriptor]
    /// Free-form keyword-substring search over name + description + tags.
    func search(_ query: String, topK: Int) -> [ToolDescriptor]
    /// Filter by provider id (case-insensitive exact match).
    func listByProvider(_ provider: String) -> [ToolDescriptor]
}

public extension IToolCatalog {
    func search(_ query: String) -> [ToolDescriptor] { search(query, topK: 10) }
}

/// A source of tools — integrations, MCP server, AetherNet peer. Registers its
/// descriptors against an `IToolCatalog` at startup. Ported from `IToolProvider`.
public protocol IToolProvider: Sendable {
    /// Stable provider id, e.g. "local" / "composio" / "mcp".
    var providerId: String { get }
    /// Discover every tool this provider exposes.
    func discover() async throws -> [ToolDescriptor]
    /// Cheap availability probe.
    func isAvailable() async throws -> Bool
}

/// Sandboxed execution surface. Routes a call to the owning provider and
/// validates args before dispatch. Ported from `IToolExecutor`.
public protocol IToolExecutor: Sendable {
    /// Execute one tool call. `argumentsJson` is the model-emitted JSON object.
    func execute(_ tool: ToolDescriptor, argumentsJson: String) async throws -> ToolExecutionResult
}

// =====================================================================
// InMemoryToolCatalog
// =====================================================================

/// Default `IToolCatalog` — in-memory + keyword-substring search. Thread-safe.
/// Ported from `InMemoryToolCatalog`.
public final class InMemoryToolCatalog: IToolCatalog, @unchecked Sendable {
    private let lock = NSLock()
    // Case-insensitive keying (StringComparer.OrdinalIgnoreCase) via lowered key.
    private var byName: [String: ToolDescriptor] = [:]

    public init() {}

    public var count: Int { lock.lock(); defer { lock.unlock() }; return byName.count }

    public func upsert(_ descriptor: ToolDescriptor) async {
        precondition(!descriptor.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "name required")
        lock.lock(); byName[descriptor.name.lowercased()] = descriptor; lock.unlock()
    }

    @discardableResult
    public func remove(name: String) async -> Bool {
        precondition(!name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "name required")
        lock.lock(); defer { lock.unlock() }
        return byName.removeValue(forKey: name.lowercased()) != nil
    }

    public func get(name: String) async -> ToolDescriptor? {
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }
        lock.lock(); defer { lock.unlock() }
        return byName[name.lowercased()]
    }

    public func list() -> [ToolDescriptor] {
        lock.lock(); let values = Array(byName.values); lock.unlock()
        return values.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    public func search(_ query: String, topK: Int = 10) -> [ToolDescriptor] {
        if query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || topK <= 0 { return [] }
        let terms = query.split(separator: " ", omittingEmptySubsequences: true).map {
            String($0).trimmingCharacters(in: .whitespaces)
        }.filter { !$0.isEmpty }

        lock.lock(); let values = Array(byName.values); lock.unlock()
        let scored = values
            .map { (tool: $0, score: Self.scoreMatch($0, terms: terms)) }
            .filter { $0.score > 0 }
            .sorted {
                if $0.score != $1.score { return $0.score > $1.score }
                return $0.tool.name.lowercased() < $1.tool.name.lowercased()
            }
            .prefix(topK)
            .map { $0.tool }
        return Array(scored)
    }

    public func listByProvider(_ provider: String) -> [ToolDescriptor] {
        precondition(!provider.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "provider required")
        lock.lock(); let values = Array(byName.values); lock.unlock()
        return values
            .filter { $0.provider.caseInsensitiveCompare(provider) == .orderedSame }
            .sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    private static func scoreMatch(_ d: ToolDescriptor, terms: [String]) -> Int {
        let name = d.name
        let desc = d.description
        let tagBlob = (d.tags ?? []).joined(separator: " ")
        var score = 0
        for t in terms {
            if name.range(of: t, options: .caseInsensitive) != nil { score += 5 }
            if desc.range(of: t, options: .caseInsensitive) != nil { score += 2 }
            if tagBlob.range(of: t, options: .caseInsensitive) != nil { score += 3 }
        }
        return score
    }
}

public extension IToolCatalog {
    /// Discover and import every tool from `provider` into this catalog. Returns
    /// how many were imported. Mirrors `ToolCatalogExtensions.ImportFromAsync`.
    @discardableResult
    func importFrom(_ provider: any IToolProvider) async throws -> Int {
        let tools = try await provider.discover()
        var count = 0
        for tool in tools {
            await upsert(tool)
            count += 1
        }
        return count
    }
}
