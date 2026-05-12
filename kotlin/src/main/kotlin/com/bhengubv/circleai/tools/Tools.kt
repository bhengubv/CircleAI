// Tools.kt
//
// Kotlin port of Circle.AI.Tools portable layer.
//
// Covers:
//   ToolDefinition     — describes a tool the model can call (OpenAI/Qwen function-call schema)
//   ToolParameter      — describes one parameter of a tool
//   ToolInvocation     — a model-generated tool call request
//   ToolResult         — outcome of executing a tool call
//   IToolBridge        — bridge between the local LLM and the TheGeekNetwork APIs

package com.bhengubv.circleai.tools

// ---------------------------------------------------------------------------
// ToolParameter
// ---------------------------------------------------------------------------

/**
 * Describes one parameter of a [ToolDefinition].
 * [type] is one of: "string", "number", "boolean", "object", "array".
 */
data class ToolParameter(
    /** JSON Schema primitive type: "string", "number", "boolean", "object", "array". */
    val type: String,
    val description: String,
    /** Optional enumeration of allowed values. */
    val enum: Array<String>? = null
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ToolParameter) return false
        return type == other.type &&
            description == other.description &&
            (enum == null && other.enum == null || enum != null && other.enum != null && enum.contentEquals(other.enum))
    }

    override fun hashCode(): Int {
        var result = type.hashCode()
        result = 31 * result + description.hashCode()
        result = 31 * result + (enum?.contentHashCode() ?: 0)
        return result
    }
}

// ---------------------------------------------------------------------------
// ToolDefinition
// ---------------------------------------------------------------------------

/**
 * Describes a tool the model can call.
 * Compatible with OpenAI/Qwen function-call schema.
 */
data class ToolDefinition(
    val name: String,
    val description: String,
    val parameters: Map<String, ToolParameter>,
    val requiredParameters: List<String>
)

// ---------------------------------------------------------------------------
// ToolInvocation
// ---------------------------------------------------------------------------

/** A model-generated tool call request. */
data class ToolInvocation(
    val toolName: String,
    val arguments: Map<String, Any?>
)

// ---------------------------------------------------------------------------
// ToolResult
// ---------------------------------------------------------------------------

/** Outcome of executing a tool call. */
data class ToolResult(
    val toolName: String,
    val success: Boolean,
    val result: Any? = null,
    val error: String? = null
) {
    companion object {
        /** Convenience factory for a failed tool result. */
        fun failure(toolName: String, error: String): ToolResult =
            ToolResult(toolName = toolName, success = false, error = error)

        /** Convenience factory for a successful tool result. */
        fun ok(toolName: String, result: Any? = null): ToolResult =
            ToolResult(toolName = toolName, success = true, result = result)
    }
}

// ---------------------------------------------------------------------------
// IToolBridge
// ---------------------------------------------------------------------------

/**
 * Bridge between the local LLM and the TheGeekNetwork APIs. Implementations
 * route tool calls to the appropriate API client (HTTP, in-process service, etc.).
 */
interface IToolBridge {
    /** The list of tools available through this bridge. */
    val availableTools: List<ToolDefinition>

    /** Executes the given [invocation] and returns its result. */
    suspend fun invokeAsync(invocation: ToolInvocation): ToolResult

    /**
     * Returns the tools available through this bridge by querying the remote
     * service. Optional — implementations that expose a static tool list may
     * return the same value as [availableTools].
     * The default implementation returns the synchronous [availableTools] list.
     */
    suspend fun getAvailableToolsAsync(): List<ToolDefinition> = availableTools
}
