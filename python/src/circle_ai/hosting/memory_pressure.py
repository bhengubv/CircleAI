"""IMemoryPressureSource — port of CircleAI.Hosting.IMemoryPressureSource.

(RT-04) Platform-published memory-pressure signal. Hosting wires the
platform-specific source (Android ``onTrimMemory``, iOS memory warning,
.NET ``MemoryCacheOptions``) into the pipeline; :class:`AIService` listens
and triggers the fallback-chain swap when the level reaches Critical.

Ports:
  * enum ``MemoryPressureLevel`` (stable ordinals),
  * interface ``IMemoryPressureSource``,
  * ``NullMemoryPressureSource`` (always Normal, never raises),
  * ``ManualMemoryPressureSource`` (test/host-driven, thread-safe).
"""
from __future__ import annotations

import asyncio
import threading
from abc import ABC, abstractmethod
from enum import IntEnum
from typing import Awaitable, Callable, List

__all__ = [
    "MemoryPressureLevel",
    "IMemoryPressureSource",
    "NullMemoryPressureSource",
    "ManualMemoryPressureSource",
]

# Handler receives (old_level, new_level) and returns an awaitable.
PressureHandler = Callable[["MemoryPressureLevel", "MemoryPressureLevel"], Awaitable[None]]


class MemoryPressureLevel(IntEnum):
    """Coarse memory-pressure level. Mirrors ``MemoryPressureLevel`` with the
    same ordinals so numeric comparisons match the C#.
    """

    NORMAL = 0
    """Plenty of headroom; no action."""

    TRIM = 1
    """OS asked apps to release optional caches. Drop prefix cache."""

    CRITICAL = 2
    """OS is about to kill the process. Drop everything; consider downshifting."""


class _Subscription:
    """Unsubscribe handle. Mirrors the nested C# ``Subscription`` disposable."""

    __slots__ = ("_owner", "_handler", "_disposed")

    def __init__(self, owner: "ManualMemoryPressureSource", handler: PressureHandler) -> None:
        self._owner = owner
        self._handler = handler
        self._disposed = False

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        with self._owner._gate:  # noqa: SLF001 - mirrors C# lock on owner._gate
            try:
                self._owner._handlers.remove(self._handler)  # noqa: SLF001
            except ValueError:
                pass

    # Context-manager sugar so ``with source.subscribe(...):`` also works.
    def __enter__(self) -> "_Subscription":
        return self

    def __exit__(self, *exc: object) -> None:
        self.dispose()


class IMemoryPressureSource(ABC):
    """(RT-04) A platform-published memory-pressure signal. Mirrors
    ``IMemoryPressureSource``.
    """

    @property
    @abstractmethod
    def current(self) -> MemoryPressureLevel:
        """Current pressure level as last observed."""
        ...

    @abstractmethod
    def subscribe(self, handler: PressureHandler) -> _Subscription:
        """Subscribe to pressure-level transitions. The handler receives
        ``(old_level, new_level)``. Returns an unsubscribe handle.
        """
        ...


class NullMemoryPressureSource(IMemoryPressureSource):
    """Default source that always reports Normal and never raises events.
    Mirrors ``NullMemoryPressureSource``. Use when no platform source is wired
    — brownout simply never fires.
    """

    instance: "NullMemoryPressureSource"

    @property
    def current(self) -> MemoryPressureLevel:
        return MemoryPressureLevel.NORMAL

    def subscribe(self, handler: PressureHandler) -> _Subscription:
        # Empty subscription — no owner list to remove from; dispose is a no-op.
        return _Subscription(_NULL_OWNER, handler)


class ManualMemoryPressureSource(IMemoryPressureSource):
    """Manually-driven source. Hosting layers (or tests) construct one and call
    :meth:`raise_level` when the platform publishes a pressure event.
    Thread-safe. Mirrors ``ManualMemoryPressureSource``.
    """

    __slots__ = ("_gate", "_current", "_handlers")

    def __init__(self) -> None:
        self._gate = threading.RLock()
        self._current = MemoryPressureLevel.NORMAL
        self._handlers: List[PressureHandler] = []

    @property
    def current(self) -> MemoryPressureLevel:
        with self._gate:
            return self._current

    def subscribe(self, handler: PressureHandler) -> _Subscription:
        if handler is None:
            raise ValueError("handler is required")
        with self._gate:
            self._handlers.append(handler)
        return _Subscription(self, handler)

    async def raise_level(self, level: MemoryPressureLevel) -> None:
        """Publish a new pressure level. Idempotent for the same level — only
        transitions fire handlers. Mirrors ``Raise``.
        """
        with self._gate:
            if self._current == level:
                return
            previous = self._current
            self._current = level
            snapshot = list(self._handlers)

        for h in snapshot:
            try:
                await h(previous, level)
            except Exception:  # noqa: BLE001 - error-isolated per C#
                pass


# Sentinel owner used by NullMemoryPressureSource's no-op subscriptions.
class _NullOwner(ManualMemoryPressureSource):
    pass


_NULL_OWNER = _NullOwner()
NullMemoryPressureSource.instance = NullMemoryPressureSource()
