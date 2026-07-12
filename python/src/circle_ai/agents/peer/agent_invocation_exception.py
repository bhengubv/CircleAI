# agent_invocation_exception.py
#
# Port of CircleAI.Agents.Peer AgentInvocationException.cs (C# — the EXACT
# spec).
#
# Raised by IAgentPeerProtocol.invoke_async when a peer Declines the invocation
# or otherwise fails to return a Response envelope.

from __future__ import annotations

from typing import Optional

from .agent_message import AgentMessage


class AgentInvocationException(Exception):
    """Mirrors ``CircleAI.Agents.Peer.AgentInvocationException``.

    Raised when a peer declines an ``AgentMessageKind.INVOKE`` or returns an
    error response. Carries the offending peer's UHID and, when the peer sent a
    decline envelope, that envelope.

    The C# type exposes four constructor overloads (message; message + peerUhid;
    message + peerUhid + declineMessage; message + innerException). Python has a
    single ``__init__`` with optional parameters covering all four shapes; pass
    ``inner_exception`` to chain via ``raise … from`` semantics.
    """

    def __init__(
        self,
        message: str,
        peer_uhid: Optional[str] = None,
        decline_message: Optional[AgentMessage] = None,
        inner_exception: Optional[BaseException] = None,
    ) -> None:
        super().__init__(message)
        # The peer that declined or errored, if known.
        self.peer_uhid: Optional[str] = peer_uhid
        # The decline envelope returned by the peer, if any.
        self.decline_message: Optional[AgentMessage] = decline_message
        if inner_exception is not None:
            self.__cause__ = inner_exception
