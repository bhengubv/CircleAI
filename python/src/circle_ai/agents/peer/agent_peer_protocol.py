# agent_peer_protocol.py
#
# Port of CircleAI.Agents.Peer IAgentPeerProtocol.cs (C# — the EXACT spec).
#
# The contract a Circle AI device implements to talk directly to other Circle AI
# devices over the Aether mesh — no cloud, no relay.
#
# Implementations vary by transport (in-memory mock for tests; real BLE /
# Wi-Fi Direct / Aether router in production). Every method MUST be safe to call
# from any task.
#
# C# Task<T> -> async def; IAsyncEnumerable<AgentMessage> -> AsyncIterator;
# CancellationToken -> an optional token object (asyncio cancellation is the
# primary mechanism, so `ct` is accepted for signature parity but the in-memory
# implementation drives timeouts with asyncio directly).

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator, List, Optional

from .agent_message import AgentMessage
from .peer_agent import AgentCapability, PeerAgent


class IAgentPeerProtocol(ABC):
    """Agent-to-agent protocol over the Aether mesh.

    Mirrors ``CircleAI.Agents.Peer.IAgentPeerProtocol``.
    """

    @abstractmethod
    async def discover_peers_async(
        self, ct: Optional[object] = None
    ) -> List[PeerAgent]:
        """Listen for ``AgentMessageKind.DISCOVER`` broadcasts and any
        already-registered peers for a short discovery window, returning every
        peer observed.
        """
        ...

    @abstractmethod
    async def greet_async(
        self, target_uhid: str, ct: Optional[object] = None
    ) -> Optional[PeerAgent]:
        """Initiate a handshake with ``target_uhid``. Returns the peer's identity
        record on a successful greet, or ``None`` if the peer is unreachable or
        did not respond.
        """
        ...

    @abstractmethod
    async def query_capabilities_async(
        self, target_uhid: str, ct: Optional[object] = None
    ) -> List[AgentCapability]:
        """Query ``target_uhid`` for the capabilities it currently advertises."""
        ...

    @abstractmethod
    async def invoke_async(
        self,
        target_uhid: str,
        capability: AgentCapability,
        request_payload: bytes,
        ct: Optional[object] = None,
    ) -> AgentMessage:
        """Invoke ``capability`` on ``target_uhid`` with ``request_payload``.
        Awaits a single ``AgentMessageKind.RESPONSE`` envelope.

        :raises AgentInvocationException: when the peer returns
            ``AgentMessageKind.DECLINE`` or when invocation otherwise fails.
        """
        ...

    @abstractmethod
    def stream_inbox_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[AgentMessage]:
        """Stream every inbound :class:`AgentMessage` addressed to this agent
        (including broadcasts where :attr:`AgentMessage.to_uhid` is ``"*"``). The
        sequence terminates when the protocol is disposed or the caller stops
        iterating.
        """
        ...
