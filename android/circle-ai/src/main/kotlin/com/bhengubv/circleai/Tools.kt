package com.bhengubv.circleai

enum class ParameterType { STRING, NUMBER, BOOLEAN, OBJECT, ARRAY }

data class ToolParameter(
    val name: String,
    val description: String,
    val type: ParameterType,
    val required: Boolean
)

data class ToolDefinition(
    val name: String,
    val description: String,
    val parameters: List<ToolParameter>
)

data class ToolInvocation(
    val invocationId: String,
    val toolName: String,
    val argumentsJson: String
)

data class ToolResult(
    val invocationId: String,
    val success: Boolean,
    val resultJson: String? = null,
    val errorMessage: String? = null
)

interface IToolBridge {
    suspend fun invoke(invocation: ToolInvocation): ToolResult
}
