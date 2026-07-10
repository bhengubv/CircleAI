"""test_aether_events.py — Aether telemetry event families (Contract 1).

Locks the stable enum ordinals (cross-language wire vocabulary), record
immutability, and the derived-property helpers.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.aether import (
    AetherNetworkEvent,
    AetherNetworkEventKind,
    AetherNodeEvent,
    AetherNodeEventKind,
    AetherNodeHealth,
    AetherRouteEvent,
    AetherRouteEventKind,
    AetherSecurityEvent,
    AetherSecurityEventKind,
    AetherThreatLevel,
    AetherTransportEvent,
    AetherTransportEventKind,
    AetherTransportKind,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def test_node_event_kind_ordinals():
    assert [(e.name, int(e)) for e in AetherNodeEventKind] == [
        ("JOINED", 0),
        ("LEFT", 1),
        ("HEALTH_CHANGED", 2),
    ]


def test_transport_kind_ordinals():
    assert [(e.name, int(e)) for e in AetherTransportKind] == [
        ("WIFI", 0),
        ("BLUETOOTH", 1),
        ("LORA", 2),
        ("NFC", 3),
        ("CELLULAR", 4),
        ("ETHERNET", 5),
        ("UNKNOWN", 6),
    ]


def test_security_kind_ordinals():
    assert [(e.name, int(e)) for e in AetherSecurityEventKind] == [
        ("NODE_AUTH_ATTEMPT", 0),
        ("ROUTING_ANOMALY", 1),
        ("NODE_BEHAVIOUR_CHANGE", 2),
        ("ENCRYPTION_EVENT", 3),
        ("INTRUSION_SIGNAL", 4),
        ("PRIVILEGE_ATTEMPT", 5),
    ]


def test_threat_level_ordinals_and_ordering():
    assert [(e.name, int(e)) for e in AetherThreatLevel] == [
        ("NONE", 0),
        ("LOW", 1),
        ("MEDIUM", 2),
        ("HIGH", 3),
        ("CRITICAL", 4),
    ]
    assert AetherThreatLevel.NONE < AetherThreatLevel.CRITICAL
    assert AetherThreatLevel.HIGH > AetherThreatLevel.MEDIUM


def test_route_and_network_kind_ordinals():
    assert [int(e) for e in AetherRouteEventKind] == [0, 1, 2]
    assert [int(e) for e in AetherNetworkEventKind] == [0, 1, 2]
    assert [int(e) for e in AetherTransportEventKind] == [0, 1, 2, 3]


def test_node_health_is_valid():
    ok = AetherNodeHealth(0.5, True, timedelta(milliseconds=10), 2)
    assert ok.is_valid
    bad = AetherNodeHealth(1.5, True, timedelta(), 1)
    assert not bad.is_valid


def test_node_event_is_exit():
    h = AetherNodeHealth(1.0, True, timedelta(), 1)
    left = AetherNodeEvent("n", AetherNodeEventKind.LEFT, h, _now())
    joined = AetherNodeEvent("n", AetherNodeEventKind.JOINED, h, _now())
    assert left.is_exit
    assert not joined.is_exit


def test_transport_exceeds_loss():
    e = AetherTransportEvent(
        "n", AetherTransportEventKind.PACKET_LOSS, AetherTransportKind.WIFI, None, 0.8, _now()
    )
    assert e.exceeds_loss(0.75)
    assert not e.exceeds_loss(0.9)
    none = AetherTransportEvent(
        "n", AetherTransportEventKind.SELECTED, AetherTransportKind.WIFI, None, None, _now()
    )
    assert not none.exceeds_loss(0.1)


def test_route_hop_count_and_failed():
    e = AetherRouteEvent(
        "a", "c", ["a", "b", "c"], AetherRouteEventKind.FAILED, "timeout", _now()
    )
    assert e.hop_count == 3
    assert e.is_failed


def test_security_event_high_severity():
    hi = AetherSecurityEvent(
        "n", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.HIGH, "x", {}, _now()
    )
    lo = AetherSecurityEvent(
        "n", AetherSecurityEventKind.ROUTING_ANOMALY, AetherThreatLevel.LOW, "x", {}, _now()
    )
    assert hi.is_high_severity
    assert not lo.is_high_severity


def test_network_event_high_congestion():
    e = AetherNetworkEvent(AetherNetworkEventKind.CONGESTION_DETECTED, 5, 3, 0.8, _now())
    assert e.is_high_congestion
    e2 = AetherNetworkEvent(AetherNetworkEventKind.TOPOLOGY_CHANGED, 5, 3, 0.5, _now())
    assert not e2.is_high_congestion


def test_event_records_are_frozen():
    e = AetherNetworkEvent(AetherNetworkEventKind.TOPOLOGY_CHANGED, 1, 1, 0.1, _now())
    with pytest.raises(Exception):
        e.node_count = 2  # type: ignore[misc]
