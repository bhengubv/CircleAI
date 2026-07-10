"""MultiplayerHub — port of CircleAI.Hosting.Multiplayer.MultiplayerHub.

The C# is a SignalR ``Hub`` for live collaboration: per-document group, LWW-by-
rev edits, live cursors, presence. This port keeps the identical semantics but
routes outgoing peer events through an injected broadcaster (the "inject the
transport, keep it in-memory" contract) instead of SignalR's
``Clients.OthersInGroup(...).SendAsync(...)``. SignalR's per-connection context
(``Context.ConnectionId``) is passed explicitly to each hub method.

State that the C# holds in static ``ConcurrentDictionary``s (per-doc rev, peer
registry) is held here as instance state so tests are isolated; a module-level
:meth:`MultiplayerHub.reset_state_for_testing` mirrors the C# static reset.

Outgoing events (name + args) match the C# exactly:
  * ``PeerJoined(doc_id, connection_id, display_name, color)``
  * ``PeerLeft(doc_id, connection_id, display_name)``
  * ``CursorChanged(connection_id, display_name, color, line, ch)``
  * ``EditApplied(doc_id, content, rev, from_connection_id)``

The cursor-colour hash reproduces the C# ``unchecked`` 32-bit ``h = h*31 + c``.
"""
from __future__ import annotations

import datetime as _dt
import threading
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional

from .contracts import IMultiplayerPeerIdentity, PeerState

__all__ = ["MultiplayerHub", "Broadcast"]

_UTC = _dt.timezone.utc
_UINT32 = 0xFFFFFFFF
_INT32_MIN = -(2**31)
_INT32_MAX = 2**31 - 1

# Broadcaster: (group, event_name, args) → awaitable-or-None. Delivers an event
# to "others in the group" (the caller's own connection is excluded by the hub).
Broadcast = Callable[[str, str, tuple], object]


@dataclass(frozen=True, slots=True)
class _DocRevState:
    rev: int
    updated_at: _dt.datetime


