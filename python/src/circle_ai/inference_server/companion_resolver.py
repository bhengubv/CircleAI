"""Companion session resolver.

Ports ``CircleAI.Inference.Server.Endpoints.ICompanionSessionResolver`` and
``CircleAI.Inference.Server.Hosting.InMemoryCompanionSessionResolver`` — caches
one companion session per (session_id, identity_id) pair and constructs missing
sessions via an ``ICompanionSessionFactory``.

Construction is single-flighted per key: a lock + a per-key future guarantees
the factory runs at most once per tuple even under concurrent resolution, and a
failed construction is dropped from the cache so the next caller retries cleanly
(matching the C# ``Lazy<Task<…>>`` + poison-drop semantics).
"""
from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from typing import Dict, Optional, Tuple

from ..companion.companion_types import InterfaceKind

__all__ = ["ICompanionSessionResolver", "InMemoryCompanionSessionResolver"]


class ICompanionSessionResolver(ABC):
    """Resolves a companion session for (session_id, identity_id). Mirrors
    ``ICompanionSessionResolver``.
    """

    @abstractmethod
    async def resolve_async(
        self, session_id: str, identity_id: str, ct: object = None
    ) -> Optional[object]: ...


class InMemoryCompanionSessionResolver(ICompanionSessionResolver):
    """In-process resolver caching one session per (session_id, identity_id).
    Port of ``InMemoryCompanionSessionResolver``.

    ``factory`` is an ``ICompanionSessionFactory`` (has ``create_async(identity_id,
    interface)``). ``default_interface`` is stamped onto created sessions
    (defaults to ``WEB`` because the HTTP-fronted server is the canonical entry
    point).
    """

    __slots__ = ("_factory", "_default_interface", "_lock", "_futures")

    def __init__(
        self, factory: object, default_interface: InterfaceKind = InterfaceKind.WEB
    ) -> None:
        if factory is None:
            raise ValueError("factory is required")
        self._factory = factory
        self._default_interface = default_interface
        self._lock = asyncio.Lock()
        self._futures: Dict[Tuple[str, str], "asyncio.Task[object]"] = {}

    async def resolve_async(
        self, session_id: str, identity_id: str, ct: object = None
    ) -> Optional[object]:
        if not session_id or not session_id.strip():
            return None
        if not identity_id or not identity_id.strip():
            return None

        key = (session_id, identity_id)
        async with self._lock:
            task = self._futures.get(key)
            if task is None:
                task = asyncio.ensure_future(
                    self._factory.create_async(identity_id, self._default_interface)
                )
                self._futures[key] = task

        try:
            session = await task
            return session
        except Exception:
            # Failed construction must not poison the cache — drop the slot so
            # the next caller retries. Re-check identity before removing.
            async with self._lock:
                if self._futures.get(key) is task:
                    self._futures.pop(key, None)
            raise

    @property
    def cached_session_count(self) -> int:
        """Number of currently cached sessions. Diagnostics only."""
        return len(self._futures)
