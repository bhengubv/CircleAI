"""Tool catalog — port of CircleAI.Hosting.Tools.

The searchable registry of every tool the host knows about, plus the provider +
executor contracts. Providers register descriptors at startup; the catalog is
searchable by name, tag, and keyword query.

Ports:
  * record ``ToolDescriptor``,
  * record ``ToolExecutionResult``,
  * interfaces ``IToolCatalog``, ``IToolProvider``, ``IToolExecutor``,
  * class ``InMemoryToolCatalog`` (keyword-substring search, thread-safe),
  * ``import_from_async`` (the C# ``ToolCatalogExtensions.ImportFromAsync``).
"""
from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import List, Optional, Sequence

__all__ = [
    "ToolDescriptor",
    "ToolExecutionResult",
    "IToolCatalog",
    "IToolProvider",
    "IToolExecutor",
    "InMemoryToolCatalog",
    "import_from_async",
]


@dataclass(frozen=True, slots=True)
class ToolDescriptor:
    """Describes one tool callable by an LLM (data-only). Mirrors the C#
    ``ToolDescriptor`` record.

    ``tags``/``examples`` default to ``None`` (matching the C# nullable lists).
    """

    name: str
    description: str
    provider: str
    json_schema: str = ""
    auth_scheme: str = "none"
    tags: Optional[Sequence[str]] = None
    examples: Optional[Sequence[str]] = None


@dataclass(frozen=True, slots=True)
class ToolExecutionResult:
    """Result of one tool execution. Mirrors ``ToolExecutionResult``."""

    success: bool
    result: object = None
    error: Optional[str] = None
    duration_ms: int = 0


class IToolCatalog(ABC):
    """The CircleAI tool catalog — searchable by name, tag, and query. Mirrors
    ``IToolCatalog``.
    """

    @property
    @abstractmethod
    def count(self) -> int:
        """How many tools are currently registered."""
        ...

    @abstractmethod
    async def upsert_async(self, descriptor: ToolDescriptor, ct: object = None) -> None:
        """Register or replace one tool. Idempotent for the same name."""
        ...

    @abstractmethod
    async def remove_async(self, name: str, ct: object = None) -> bool:
        """Remove a tool by name. Idempotent; returns whether it existed."""
        ...

    @abstractmethod
    async def get_async(self, name: str, ct: object = None) -> Optional[ToolDescriptor]:
        """Get exactly one descriptor by name, or ``None`` when unknown."""
        ...

    @abstractmethod
    def list(self) -> List[ToolDescriptor]:
        """Enumerate every registered descriptor (stable order)."""
        ...

    @abstractmethod
    def search(self, query: str, top_k: int = 10) -> List[ToolDescriptor]:
        """Free-form keyword-substring search over name + description + tags."""
        ...

    @abstractmethod
    def list_by_provider(self, provider: str) -> List[ToolDescriptor]:
        """Filter by provider id (exact match, case-insensitive)."""
        ...


class IToolProvider(ABC):
    """A source of tools — vendored integrations, MCP server, AetherNet peer.
    Mirrors ``IToolProvider``.
    """

    @property
    @abstractmethod
    def provider_id(self) -> str:
        """Stable provider id, e.g. ``"local"`` / ``"composio"`` / ``"mcp"``."""
        ...

    @abstractmethod
    async def discover_async(self, ct: object = None) -> List[ToolDescriptor]:
        """Discover every tool this provider exposes."""
        ...

    @abstractmethod
    async def is_available_async(self, ct: object = None) -> bool:
        """Cheap availability probe."""
        ...


class IToolExecutor(ABC):
    """Sandboxed execution surface. Mirrors ``IToolExecutor``."""

    @abstractmethod
    async def execute_async(
        self, tool: ToolDescriptor, arguments_json: str, ct: object = None
    ) -> ToolExecutionResult:
        """Execute one tool call. ``arguments_json`` is the model-emitted JSON
        object; the executor validates against ``ToolDescriptor.json_schema``
        before dispatch.
        """
        ...


class InMemoryToolCatalog(IToolCatalog):
    """Default :class:`IToolCatalog` — in-memory + keyword-substring search.
    Thread-safe. Mirrors ``InMemoryToolCatalog``.

    Name matching is case-insensitive (the C# uses
    ``StringComparer.OrdinalIgnoreCase`` for the dictionary), so upserting two
    descriptors whose names differ only in case replaces the earlier one.
    """

    __slots__ = ("_by_name", "_gate")

    def __init__(self) -> None:
        # Map lower-cased name -> descriptor, preserving the original name on
        # the descriptor itself (matches OrdinalIgnoreCase dictionary keys).
        self._by_name: dict[str, ToolDescriptor] = {}
        self._gate = threading.RLock()

    @property
    def count(self) -> int:
        with self._gate:
            return len(self._by_name)

    async def upsert_async(self, descriptor: ToolDescriptor, ct: object = None) -> None:
        if descriptor is None:
            raise ValueError("descriptor is required")
        if descriptor.name is None or not descriptor.name.strip():
            raise ValueError("descriptor.name is required")
        with self._gate:
            self._by_name[descriptor.name.lower()] = descriptor

    async def remove_async(self, name: str, ct: object = None) -> bool:
        if name is None or not name.strip():
            raise ValueError("name is required")
        with self._gate:
            return self._by_name.pop(name.lower(), None) is not None

    async def get_async(self, name: str, ct: object = None) -> Optional[ToolDescriptor]:
        if name is None or not name.strip():
            return None
        with self._gate:
            return self._by_name.get(name.lower())

    def list(self) -> List[ToolDescriptor]:
        with self._gate:
            values = list(self._by_name.values())
        return sorted(values, key=lambda d: d.name.lower())

    def search(self, query: str, top_k: int = 10) -> List[ToolDescriptor]:
        if query is None or not query.strip() or top_k <= 0:
            return []
        terms = [t for t in query.split(" ") if t.strip()]
        terms = [t.strip() for t in terms]

        with self._gate:
            values = list(self._by_name.values())

        scored = []
        for d in values:
            score = _score_match(d, terms)
            if score > 0:
                scored.append((score, d))
        # Order by score desc, then name asc (case-insensitive) — matches C#.
        scored.sort(key=lambda x: (-x[0], x[1].name.lower()))
        return [d for _, d in scored[:top_k]]

    def list_by_provider(self, provider: str) -> List[ToolDescriptor]:
        if provider is None or not provider.strip():
            raise ValueError("provider is required")
        with self._gate:
            values = list(self._by_name.values())
        matches = [d for d in values if d.provider.lower() == provider.lower()]
        return sorted(matches, key=lambda d: d.name.lower())


def _score_match(d: ToolDescriptor, terms: Sequence[str]) -> int:
    name = (d.name or "").lower()
    desc = (d.description or "").lower()
    tag_blob = "" if d.tags is None else " ".join(d.tags).lower()

    score = 0
    for t in terms:
        t_low = t.lower()
        if t_low in name:
            score += 5
        if t_low in desc:
            score += 2
        if t_low in tag_blob:
            score += 3
    return score


async def import_from_async(
    catalog: IToolCatalog, provider: IToolProvider, ct: object = None
) -> int:
    """Discover and import every tool from ``provider`` into ``catalog``.
    Returns how many were imported. Mirrors ``ToolCatalogExtensions.ImportFromAsync``.
    """
    if catalog is None:
        raise ValueError("catalog is required")
    if provider is None:
        raise ValueError("provider is required")
    tools = await provider.discover_async()
    count = 0
    for tool in tools:
        await catalog.upsert_async(tool)
        count += 1
    return count
