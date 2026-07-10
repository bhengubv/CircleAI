"""test_peer_security_types.py — transport-agnostic peer security primitives.

Locks the enum ordinals (stable cross-language), record immutability, and the
IPeerSecurityEventFeed adapter contract.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.security import (
    IPeerSecurityEventFeed,
    PeerDirectiveKind,
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerThreatLevel,
)


def test_event_kind_ordinals_stable():
    assert [(e.name, int(e)) for e in PeerSecurityEventKind] == [
        ("AUTH_ATTEMPT", 0),
        ("ROUTING_ANOMALY", 1),
        ("BEHAVIOUR_CHANGE", 2),
        ("ENCRYPTION_EVENT", 3),
        ("INTRUSION_SIGNAL", 4),
        ("PRIVILEGE_ATTEMPT", 5),
        ("CONNECTION_ANOMALY", 6),
        ("DATA_EXFILTRATION", 7),
        ("DENIAL_OF_SERVICE", 8),
        ("UNKNOWN", 9),
    ]


def test_threat_level_ordinals_stable():
    assert [(e.name, int(e)) for e in PeerThreatLevel] == [
        ("NONE", 0),
        ("LOW", 1),
        ("MEDIUM", 2),
        ("HIGH", 3),
        ("CRITICAL", 4),
    ]


def test_directive_kind_ordinals_stable():
    assert [(e.name, int(e)) for e in PeerDirectiveKind] == [
        ("ELEVATE_MONITORING", 0),
        ("AVOID_NODE", 1),
        ("QUARANTINE_NODE", 2),
        ("RELEASE_NODE", 3),
    ]


def test_threat_level_ordering():
    assert PeerThreatLevel.NONE < PeerThreatLevel.CRITICAL
    assert PeerThreatLevel.HIGH > PeerThreatLevel.MEDIUM


def test_peer_security_event_is_frozen():
    e = PeerSecurityEvent(
        "n", PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.LOW,
        "d", "wifi", datetime.now(timezone.utc),
    )
    with pytest.raises(Exception):
        e.node_id = "other"  # type: ignore[misc]


async def test_event_feed_contract():
    """A concrete IPeerSecurityEventFeed pumps events into the supplied handler."""

    class _Feed(IPeerSecurityEventFeed):
        @property
        def transport_id(self) -> str:
            return "test-transport"

        async def start_async(self, handler, ct=None):
            handler(
                PeerSecurityEvent(
                    "n1", PeerSecurityEventKind.INTRUSION_SIGNAL,
                    PeerThreatLevel.HIGH, "e", self.transport_id,
                    datetime.now(timezone.utc),
                )
            )

    feed = _Feed()
    assert feed.transport_id == "test-transport"
    got = []
    await feed.start_async(got.append)
    assert len(got) == 1
    assert got[0].transport_id == "test-transport"
