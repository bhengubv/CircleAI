# companion_state_channel.py
#
# Transport seam for the sync engine plus the in-process (loopback) channel.
#
# Implementations:
#   • InProcessCompanionStateChannel — loopback for tests + same-device sim
#   • (Phase 3.1) AetherNetCompanionStateChannel — over the live mesh
#   • Any other transport the host wants (TCP, WebSockets, etc.)
#
# Ported faithfully from CircleAI.Memory.Sync.ICompanionStateChannel and
# CircleAI.Memory.Sync.InProcessCompanionStateChannel (C# — the spec).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from typing import Awaitable, Callable, Dict, List, Optional

from .sync_envelope import SyncEnvelope

# handler(envelope, ct) -> Awaitable[None]
EnvelopeHandler = Callable[[SyncEnvelope, Optional[object]], Awaitable[None]]


class IDisposable(ABC):
    """A resource that can be released. Mirrors C# ``IDisposable`` — the
    subscription returned by :meth:`ICompanionStateChannel.subscribe`.

    Supports use as a context manager (``with channel.subscribe(h): ...``).
    """

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class ICompanionStateChannel(ABC):
    """Transport that moves :class:`SyncEnvelope` messages between peers."""

    @property
    @abstractmethod
    def local_node_id(self) -> str:
        """Stable identifier of THIS node on this channel. Stamped onto every
        envelope as :attr:`SyncEnvelope.from_node_id`.
        """
        ...

    @abstractmethod
    async def send_async(
        self, envelope: SyncEnvelope, *, ct: Optional[object] = None
    ) -> None:
        """Send an envelope to peers. Channel decides whether this is broadcast
        (to every peer) or routed. For v0.1 every channel implements broadcast
        semantics.
        """
        ...

    @abstractmethod
    def subscribe(self, handler: EnvelopeHandler) -> IDisposable:
        """Subscribe to inbound envelopes. The returned disposable
        unsubscribes.
        """
        ...


class InProcessSyncHub:
    """Routes envelopes between every :class:`InProcessCompanionStateChannel`
    that has joined the hub. One hub per simulated "mesh".
    """

    def __init__(self) -> None:
        self._channels: Dict[str, "InProcessCompanionStateChannel"] = {}
        self._lock = threading.Lock()

    def _join(self, channel: "InProcessCompanionStateChannel") -> None:
        with self._lock:
            self._channels[channel.local_node_id] = channel

    def _leave(self, node_id: str) -> None:
        with self._lock:
            self._channels.pop(node_id, None)

    async def _broadcast_async(
        self, envelope: SyncEnvelope, sender_node_id: str, ct: Optional[object]
    ) -> None:
        with self._lock:
            peers = [
                c for c in self._channels.values() if c.local_node_id != sender_node_id
            ]
        for peer in peers:
            await peer._deliver_async(envelope, ct)

    @property
    def connected_node_ids(self) -> List[str]:
        """Channels currently on this hub."""
        with self._lock:
            return list(self._channels.keys())


class InProcessCompanionStateChannel(ICompanionStateChannel):
    """In-process :class:`ICompanionStateChannel`. Broadcasts via an
    :class:`InProcessSyncHub`.
    """

    def __init__(self, hub: InProcessSyncHub, local_node_id: str) -> None:
        if hub is None:
            raise ValueError("hub required")
        if local_node_id is None or local_node_id.strip() == "":
            raise ValueError("local_node_id required")
        self._hub = hub
        self._local_node_id = local_node_id
        self._handlers: List[EnvelopeHandler] = []
        self._lock = threading.Lock()
        self._disposed = False
        self._hub._join(self)

    @property
    def local_node_id(self) -> str:
        return self._local_node_id

    async def send_async(
        self, envelope: SyncEnvelope, *, ct: Optional[object] = None
    ) -> None:
        if envelope is None:
            raise ValueError("envelope required")
        if self._disposed:
            raise RuntimeError("InProcessCompanionStateChannel is disposed")
        await self._hub._broadcast_async(envelope, self._local_node_id, ct)

    def subscribe(self, handler: EnvelopeHandler) -> IDisposable:
        if handler is None:
            raise ValueError("handler required")
        if self._disposed:
            raise RuntimeError("InProcessCompanionStateChannel is disposed")
        with self._lock:
            self._handlers.append(handler)
        return _Subscription(self, handler)

    async def _deliver_async(
        self, envelope: SyncEnvelope, ct: Optional[object]
    ) -> None:
        with self._lock:
            snapshot = list(self._handlers)
        for h in snapshot:
            await h(envelope, ct)

    def dispose(self) -> None:
        """Unregister from the hub."""
        if self._disposed:
            return
        self._disposed = True
        self._hub._leave(self._local_node_id)
        with self._lock:
            self._handlers.clear()

    def __enter__(self) -> "InProcessCompanionStateChannel":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class _Subscription(IDisposable):
    def __init__(
        self, owner: InProcessCompanionStateChannel, handler: EnvelopeHandler
    ) -> None:
        self._owner = owner
        self._handler = handler

    def dispose(self) -> None:
        with self._owner._lock:
            try:
                self._owner._handlers.remove(self._handler)
            except ValueError:
                pass
