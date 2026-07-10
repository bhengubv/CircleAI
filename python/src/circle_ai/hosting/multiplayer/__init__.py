"""circle_ai.hosting.multiplayer — port of CircleAI.Hosting.Multiplayer.

Live-collaboration hub (per-document groups, LWW-by-rev edits, cursors,
presence) over an injected broadcaster, plus peer-identity contracts.
"""
from __future__ import annotations

from .contracts import GuestPeerIdentity, IMultiplayerPeerIdentity, PeerState
from .hub import MultiplayerHub, colour_for

__all__ = [
    "IMultiplayerPeerIdentity",
    "GuestPeerIdentity",
    "PeerState",
    "MultiplayerHub",
    "colour_for",
]
