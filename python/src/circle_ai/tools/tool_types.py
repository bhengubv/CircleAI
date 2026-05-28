from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Optional


@dataclass(frozen=True)
class ToolParameter:
    """A single parameter descriptor compatible with OpenAI function-call schema."""

    type: str           # "string" | "number" | "boolean" | "object" | "array"
    description: str
    enum: Optional[list[str]] = None


@dataclass(frozen=True)
class ToolDefinition:
    """Describes a tool the model can call.

    Compatible with OpenAI/Qwen function-call schema.
    """

    name: str
    description: str
    parameters: dict[str, ToolParameter]
    required_parameters: list[str]


@dataclass(frozen=True)
class ToolInvocation:
    """A tool call requested by the model."""

    tool_name: str
    arguments: dict[str, Any]


@dataclass
class ToolResult:
    """The result of executing a tool invocation."""

    tool_name: str
    success: bool
    result: Any = None
    error: Optional[str] = None

    @staticmethod
    def ok(tool_name: str, result: Any = None) -> "ToolResult":
        """Convenience factory for a successful tool result."""
        return ToolResult(tool_name=tool_name, success=True, result=result)

    @staticmethod
    def failure(tool_name: str, error: str) -> "ToolResult":
        """Convenience factory for a failed tool result."""
        return ToolResult(tool_name=tool_name, success=False, error=error)
