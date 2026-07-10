# telemetry.py
#
# Port of CircleAI.Aether.IAetherTelemetry.cs (C# — the EXACT spec).
#
# Contract 1 — Telemetry.
#
# Aether publishes. BhenguAI subscribes. Aether never calls into BhenguAI.
# External Aether adopters can implement IAetherTelemetry without pulling in any
# AI dependency.
#
# Ships:
#   IAetherTelemetryObserver — the sink an AI component implements
#   IAetherTelemetry         — the outward-facing subscription surface
#   IDisposable              — subscription handle (mirrors C# IDisposable)
#   NullAetherTelemetry      — no-op bus (faithful port; tests + Aether-absent)
#   InMemoryAetherTelemetry  — a working fan-out bus (deterministic, thread-safe)

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from typing import List

from .events import (
    AetherNetworkEvent,
    AetherNodeEvent,
    AetherRouteEvent,
    AetherSecurityEvent,
    AetherTransportEvent,
)


class IDisposable(ABC):
    """A resource that can be released. Mirrors C# ``IDisposable`` — the handle
    returned by :meth:`IAetherTelemetry.subscribe`.

    Supports use as a context manager (``with telemetry.subscribe(o): ...``).
    """

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class IAetherTelemetryObserver(ABC):
    """Receives events emitted by Aether. Implement this to react to mesh
    activity — nodes, transports, routes, security signals, and topology.
    """

    @abstractmethod
    def on_node_event(self, e: AetherNodeEvent) -> None:
        ...

    @abstractmethod
    def on_transport_event(self, e: AetherTransportEvent) -> None:
        ...

    @abstractmethod
    def on_route_event(self, e: AetherRouteEvent) -> None:
        ...

    @abstractmethod
    def on_security_event(self, e: AetherSecurityEvent) -> None:
        ...

    @abstractmethod
    def on_network_event(self, e: AetherNetworkEvent) -> None:
        ...


class IAetherTelemetry(ABC):
    """The outward-facing telemetry surface of Aether. The AI Security Layer and
    any other BhenguAI component subscribes here. Aether owns this interface and
    publishes; consumers subscribe and dispose.
    """

    @abstractmethod
    def subscribe(self, observer: IAetherTelemetryObserver) -> IDisposable:
        """Subscribe to all Aether telemetry events. Dispose the returned handle
        to unsubscribe.
        """
        ...


class _NullDisposable(IDisposable):
    def dispose(self) -> None:
        pass


class NullAetherTelemetry(IAetherTelemetry):
    """No-op telemetry — useful for unit tests and environments where Aether is
    absent. :meth:`subscribe` returns a no-op disposable; no events are emitted.
    """

    #: Shared singleton instance, mirroring C# ``NullAetherTelemetry.Instance``.
    instance: "NullAetherTelemetry"

    def subscribe(self, observer: IAetherTelemetryObserver) -> IDisposable:
        if observer is None:
            raise ValueError("observer must not be None")
        return _NULL_DISPOSABLE


_NULL_DISPOSABLE = _NullDisposable()
NullAetherTelemetry.instance = NullAetherTelemetry()


class InMemoryAetherTelemetry(IAetherTelemetry):
    """A working in-memory telemetry bus. Fans every published event out to all
    current subscribers. Concurrent subscribe, dispose, and publish are all
    thread-safe: a snapshot of the observer list is taken under the lock, and
    callbacks fire OUTSIDE it so a consumer that re-enters the bus (subscribe /
    dispose) cannot self-deadlock.

    This is the concrete publisher an Aether runtime (or a test harness) uses to
    drive :class:`IAetherTelemetry` consumers such as the AI Security Layer.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._observers: List[IAetherTelemetryObserver] = []

    def subscribe(self, observer: IAetherTelemetryObserver) -> IDisposable:
        if observer is None:
            raise ValueError("observer must not be None")
        with self._lock:
            self._observers.append(observer)
        return _Subscription(self, observer)

    @property
    def subscriber_count(self) -> int:
        """Number of currently active subscribers. Useful in tests."""
        with self._lock:
            return len(self._observers)

    # ── Publish surface (Aether-side) ─────────────────────────────────────────

    def publish_node_event(self, e: AetherNodeEvent) -> None:
        for o in self._snapshot():
            o.on_node_event(e)

    def publish_transport_event(self, e: AetherTransportEvent) -> None:
        for o in self._snapshot():
            o.on_transport_event(e)

    def publish_route_event(self, e: AetherRouteEvent) -> None:
        for o in self._snapshot():
            o.on_route_event(e)

    def publish_security_event(self, e: AetherSecurityEvent) -> None:
        for o in self._snapshot():
            o.on_security_event(e)

    def publish_network_event(self, e: AetherNetworkEvent) -> None:
        for o in self._snapshot():
            o.on_network_event(e)

    # ── Private ──────────────────────────────────────────────────────────────

    def _snapshot(self) -> List[IAetherTelemetryObserver]:
        with self._lock:
            return list(self._observers)

    def _unsubscribe(self, observer: IAetherTelemetryObserver) -> None:
        with self._lock:
            try:
                self._observers.remove(observer)
            except ValueError:
                pass


class _Subscription(IDisposable):
    def __init__(
        self, owner: InMemoryAetherTelemetry, observer: IAetherTelemetryObserver
    ) -> None:
        self._owner = owner
        self._observer = observer
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._owner._unsubscribe(self._observer)
