"""MCP contracts — port of CircleAI.Hosting.Mcp.Contracts.

MCP tool + resource provider contracts. Hosts implement :class:`IMcpTool` for
each tool they expose; the dispatcher routes ``tools/call`` by name.
:class:`IMcpResourceProvider` handles ``resources/list`` and ``resources/read``.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any, List, Optional

__all__ = [
    "IMcpTool",
    "IMcpResourceProvider",
    "McpResource",
    "McpResourceContent",
    "McpToolException",
]


class McpToolException(Exception):
    """(3.2.0) Thrown from inside :meth:`IMcpTool.execute_async` to signal a
    tool-level error (vs an MCP protocol error). The dispatcher returns this as
    ``{content:[{type:"text",text:msg}], isError:true}``. Mirrors ``McpToolException``.
    """


class IMcpTool(ABC):
    """(3.2.0) One MCP tool the host exposes. Mirrors ``IMcpTool``."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Unique tool name (snake_case by convention)."""
        ...

    @property
    @abstractmethod
    def description(self) -> str:
        """One-line description shown in tool listings."""
        ...

    @property
    @abstractmethod
    def input_schema(self) -> Any:
        """JSON Schema describing the tool's ``arguments`` object. Included
        verbatim in ``tools/list`` (any JSON-serialisable value).
        """
        ...

    @abstractmethod
    async def execute_async(self, arguments: dict, ct: object = None) -> Any:
        """Execute the tool. Return any JSON-serialisable value; the dispatcher
        wraps it in MCP's text-content envelope. Raise :class:`McpToolException`
        to signal a tool-level error.
        """
        ...


class IMcpResourceProvider(ABC):
    """(3.2.0) One MCP resource provider. Mirrors ``IMcpResourceProvider``."""

    @property
    @abstractmethod
    def uri_scheme(self) -> str:
        """e.g. ``"vault://"``, ``"models://"``."""
        ...

    @abstractmethod
    async def list_async(self, ct: object = None) -> List["McpResource"]:
        """List every resource this provider serves."""
        ...

    @abstractmethod
    async def read_async(self, uri: str, ct: object = None) -> Optional["McpResourceContent"]:
        """Read one resource by uri. Returns ``None`` on not-found."""
        ...


@dataclass(frozen=True, slots=True)
class McpResource:
    """(3.2.0) One MCP resource descriptor. Mirrors ``McpResource``."""

    uri: str
    name: str
    description: Optional[str]
    mime_type: str


@dataclass(frozen=True, slots=True)
class McpResourceContent:
    """(3.2.0) One MCP resource content (returned by resources/read). Mirrors
    ``McpResourceContent``.
    """

    uri: str
    mime_type: str
    text: str
