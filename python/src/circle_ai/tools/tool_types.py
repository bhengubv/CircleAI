from __future__ import annotations

from abc import ABC, abstractmethod
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


class IToolBridge(ABC):
    """Bridge between the local LLM and host/network tool APIs.

    Implementations route tool calls to the appropriate client (HTTP,
    in-process service, etc.). Mirrors ``CircleAI.Tools.IToolBridge``.
    """

    @property
    @abstractmethod
    def available_tools(self) -> list[ToolDefinition]:
        """Synchronous list of tools exposed by this bridge."""
        ...

    @abstractmethod
    async def invoke_async(
        self,
        invocation: ToolInvocation,
        *,
        ct: Optional[object] = None,
    ) -> ToolResult:
        """Execute a tool call and return the result."""
        ...

    async def get_available_tools_async(
        self, *, ct: Optional[object] = None
    ) -> list[ToolDefinition]:
        """Return tools, optionally querying the remote service. Default
        returns :attr:`available_tools` (the C# default-interface-method).
        """
        return self.available_tools
