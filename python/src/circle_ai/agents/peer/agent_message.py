"""AgentMessage — port of CircleAI.Agents.Peer.AgentMessage.

Signed, content-typed envelope exchanged between two CircleAI agents.
Carries a correlation_id so multi-hop exchanges stitch together in
distributed traces (HTTP wire equivalent: X-Correlation-ID).
"""
from __future__ import annotations

import datetime as _dt
import uuid
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Optional


class AgentMessageKind(IntEnum):
    """Discriminator for the kind of agent-to-agent exchange."""

    DISCOVER = 0
    GREET = 1
    CAPABILITY_QUERY = 2
    INVOKE = 3
    RESPONSE = 4
    DECLINE = 5
    HEARTBEAT = 6


@dataclass(frozen=True)
class AgentMessage:
    """Signed envelope for the agent-to-agent protocol."""

    id: uuid.UUID
    kind: AgentMessageKind
    from_uhid: str
    to_uhid: str
    content_type: str
    payload: bytes
    signature: bytes
    sent_at: _dt.datetime
    correlation_id: Optional[str] = None

    @staticmethod
    def create(
        kind: AgentMessageKind,
        from_uhid: str,
        to_uhid: str,
        content_type: str,
        payload: bytes,
        signature: bytes,
        correlation_id: Optional[str] = None,
    ) -> "AgentMessage":
        """Create a new envelope.

        When `correlation_id` is None, a 32-char UUID4 hex string is generated
        so every outbound envelope carries SOME trace anchor — distributed
        traces always stitch even when the caller forgets.
        """
        return AgentMessage(
            id=uuid.uuid4(),
            kind=kind,
            from_uhid=from_uhid,
            to_uhid=to_uhid,
            content_type=content_type,
            payload=payload,
            signature=signature,
            sent_at=_dt.datetime.now(_dt.timezone.utc),
            correlation_id=correlation_id or uuid.uuid4().hex,
        )
