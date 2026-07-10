// TelephonyToolCalling.swift
//
// Port of CircleAI.Telephony.ToolCalling — tool-calling for the voice loop.
// The AI emits a tool call during a turn; the orchestrator dispatches it to
// either a local handler or an HTTPS webhook and returns the result for the
// next turn.
//
// NAMING: the DTO names `ToolDefinition` / `ToolInvocation` / `ToolResult` are
// already taken by Tools.swift (the LLM function-call bridge) with a different
// shape. The telephony tool DTOs are therefore prefixed `Telephony…`. The
// registry keeps the C# names (`IToolCallRegistry`, `DefaultToolCallRegistry`).
//
// The webhook path uses the injected `ITelephonyHttpTransport` instead of a
// raw HttpClient (no real network). The POST body preserves the C# shape:
//   { "call_id": <id>, "tool": <name>, "arguments": <parsed args JSON> }.

import Foundation

// MARK: - TelephonyToolDefinition

/// Tool definition surfaced to the LLM. Port of the C# record
/// `CircleAI.Telephony.ToolDefinition`.
public struct TelephonyToolDefinition: Sendable, Equatable, Codable {
    /// Tool name (function call name).
    public let name: String
    /// Human description used to pick the tool.
    public let description: String
    /// JSON Schema describing the arguments.
    public let argumentsJsonSchema: String

    public init(name: String, description: String, argumentsJsonSchema: String) {
        self.name = name
        self.description = description
        self.argumentsJsonSchema = argumentsJsonSchema
    }
}

// MARK: - TelephonyToolInvocation

/// An invocation of one tool by the model. Port of the C# record
/// `CircleAI.Telephony.ToolInvocation`.
public struct TelephonyToolInvocation: Sendable, Equatable, Codable {
    public let callId: String
    public let toolName: String
    public let argumentsJson: String

    public init(callId: String, toolName: String, argumentsJson: String) {
        self.callId = callId
        self.toolName = toolName
        self.argumentsJson = argumentsJson
    }
}

// MARK: - TelephonyToolResult

/// Result of a tool invocation. Port of the C# record
/// `CircleAI.Telephony.ToolResult`.
public struct TelephonyToolResult: Sendable, Equatable, Codable {
    public let callId: String
    public let succeeded: Bool
    public let resultJson: String
    public let error: String?

    public init(callId: String, succeeded: Bool, resultJson: String, error: String? = nil) {
        self.callId = callId
        self.succeeded = succeeded
        self.resultJson = resultJson
        self.error = error
    }
}

// MARK: - TelephonyLocalToolHandler

/// In-process tool handler. Port of the C# delegate
/// `CircleAI.Telephony.LocalToolHandler`
/// (`ValueTask<string>(string argumentsJson, CancellationToken)`).
public typealias TelephonyLocalToolHandler = @Sendable (_ argumentsJson: String) async throws -> String

// MARK: - IToolCallRegistry

/// Tool registry: register local handlers OR HTTPS webhook URLs against a tool
/// name; the orchestrator dispatches. Port of
/// `CircleAI.Telephony.IToolCallRegistry`.
public protocol IToolCallRegistry: Sendable {
    /// All registered tool definitions.
    var definitions: [TelephonyToolDefinition] { get }

    /// Register a local handler for `definition`.
    func registerLocal(_ definition: TelephonyToolDefinition, handler: @escaping TelephonyLocalToolHandler) throws

    /// Register a webhook URL; the orchestrator POSTs arguments JSON.
    func registerWebhook(_ definition: TelephonyToolDefinition, webhook: URL) throws

    /// Invoke one tool call.
    func invoke(_ invocation: TelephonyToolInvocation) async -> TelephonyToolResult
}

// MARK: - DefaultToolCallRegistry

/// Default in-memory registry. Thread-safe. Port of
/// `CircleAI.Telephony.DefaultToolCallRegistry`.
///
/// The C# `ConcurrentDictionary` (case-insensitive keys) is modelled with an
/// `NSLock`-guarded dictionary keyed by the lowercased tool name (to preserve
/// `StringComparer.OrdinalIgnoreCase` lookup semantics) while retaining the
/// original-cased definition. The webhook dispatch uses the injected
/// `ITelephonyHttpTransport`.
public final class DefaultToolCallRegistry: IToolCallRegistry, @unchecked Sendable {

    private struct Entry {
        let def: TelephonyToolDefinition
        let local: TelephonyLocalToolHandler?
        let webhook: URL?
    }

