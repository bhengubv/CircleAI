"""Multiplayer contracts — port of CircleAI.Hosting.Multiplayer.Contracts.

Peer-identity surface + peer-state record used by the multiplayer hub. Hosts
implement :class:`IMultiplayerPeerIdentity` to plug in whatever auth they have;
:class:`GuestPeerIdentity` is the anonymous default.
"""
from __future__ import annotations

import uuid
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Optional

__all__ = ["IMultiplayerPeerIdentity", "GuestPeerIdentity", "PeerState"]


class IMultiplayerPeerIdentity(ABC):
    """(3.2.0) Resolves the human-visible identity of the peer making a hub
    call. Mirrors ``IMultiplayerPeerIdentity``.
    """

    @property
    @abstractmethod
    def peer_id(self) -> str:
        """Stable id (used to derive a colour)."""
        ...

    @property
    @abstractmethod
    def display_name(self) -> str:
        """Human-readable display name."""
        ...


class GuestPeerIdentity(IMultiplayerPeerIdentity):
    """(3.2.0) Anonymous guest identity. Mirrors ``GuestPeerIdentity``.

    A ``None`` peer_id becomes a fresh 32-char hex GUID; a ``None`` display name
    becomes ``"Guest"`` — matching the C# defaults.
    """

    __slots__ = ("_peer_id", "_display_name")

    def __init__(self, peer_id: Optional[str] = None, display_name: Optional[str] = None) -> None:
        self._peer_id = peer_id if peer_id is not None else uuid.uuid4().hex
        self._display_name = display_name if display_name is not None else "Guest"

    @property
    def peer_id(self) -> str:
        return self._peer_id

    @property
    def display_name(self) -> str:
        return self._display_name


@dataclass(frozen=True, slots=True)
class PeerState:
    """(3.2.0) Snapshot of one connected peer. Mirrors the nested C#
    ``MultiplayerHub.PeerState`` record. Immutable — the hub derives modified
    copies when a peer joins/leaves a document.
    """

    connection_id: str
    display_name: str
    color: str
    doc_id: Optional[str]
