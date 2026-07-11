# helpers.py
#
# Port of CircleAI.MicroAgents MicroAgentHelpers.cs (C# — the EXACT spec).
#
# (3.3.0) Capability search + invoke-history helpers around the host.
# MicroAgentSearch is a static-method utility (module-level functions here);
# MicroAgentInvocationLog is an in-memory append-only log.

from __future__ import annotations

import threading
from dataclasses import dataclass
from datetime import datetime
from typing import Iterable, List

from .contracts import MicroAgentDescriptor


@dataclass(frozen=True, slots=True)
class MicroAgentInvocation:
    """Mirrors ``CircleAI.MicroAgents.MicroAgentInvocation`` — ``record(string
    AgentId, string Input, string ResponseText, DateTimeOffset AtUtc)``."""

    agent_id: str
    input: str
    response_text: str
    at_utc: datetime


class MicroAgentSearch:
    """(3.3.0) Capability filter + free-text search over descriptors. Static
    utility — mirrors the C# ``static class MicroAgentSearch``."""

    @staticmethod
    def by_capability(all: Iterable[MicroAgentDescriptor], capability: str) -> List[MicroAgentDescriptor]:
        if all is None:
            raise ValueError("all must not be None")
        if capability is None or capability.strip() == "":
            raise ValueError("capability required")
        cf = capability.casefold()
        matches = [
            d for d in all if any(c.casefold() == cf for c in d.capabilities)
        ]
        return sorted(matches, key=lambda d: d.agent_id)

    @staticmethod
    def search(all: Iterable[MicroAgentDescriptor], query: str, top_k: int = 10) -> List[MicroAgentDescriptor]:
        if all is None:
            raise ValueError("all must not be None")
        if query is None:
            raise ValueError("query must not be None")
        if top_k <= 0:
            raise ValueError("topK must be positive")
        q = query.casefold()
        results: List[MicroAgentDescriptor] = []
        for d in all:
            if (
                q in d.agent_id.casefold()
                or q in d.description.casefold()
                or any(q in c.casefold() for c in d.capabilities)
            ):
                results.append(d)
        return results[:top_k]


class MicroAgentInvocationLog:
    """(3.3.0) Keep an in-memory invocation log."""

    def __init__(self) -> None:
        self._items: List[MicroAgentInvocation] = []
        self._lock = threading.Lock()

    def append(self, i: MicroAgentInvocation) -> None:
        if i is None:
            raise ValueError("i must not be None")
        with self._lock:
            self._items.append(i)

    def for_agent(self, agent_id: str, limit: int = 50) -> List[MicroAgentInvocation]:
        if limit <= 0:
            raise ValueError("limit must be positive")
        with self._lock:
            matches = [i for i in self._items if i.agent_id == agent_id]
        ordered = sorted(matches, key=lambda i: i.at_utc, reverse=True)
        return ordered[:limit]

    @property
    def total_invocations(self) -> int:
        with self._lock:
            return len(self._items)