    private let lock = NSLock()
    /// Keyed by lowercased name (ordinal-ignore-case), value keeps original def.
    private var tools: [String: Entry] = [:]
    /// Preserves registration/replacement order so `definitions` is stable.
    private var order: [String] = []
    private let http: ITelephonyHttpTransport

    public init(http: ITelephonyHttpTransport) {
        self.http = http
    }

    public var definitions: [TelephonyToolDefinition] {
        lock.lock(); defer { lock.unlock() }
        return order.compactMap { tools[$0]?.def }
    }

    public func registerLocal(
        _ definition: TelephonyToolDefinition,
        handler: @escaping TelephonyLocalToolHandler
    ) throws {
        if definition.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw TelephonyError.argument("Tool name is required")
        }
        put(definition, Entry(def: definition, local: handler, webhook: nil))
    }

    public func registerWebhook(_ definition: TelephonyToolDefinition, webhook: URL) throws {
        // Mirror C#: reject non-absolute URIs and empty names.
        // A URL constructed from a relative string has a nil `host`/`scheme`;
        // require an absolute http(s) URL (matching `Uri.IsAbsoluteUri`).
        if webhook.scheme == nil || webhook.host == nil {
            throw TelephonyError.argument("Webhook URL must be absolute.")
        }
        if definition.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw TelephonyError.argument("Tool name is required")
        }
        put(definition, Entry(def: definition, local: nil, webhook: webhook))
    }

    private func put(_ definition: TelephonyToolDefinition, _ entry: Entry) {
        let key = definition.name.lowercased()
        lock.lock()
        if tools[key] == nil { order.append(key) }
        tools[key] = entry
        lock.unlock()
    }

    public func invoke(_ invocation: TelephonyToolInvocation) async -> TelephonyToolResult {
        let key = invocation.toolName.lowercased()
        lock.lock()
        let entry = tools[key]
        lock.unlock()

        guard let entry else {
            return TelephonyToolResult(
                callId: invocation.callId,
                succeeded: false,
                resultJson: "{}",
                error: "Tool '\(invocation.toolName)' is not registered.")
        }

        do {
            if let local = entry.local {
                let resultJson = try await local(invocation.argumentsJson)
                // C#: `resultJson ?? "{}"`. In Swift the handler returns non-nil
                // String; empty is preserved as-is (matches non-null path).
                return TelephonyToolResult(callId: invocation.callId, succeeded: true, resultJson: resultJson)
            }

            if let webhook = entry.webhook {
                // Body: { call_id, tool, arguments: <parsed args JSON element> }.
                // Preserve the C# shape by embedding the arguments JSON verbatim
                // (it is already a JSON document/element in C#).
                let argsElement = invocation.argumentsJson.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    ? "null"
                    : invocation.argumentsJson
                let bodyJson =
                    "{\"call_id\":\(Self.jsonString(invocation.callId))," +
                    "\"tool\":\(Self.jsonString(invocation.toolName))," +
                    "\"arguments\":\(argsElement)}"
                let req = TelephonyHttpRequest(
                    method: .post,
                    path: webhook.absoluteString,
                    body: Data(bodyJson.utf8),
                    contentType: .json)
                let resp = try await http.send(req)
                if !resp.isSuccessStatusCode {
                    let error = resp.bodyString
                    return TelephonyToolResult(
                        callId: invocation.callId,
                        succeeded: false,
                        resultJson: "{}",
                        error: "Webhook \(resp.statusCode): \(Self.truncate(error, 240))")
                }
                let body = resp.bodyString
                return TelephonyToolResult(
                    callId: invocation.callId,
                    succeeded: true,
                    resultJson: body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "{}" : body)
            }

            return TelephonyToolResult(
                callId: invocation.callId,
                succeeded: false,
                resultJson: "{}",
                error: "Tool '\(invocation.toolName)' is registered without a local handler or webhook.")
        } catch {
            return TelephonyToolResult(
                callId: invocation.callId,
                succeeded: false,
                resultJson: "{}",
                error: (error as? TelephonyError)?.description ?? "\(error)")
        }
    }

    /// C# `Truncate(s, max)`: append an ellipsis when longer than `max`.
    private static func truncate(_ s: String, _ max: Int) -> String {
        s.count <= max ? s : String(s.prefix(max)) + "\u{2026}"
    }

    /// Minimal JSON string encoder for the two string fields embedded in the
    /// webhook body (escapes quote, backslash, and control characters).
    private static func jsonString(_ s: String) -> String {
        var out = "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if scalar.value < 0x20 {
                    out += String(format: "\\u%04x", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
        return out
    }
}
