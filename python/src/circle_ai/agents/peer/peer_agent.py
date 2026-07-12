# peer_agent.py
#
# Port of CircleAI.Agents.Peer PeerAgent.cs + AgentCapability (C# — the EXACT
# spec).
#
# Identity record for a remote agent reachable over the Aether peer mesh.
# PeerAgent describes WHO another CircleAI is and HOW to reach them; it does not
# own the connection. Connections live on the protocol implementation.
#
# C# `sealed record PeerAgent(Guid Id, ...)` maps to a frozen slotted
# dataclass; Guid -> uuid.UUID; IReadOnlyList<AgentCapability> ->
# Sequence[AgentCapability]; byte[] -> bytes; DateTimeOffset -> datetime;
# `string? CurrentTransportId` -> Optional[str]. The C# `with { LastSeenAt = … }`
# expression maps to :func:`dataclasses.replace` (see
# InMemoryAgentPeerProtocol._with_last_seen).

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional, Sequence
from uuid import UUID


@dataclass(frozen=True, slots=True)
class AgentCapability:
    """Mirrors ``CircleAI.Agents.Peer.AgentCapability`` — ``record(string Name,
    string Version, decimal CostPerInvocation, string CostCurrency)``.

    A capability advertised by a :class:`PeerAgent`.

    :param name: Canonical capability name — e.g. ``"translate"``,
        ``"summarise"``, ``"navigate"``, ``"diagnose"``.
    :param version: Semantic version of the capability contract.
    :param cost_per_invocation: Cost in :attr:`cost_currency`. ``0`` means free.
        C# ``decimal`` maps to :class:`decimal.Decimal` at the call site; a plain
        number is accepted here so free (``0``) capabilities need no import.
    :param cost_currency: Currency code. Defaults to ``"SDPKT"`` within the
        CircleAI ecosystem; other codes are allowed for interoperability with
        external agents.
    """

    name: str
    version: str
    cost_per_invocation: object
    cost_currency: str


@dataclass(frozen=True, slots=True)
class PeerAgent:
    """Mirrors ``CircleAI.Agents.Peer.PeerAgent`` — ``record(Guid Id,
    string UhidIdentityId, string DisplayName,
    IReadOnlyList<AgentCapability> Capabilities, byte[] PublicKeyDer,
    string? CurrentTransportId, DateTimeOffset LastSeenAt)``.

    A peer Circle AI agent discoverable over the Aether mesh.

    :param id: Local handle for this peer (stable per discovery session).
    :param uhid_identity_id: Hashed UHID identity reference — never raw PII. Used
        as the routing key in :attr:`AgentMessage.to_uhid`.
    :param display_name: User-chosen display label (e.g. ``"Sipho's Circle"``).
    :param capabilities: Capabilities this peer advertises.
    :param public_key_der: DER-encoded P-256 public key from the peer's
        UhidKeyRing.
    :param current_transport_id: Transport currently carrying this peer —
        ``"aether"``, ``"wifi-direct"``, ``"ble"``, ``"https-relay"``, or
        ``None`` when the peer is offline.
    :param last_seen_at: UTC timestamp of the last message or heartbeat from this
        peer.
    """

    id: UUID
    uhid_identity_id: str
    display_name: str
    capabilities: Sequence[AgentCapability]
    public_key_der: bytes
    current_transport_id: Optional[str]
    last_seen_at: datetime
