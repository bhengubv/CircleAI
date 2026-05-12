# tools.py
#
# Python port of Circle.AI.Tools public surface.
#
# Covers:
#   ToolParameter  — JSON-schema-style parameter descriptor
#   ToolDefinition — tool the model can call (OpenAI/Qwen compatible)
#   ToolInvocation — a model-requested tool call
#   ToolResult     — the result of executing a tool call
#   IToolBridge    — bridge between the LLM and TheGeekNetwork APIs

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, Optional


# ---------------------------------------------------------------------------
# Data types
# ---------------------------------------------------------------------------

@dataclass
class ToolParameter:
    """A single parameter descriptor compatible with OpenAI function-call schema."""

    type: str           # "string" | "number" | "boolean" | "object" | "array"
    description: str
    enum: Optional[list[str]] = None


@dataclass
class ToolDefinition:
    """Describes a tool the model can call.

    Compatible with OpenAI/Qwen function-call schema.
    """

    name: str
    description: str
    parameters: dict[str, ToolParameter]
    required_parameters: list[str]


@dataclass
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

    # ------------------------------------------------------------------
    # Convenience factories
    # ------------------------------------------------------------------

    @classmethod
    def failure(cls, tool_name: str, error: str) -> "ToolResult":
        """Convenience factory for a failed tool result."""
        return cls(tool_name=tool_name, success=False, error=error)

    @classmethod
    def ok(cls, tool_name: str, result: Any = None) -> "ToolResult":
        """Convenience factory for a successful tool result."""
        return cls(tool_name=tool_name, success=True, result=result)


# ---------------------------------------------------------------------------
# IToolBridge ABC
# ---------------------------------------------------------------------------

class IToolBridge(ABC):
    """Bridge between the local LLM and the TheGeekNetwork APIs.

    Implementations route tool calls to the appropriate API client (HTTP,
    in-process service, etc.).
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
        """Return tools, optionally querying the remote service.

        Implementations that expose a static tool list may return the same
        value as ``available_tools``.  The default implementation does exactly
        that, mirroring the C# default interface method.
        """
        return self.available_tools
