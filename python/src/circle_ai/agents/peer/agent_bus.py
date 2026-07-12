# agent_bus.py
#
# Port of CircleAI.Agents.Peer AgentBus.cs (C# — the EXACT spec).
#
# In-process coordinator that lets several InMemoryAgentPeerProtocol instances
# behave like devices on a mesh, for tests and samples.
#
# AgentBus owns the peer registry and an unbounded queue per registered peer.
# `send` routes a message to the right queue (or fans out on broadcast).
# `receive` yields envelopes as they arrive.
#
# AgentBus is NOT a production transport. It exists so the protocol contract can
# be exercised without a real Aether router on the wire.
#
# Porting notes:
#   * C# ConcurrentDictionary -> plain dict guarded by a threading.Lock. The bus
#     is touched from protocol pump tasks and caller tasks; the lock keeps the
#     registry/inbox maps consistent. Queue writes themselves are lock-free
#     (asyncio.Queue.put_nowait on an unbounded queue never blocks), matching
#     `Writer.TryWrite`.
#   * C# Channel<AgentMessage> (unbounded) -> asyncio.Queue(). A queue is
#     "completed" (mirrors `Writer.TryComplete`) by pushing a private sentinel;
#     `receive` stops when it reads the sentinel.
#   * `Send` to an unknown UHID is dropped silently — the simulated peer is
#     considered offline (identical to the C#).

from __future__ import annotations

import asyncio
import threading
from typing import AsyncIterator, Dict, List, Optional, Tuple

from .agent_message import AgentMessage
from .peer_agent import PeerAgent

# Broadcast recipient token — an envelope with this ToUhid fans out to every
# registered inbox except the sender's own.
_BROADCAST = "*"

# Private completion sentinel pushed onto an inbox queue to terminate any active
# `receive` enumerator (mirrors Channel Writer.TryComplete()).
_COMPLETE = object()


class AgentBus:
    """In-process bus used to simulate a mesh of CircleAI peers for tests and
    samples. Not a production transport. Mirrors ``CircleAI.Agents.Peer.AgentBus``.
    """

    def __init__(self) -> None:
        self._peers: Dict[str, PeerAgent] = {}
        self._inboxes: Dict[str, "asyncio.Queue[object]"] = {}
        self._lock = threading.Lock()

    @property
    def registered_peers(self) -> List[PeerAgent]:
        """Snapshot of every peer currently registered on the bus."""
        with self._lock:
            return list(self._peers.values())

    def register(self, peer: PeerAgent) -> None:
        """Register ``peer`` on the bus. A subsequent :meth:`send` targeted at
        the peer's UHID will deliver to its inbox. Re-registering with the same
        UHID replaces the prior record.
        """
        if peer is None:
            raise ValueError("peer must not be None")
        with self._lock:
            self._peers[peer.uhid_identity_id] = peer
            self._inboxes.setdefault(peer.uhid_identity_id, asyncio.Queue())

    def unregister(self, uhid: str) -> None:
        """Remove ``uhid`` from the bus and complete its inbox so any active
        :meth:`receive` enumerator terminates cleanly.
        """
        if uhid is None or uhid.strip() == "":
            raise ValueError("uhid must be non-empty")
        with self._lock:
            self._peers.pop(uhid, None)
            queue = self._inboxes.pop(uhid, None)
        if queue is not None:
            queue.put_nowait(_COMPLETE)

    def try_get_peer(self, uhid: str) -> Tuple[bool, Optional[PeerAgent]]:
        """Try to read the latest record for ``uhid``.

        Returns ``(True, peer)`` when found, ``(False, None)`` otherwise —
        Python's stand-in for the C# ``bool TryGetPeer(uhid, out peer)``.
        """
        if uhid is None or uhid.strip() == "":
            raise ValueError("uhid must be non-empty")
        with self._lock:
            found = self._peers.get(uhid)
        return (True, found) if found is not None else (False, None)

    def send(self, message: AgentMessage) -> None:
        """Route ``message`` to its recipient(s).

        When :attr:`AgentMessage.to_uhid` is ``"*"`` the envelope is delivered to
        every registered inbox except the sender's own. Messages for an unknown
        UHID are dropped silently — the simulated peer is considered offline.
        """
        if message is None:
            raise ValueError("message must not be None")

        if message.to_uhid == _BROADCAST:
            with self._lock:
                targets = [
                    q
                    for uhid, q in self._inboxes.items()
                    if uhid != message.from_uhid
                ]
            for queue in targets:
                queue.put_nowait(message)
            return

        with self._lock:
            inbox = self._inboxes.get(message.to_uhid)
        if inbox is not None:
            inbox.put_nowait(message)

    async def receive(
        self, uhid: str, ct: Optional[object] = None
    ) -> AsyncIterator[AgentMessage]:
        """Stream every envelope delivered to ``uhid``'s inbox.

        The sequence terminates when the inbox is completed (via
        :meth:`unregister`) or when the consuming task is cancelled.
        """
        if uhid is None or uhid.strip() == "":
            raise ValueError("uhid must be non-empty")

        with self._lock:
            inbox = self._inboxes.setdefault(uhid, asyncio.Queue())

        while True:
            item = await inbox.get()
            if item is _COMPLETE:
                return
            # Only AgentMessage values are enqueued besides the sentinel.
            yield item  # type: ignore[misc]