class MultiplayerHub:
    """(3.2.0) Multiplayer collaboration hub. Mirrors ``MultiplayerHub`` over an
    injected :data:`Broadcast`.

    Usage: construct with a peer-identity resolver + a broadcaster, then drive
    :meth:`on_connected_async` / :meth:`join_document` / :meth:`send_edit` etc.
    with the SignalR connection id.
    """

    def __init__(
        self,
        peer_identity: IMultiplayerPeerIdentity,
        broadcast: Broadcast,
    ) -> None:
        if peer_identity is None:
            raise ValueError("peer_identity is required")
        if broadcast is None:
            raise ValueError("broadcast is required")
        self._peer_identity = peer_identity
        self._broadcast = broadcast

    # Static per-doc / per-connection state (instance-level for test isolation;
    # class-level dicts back the static C# accessors below).
    _rev_by_doc: Dict[str, _DocRevState] = {}
    _peer_by_conn: Dict[str, PeerState] = {}
    _gate = threading.RLock()

    # ── Connection lifecycle ───────────────────────────────────────────────

    async def on_connected_async(self, connection_id: str) -> None:
        state = PeerState(
            connection_id=connection_id,
            display_name=self._peer_identity.display_name,
            color=colour_for(self._peer_identity.peer_id),
            doc_id=None,
        )
        with MultiplayerHub._gate:
            MultiplayerHub._peer_by_conn[connection_id] = state

    async def on_disconnected_async(self, connection_id: str) -> None:
        with MultiplayerHub._gate:
            peer = MultiplayerHub._peer_by_conn.pop(connection_id, None)
        if peer is not None and peer.doc_id:
            await self._emit(
                _doc_group(peer.doc_id),
                "PeerLeft",
                (peer.doc_id, peer.connection_id, peer.display_name),
            )

    # ── Document membership ─────────────────────────────────────────────────

    async def join_document(self, connection_id: str, doc_id: str) -> None:
        if doc_id is None or not doc_id.strip():
            return
        with MultiplayerHub._gate:
            peer = MultiplayerHub._peer_by_conn.get(connection_id)
            if peer is not None:
                peer = PeerState(peer.connection_id, peer.display_name, peer.color, doc_id)
                MultiplayerHub._peer_by_conn[connection_id] = peer
        if peer is not None:
            await self._emit(
                _doc_group(doc_id),
                "PeerJoined",
                (doc_id, peer.connection_id, peer.display_name, peer.color),
            )

    async def leave_document(self, connection_id: str, doc_id: str) -> None:
        if doc_id is None or not doc_id.strip():
            return
        with MultiplayerHub._gate:
            peer = MultiplayerHub._peer_by_conn.get(connection_id)
            if peer is not None:
                peer = PeerState(peer.connection_id, peer.display_name, peer.color, None)
                MultiplayerHub._peer_by_conn[connection_id] = peer
        if peer is not None:
            await self._emit(
                _doc_group(doc_id),
                "PeerLeft",
                (doc_id, peer.connection_id, peer.display_name),
            )

    # ── Cursors ─────────────────────────────────────────────────────────────

    async def send_cursor(self, connection_id: str, doc_id: str, line: int, ch: int) -> None:
        with MultiplayerHub._gate:
            peer = MultiplayerHub._peer_by_conn.get(connection_id)
        if peer is None:
            return
        await self._emit(
            _doc_group(doc_id),
            "CursorChanged",
            (peer.connection_id, peer.display_name, peer.color, line, ch),
        )

    # ── Edits (LWW by rev) ──────────────────────────────────────────────────

    async def send_edit(self, connection_id: str, doc_id: str, content: str, rev: int) -> int:
        """Apply an edit if its rev is greater than the server's current rev.
        Returns the new rev (or the server's current rev if the client's rev was
        stale). Mirrors ``SendEdit`` (LWW via ``AddOrUpdate``).
        """
        with MultiplayerHub._gate:
            prev = MultiplayerHub._rev_by_doc.get(doc_id)
            if prev is None:
                new_rev = _DocRevState(max(rev, 1), _dt.datetime.now(_UTC))
            elif rev <= prev.rev:
                new_rev = prev
            else:
                new_rev = _DocRevState(rev, _dt.datetime.now(_UTC))
            MultiplayerHub._rev_by_doc[doc_id] = new_rev

        if new_rev.rev != rev:
            # Rejected — client gets current rev back and can rebase.
            return new_rev.rev

        await self._emit(
            _doc_group(doc_id),
            "EditApplied",
            (doc_id, content, rev, connection_id),
        )
        return rev

    # ── Static-style accessors (mirror the C# statics) ─────────────────────

    @staticmethod
    def peers(doc_id: str) -> List[PeerState]:
        """Snapshot of who is currently in a document. Mirrors ``Peers``."""
        with MultiplayerHub._gate:
            return [p for p in MultiplayerHub._peer_by_conn.values() if p.doc_id == doc_id]

    @staticmethod
    def current_rev(doc_id: str) -> int:
        """Current server-known rev for a document (0 if never touched). Mirrors
        ``CurrentRev``.
        """
        with MultiplayerHub._gate:
            state = MultiplayerHub._rev_by_doc.get(doc_id)
            return state.rev if state is not None else 0

    @staticmethod
    def reset_state_for_testing() -> None:
        """Wipe static state. Mirrors ``ResetStateForTesting``."""
        with MultiplayerHub._gate:
            MultiplayerHub._rev_by_doc.clear()
            MultiplayerHub._peer_by_conn.clear()

    # ── Helpers ─────────────────────────────────────────────────────────────

    async def _emit(self, group: str, event: str, args: tuple) -> None:
        r = self._broadcast(group, event, args)
        if hasattr(r, "__await__"):
            await r


def _doc_group(doc_id: str) -> str:
    return f"doc:{doc_id}"


def colour_for(peer_id: str) -> str:
    """Stable hash → HSL hue so each peer lands on a distinct cursor colour.
    Reproduces the C# ``unchecked`` 32-bit ``h = h*31 + c`` then
    ``((h % 360) + 360) % 360``. Mirrors ``ColourFor``.
    """
    if peer_id is None or peer_id == "":
        return "#5a4fcf"
    h = 0
    for c in peer_id:
        h = _to_int32((h * 31 + ord(c)) & _UINT32)
    hue = ((h % 360) + 360) % 360
    return f"hsl({hue}, 70%, 55%)"


def _to_int32(v: int) -> int:
    """Wrap an unsigned 32-bit value into signed int32 (C# ``unchecked int``)."""
    v &= _UINT32
    return v - (1 << 32) if v > _INT32_MAX else v
