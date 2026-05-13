// Tools.swift
//
// ToolDefinition, ToolParameter, ToolInvocation, ToolResult, IToolBridge.
// Bridge between the local LLM and the TheGeekNetwork APIs.
// Compatible with OpenAI/Qwen function-call schema.

import Foundation

// MARK: - ToolDefinition

/// Describes a tool the model can call.
public struct ToolDefinition: Sendable {
    public var name: String
    public var description: String
    /// Parameter name → parameter descriptor.
    public var parameters: [String: ToolParameter]
    /// Names of parameters that are required.
    public var requiredParameters: [String]

    public init(
        name: String,
        description: String,
        parameters: [String: ToolParameter],
        requiredParameters: [String]
    ) {
        self.name = name
        self.description = description
        self.parameters = parameters
        self.requiredParameters = requiredParameters
    }
}

// MARK: - ToolParameter

/// Describes a single parameter of a ToolDefinition.
public struct ToolParameter: Sendable {
    /// JSON schema type: "string", "number", "boolean", "object", "array".
    public var type: String
    public var description: String
    /// Optional list of allowed enum values.
    public var enumValues: [String]?

    public init(type: String, description: String, enumValues: [String]? = nil) {
        self.type = type
        self.description = description
        self.enumValues = enumValues
    }
}

// MARK: - ToolInvocation

/// A tool call emitted by the model.
public struct ToolInvocation: @unchecked Sendable {
    public var toolName: String
    /// Argument values keyed by parameter name. Values are JSON-compatible types.
    public var arguments: [String: Any?]

    public init(toolName: String, arguments: [String: Any?]) {
        self.toolName = toolName
        self.arguments = arguments
    }
}

// MARK: - ToolResult

/// The result of executing a ToolInvocation.
public struct ToolResult: @unchecked Sendable {
    public var toolName: String
    public var success: Bool
    /// The return value (any JSON-compatible type). nil on failure.
    public var result: (any Sendable)?
    /// Error message when success == false.
    public var error: String?

    public init(
        toolName: String,
        success: Bool,
        result: (any Sendable)? = nil,
        error: String? = nil
    ) {
        self.toolName = toolName
        self.success = success
        self.result = result
        self.error = error
    }

    /// Convenience factory for a failed tool result.
    public static func failure(toolName: String, error: String) -> ToolResult {
        ToolResult(toolName: toolName, success: false, error: error)
    }

    /// Convenience factory for a successful tool result.
    public static func ok(toolName: String, result: (any Sendable)? = nil) -> ToolResult {
        ToolResult(toolName: toolName, success: true, result: result)
    }
}

// MARK: - IToolBridge

/// Bridge between the local LLM and the TheGeekNetwork APIs.
/// Implementations route tool calls to the appropriate API client.
public protocol IToolBridge: AnyObject {
    /// The tools currently available through this bridge.
    var availableTools: [ToolDefinition] { get }

    /// Executes a tool invocation and returns the result.
    func invoke(_ invocation: ToolInvocation) async throws -> ToolResult

    /// Returns the tools available through this bridge, potentially querying
    /// a remote service. Falls back to availableTools for static bridges.
    func getAvailableTools() async throws -> [ToolDefinition]
}
