// HostingMcp.swift
//
// Port of the CircleAI.Hosting.Mcp surface:
//   - Contracts.cs    → IMcpTool, IMcpResourceProvider, McpResource,
//                       McpResourceContent, McpToolError
//   - McpEndpoints.cs → McpServerInfo, McpDispatcher (the JSON-RPC 2.0 dispatch
//                       logic of MapMcpApi/DispatchAsync, minus the ASP.NET
//                       socket layer — routing/registry are injected)
//
// POST /mcp is JSON-RPC 2.0 over `[String: Any]` JSON objects; the dispatcher
// handles initialize / tools.list / tools.call / resources.list / resources.read
// and returns response objects (or nil for notifications), exactly as the C#
// `DispatchAsync`.

import Foundation

// =====================================================================
// Contracts
// =====================================================================

/// Signals a tool-level error (vs an MCP protocol error). The dispatcher returns
/// this as `{content:[{type:"text",text:msg}], isError:true}`. Ported from
/// `McpToolException`.
public struct McpToolError: Error, Equatable {
    public let message: String
    public init(_ message: String) { self.message = message }
}

/// One MCP tool the host exposes. Ported from `IMcpTool`.
public protocol IMcpTool: Sendable {
    /// Unique tool name (snake_case by convention).
    var name: String { get }
    /// One-line description shown in tool listings.
    var description: String { get }
    /// JSON Schema describing the tool's `arguments` object. Included verbatim in
    /// `tools/list`. Represented as a JSON-compatible value (`[String: Any]`).
    var inputSchema: [String: Any] { get }
    /// Execute the tool. Return any JSON-serialisable value; the dispatcher wraps
    /// it in the MCP text envelope. Throw `McpToolError` for a tool-level error.
    func execute(arguments: [String: Any]) async throws -> Any
}

/// One MCP resource descriptor. Ported from `McpResource`.
public struct McpResource: Sendable, Equatable {
    public let uri: String
    public let name: String
    public let description: String?
    public let mimeType: String

    public init(uri: String, name: String, description: String?, mimeType: String) {
        self.uri = uri
        self.name = name
        self.description = description
        self.mimeType = mimeType
    }
}

/// One MCP resource content (returned by resources/read). Ported from
/// `McpResourceContent`.
public struct McpResourceContent: Sendable, Equatable {
    public let uri: String
    public let mimeType: String
    public let text: String

    public init(uri: String, mimeType: String, text: String) {
        self.uri = uri
        self.mimeType = mimeType
        self.text = text
    }
}

/// One MCP resource provider. Ported from `IMcpResourceProvider`.
public protocol IMcpResourceProvider: Sendable {
    /// e.g. "vault://", "models://".
    var uriScheme: String { get }
    /// List every resource this provider serves.
    func list() async throws -> [McpResource]
    /// Read one resource by uri. Returns nil on not-found.
    func read(uri: String) async throws -> McpResourceContent?
}

// =====================================================================
// McpDispatcher (JSON-RPC 2.0)
// =====================================================================

/// Server identity advertised in `initialize`. Ported from
/// `McpEndpoints.McpServerInfo`.
public struct McpServerInfo: Sendable {
    public let name: String
    public let version: String
    public let description: String

    public init(name: String = "circleai-mcp", version: String = "3.2.0",
                description: String = "CircleAI MCP endpoint") {
        self.name = name
        self.version = version
        self.description = description
    }
}

/// Pure-DI JSON-RPC 2.0 dispatcher. Registered tools + resource providers are
/// injected (the C# host resolves them from DI). Ported from the dispatch logic
/// of `McpEndpoints.DispatchAsync`.
///
/// `dispatch(_:)` returns the response object for a single request, or nil for a
/// notification. `dispatchBatch(_:)` mirrors POST /mcp with an array body.
public final class McpDispatcher: @unchecked Sendable {
    private let tools: [any IMcpTool]
    private let resources: [any IMcpResourceProvider]
    private let info: McpServerInfo

    public init(tools: [any IMcpTool], resources: [any IMcpResourceProvider], info: McpServerInfo = McpServerInfo()) {
        self.tools = tools
        self.resources = resources
        self.info = info
    }

    /// Dispatch a single JSON-RPC request object. Returns nil for notifications
    /// (e.g. `notifications/initialized`). Mirrors `DispatchAsync`.
    public func dispatch(_ req: [String: Any]?) async -> [String: Any]? {
        guard let req = req else { return Self.errorObj(id: nil, code: -32600, message: "Invalid Request") }

        let id = req["id"]
        let isV2 = (req["jsonrpc"] as? String) == "2.0"
        let method = isV2 ? (req["method"] as? String) : nil
        guard let method = method else {
            return Self.errorObj(id: id, code: -32600, message: "Invalid Request: missing jsonrpc or method")
        }

        let params = req["params"]
        switch method {
        case "initialize":
            return handleInitialize(id: id)
        case "notifications/initialized":
            return nil
        case "tools/list":
            return handleToolsList(id: id)
        case "tools/call":
            return await handleToolsCall(id: id, params: params)
        case "resources/list":
            return await handleResourcesList(id: id)
        case "resources/read":
            return await handleResourcesRead(id: id, params: params)
        default:
            return Self.errorObj(id: id, code: -32601, message: "Method not found: \(method)")
        }
    }

