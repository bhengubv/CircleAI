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

// MARK: - FaceExpressionClassification

/// Classified facial expression from the on-device face analysis pipeline.
public enum FaceExpressionClassification: String, Sendable, CaseIterable {
    /// Relaxed, expressionless face.
    case neutral
    /// Smile or laughter.
    case happy
    /// Downturned mouth, furrowed brows.
    case sad
    /// Raised brows, wide eyes.
    case surprised
    /// Furrowed brows, head tilt.
    case confused
    /// Tense jaw, tight eyes, shallow breathing indicators.
    case stressed
    /// Lowered brows, clenched jaw.
    case angry
    /// Classifier could not determine expression with sufficient confidence.
    case unknown
}

// MARK: - FaceBoundingBox

/// Normalised bounding box for a detected face.
/// All coordinates are in the range [0.0, 1.0] relative to the image dimensions.
public struct FaceBoundingBox: Sendable {
    /// Left edge (0 = left of frame).
    public let x: Float
    /// Top edge (0 = top of frame).
    public let y: Float
    /// Width of the box.
    public let width: Float
    /// Height of the box.
    public let height: Float

    public init(x: Float, y: Float, width: Float, height: Float) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

// MARK: - FacialMetricMatrix

/// Output of the on-device face analysis pipeline for a single frame.
/// Contains 68 landmark pairs (136 floats), the bounding box, the classified
/// expression, and a confidence score.
public final class FacialMetricMatrix: @unchecked Sendable {

    /// 68 (x, y) landmark pairs stored as a flat array of 136 normalised floats.
    /// Indices: landmark k → (x=landmarks[k*2], y=landmarks[k*2+1]).
    public let landmarks: [Float]

    /// Normalised bounding box of the detected face.
    public let boundingBox: FaceBoundingBox

    /// Classified expression for this frame.
    public let expression: FaceExpressionClassification

    /// Model confidence for the expression classification, in [0.0, 1.0].
    public let confidenceScore: Float

    /// UTC time when this frame was captured.
    public let capturedAt: Date

    public init(
        landmarks: [Float],
        boundingBox: FaceBoundingBox,
        expression: FaceExpressionClassification,
        confidenceScore: Float,
        capturedAt: Date = Date()
    ) {
        precondition(landmarks.count == 136, "FacialMetricMatrix.landmarks must have exactly 136 elements (68 x/y pairs)")
        self.landmarks = landmarks
        self.boundingBox = boundingBox
        self.expression = expression
        self.confidenceScore = confidenceScore
        self.capturedAt = capturedAt
    }

    /// Returns the (x, y) pair for landmark at index i (0-based, 0–67).
    public func getLandmark(at i: Int) -> (x: Float, y: Float) {
        precondition(i >= 0 && i < 68, "Landmark index must be in 0..<68")
        return (x: landmarks[i * 2], y: landmarks[i * 2 + 1])
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