    /// Dispatch a batch (array) or single request, matching POST /mcp. Returns
    /// the array of non-nil responses for a batch, or the single response.
    public func dispatchBatch(_ body: Any?) async -> Any? {
        if let batch = body as? [Any] {
            var responses: [[String: Any]] = []
            for item in batch {
                if let r = await dispatch(item as? [String: Any]) { responses.append(r) }
            }
            return responses
        }
        return await dispatch(body as? [String: Any])
    }

    // ------------------------------------------------------------------
    // Handlers
    // ------------------------------------------------------------------

    private func handleInitialize(id: Any?) -> [String: Any] {
        Self.result(id: id, result: [
            "protocolVersion": "2024-11-05",
            "serverInfo": ["name": info.name, "version": info.version],
            "capabilities": [
                "tools": ["listChanged": false],
                "resources": ["listChanged": false, "subscribe": false],
            ],
        ])
    }

    private func handleToolsList(id: Any?) -> [String: Any] {
        let list = tools.map { t -> [String: Any] in
            ["name": t.name, "description": t.description, "inputSchema": t.inputSchema]
        }
        return Self.result(id: id, result: ["tools": list])
    }

    private func handleToolsCall(id: Any?, params: Any?) async -> [String: Any] {
        let p = params as? [String: Any]
        guard let toolName = p?["name"] as? String,
              !toolName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return Self.errorObj(id: id, code: -32602, message: "Invalid params: 'name' is required")
        }
        guard let tool = tools.first(where: { $0.name == toolName }) else {
            return Self.errorObj(id: id, code: -32602, message: "Unknown tool: \(toolName)")
        }
        let args = (p?["arguments"] as? [String: Any]) ?? [:]
        do {
            let result = try await tool.execute(arguments: args)
            return Self.toolResult(id: id, data: result)
        } catch let ex as McpToolError {
            return Self.toolError(id: id, message: ex.message)
        } catch {
            return Self.errorObj(id: id, code: -32603, message: "Internal error: \(error)")
        }
    }

    private func handleResourcesList(id: Any?) async -> [String: Any] {
        var all: [McpResource] = []
        for p in resources {
            if let page = try? await p.list() { all.append(contentsOf: page) }
        }
        let list = all.map { r -> [String: Any] in
            ["uri": r.uri, "name": r.name, "description": r.description ?? r.name, "mimeType": r.mimeType]
        }
        return Self.result(id: id, result: ["resources": list])
    }

    private func handleResourcesRead(id: Any?, params: Any?) async -> [String: Any] {
        let p = params as? [String: Any]
        guard let uri = p?["uri"] as? String,
              !uri.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return Self.errorObj(id: id, code: -32602, message: "Invalid params: 'uri' is required")
        }
        guard let provider = resources.first(where: {
            uri.range(of: $0.uriScheme, options: [.caseInsensitive, .anchored]) != nil
        }) else {
            return Self.errorObj(id: id, code: -32602, message: "No provider for URI scheme: \(uri)")
        }
        guard let c = try? await provider.read(uri: uri) else {
            return Self.errorObj(id: id, code: -32602, message: "Resource not found: \(uri)")
        }
        return Self.result(id: id, result: [
            "contents": [["uri": c.uri, "mimeType": c.mimeType, "text": c.text]],
        ])
    }

    // ------------------------------------------------------------------
    // Envelope helpers (mirror the C# static helpers; `id` echoed as a string)
    // ------------------------------------------------------------------

    private static func idString(_ id: Any?) -> Any {
        guard let id = id, !(id is NSNull) else { return NSNull() }
        if let s = id as? String { return "\"\(s)\"" }
        return "\(id)"
    }

    private static func result(id: Any?, result: [String: Any]) -> [String: Any] {
        ["jsonrpc": "2.0", "id": idString(id), "result": result]
    }

    private static func toolResult(id: Any?, data: Any) -> [String: Any] {
        let text = jsonSerialize(data)
        return result(id: id, result: [
            "content": [["type": "text", "text": text]],
            "isError": false,
        ])
    }

    private static func toolError(id: Any?, message: String) -> [String: Any] {
        result(id: id, result: [
            "content": [["type": "text", "text": message]],
            "isError": true,
        ])
    }

    private static func errorObj(id: Any?, code: Int, message: String) -> [String: Any] {
        ["jsonrpc": "2.0", "id": idString(id), "error": ["code": code, "message": message]]
    }

    private static func jsonSerialize(_ value: Any) -> String {
        if JSONSerialization.isValidJSONObject(value),
           let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]),
           let s = String(data: data, encoding: .utf8) {
            return s
        }
        // Scalars: wrap in an array to serialise, then strip the brackets.
        if let data = try? JSONSerialization.data(withJSONObject: [value], options: []),
           let arr = String(data: data, encoding: .utf8), arr.count >= 2 {
            return String(arr.dropFirst().dropLast())
        }
        return "\"\(value)\""
    }
}
