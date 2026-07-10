"""test_threat_detector.py — ThreatDetector degradation weights + indicators.

Mirrors the C# ThreatDetector base-weight * threat-multiplier table and the
detect_indicators pattern rules exactly.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.security import (
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerThreatLevel,
    ThreatDetector,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _event(
    kind: PeerSecurityEventKind,
    level: PeerThreatLevel,
    *,
    node: str = "n1",
    when: datetime | None = None,
) -> PeerSecurityEvent:
    return PeerSecurityEvent(
        node_id=node,
        kind=kind,
        threat_level=level,
        description=f"{kind.name}/{level.name}",
        transport_id="wifi",
        occurred_at=when or _now(),
    )


# ── compute_degradation ───────────────────────────────────────────────────────

BASE_WEIGHTS = {
    PeerSecurityEventKind.AUTH_ATTEMPT: 0.05,
    PeerSecurityEventKind.ROUTING_ANOMALY: 0.10,
    PeerSecurityEventKind.BEHAVIOUR_CHANGE: 0.08,
    PeerSecurityEventKind.ENCRYPTION_EVENT: 0.06,
    PeerSecurityEventKind.INTRUSION_SIGNAL: 0.15,
    PeerSecurityEventKind.PRIVILEGE_ATTEMPT: 0.12,
    PeerSecurityEventKind.CONNECTION_ANOMALY: 0.07,
    PeerSecurityEventKind.DATA_EXFILTRATION: 0.14,
    PeerSecurityEventKind.DENIAL_OF_SERVICE: 0.13,
    PeerSecurityEventKind.UNKNOWN: 0.05,
}
MULTIPLIERS = {
    PeerThreatLevel.NONE: 0.0,
    PeerThreatLevel.LOW: 0.5,
    PeerThreatLevel.MEDIUM: 1.0,
    PeerThreatLevel.HIGH: 2.0,
    PeerThreatLevel.CRITICAL: 3.0,
}


@pytest.mark.parametrize("kind", list(BASE_WEIGHTS))
@pytest.mark.parametrize("level", list(MULTIPLIERS))
def test_compute_degradation_matches_table(kind, level):
    e = _event(kind, level)
    expected = BASE_WEIGHTS[kind] * MULTIPLIERS[level]
    assert abs(ThreatDetector.compute_degradation(e) - expected) <= 1e-12


def test_none_level_is_zero_degradation():
    for kind in BASE_WEIGHTS:
        e = _event(kind, PeerThreatLevel.NONE)
        assert ThreatDetector.compute_degradation(e) == 0.0


# ── detect_indicators ─────────────────────────────────────────────────────────


def test_empty_returns_empty_list():
    assert ThreatDetector.detect_indicators([], timedelta(minutes=5)) == []


def test_events_outside_window_ignored():
    old = _now() - timedelta(minutes=10)
    events = [_event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.LOW, when=old)
              for _ in range(5)]
    assert ThreatDetector.detect_indicators(events, timedelta(minutes=5)) == []


def test_repeated_auth_attempts_needs_three():
    two = [_event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.LOW) for _ in range(2)]
    assert "repeated-auth-attempts" not in ThreatDetector.detect_indicators(
        two, timedelta(minutes=5)
    )
    three = [_event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.LOW) for _ in range(3)]
    assert "repeated-auth-attempts" in ThreatDetector.detect_indicators(
        three, timedelta(minutes=5)
    )


def test_intrusion_and_severity_and_privilege_and_exfil():
    events = [
        _event(PeerSecurityEventKind.INTRUSION_SIGNAL, PeerThreatLevel.CRITICAL),
        _event(PeerSecurityEventKind.PRIVILEGE_ATTEMPT, PeerThreatLevel.MEDIUM),
        _event(PeerSecurityEventKind.DATA_EXFILTRATION, PeerThreatLevel.LOW),
    ]
    ind = ThreatDetector.detect_indicators(events, timedelta(minutes=5))
    assert "intrusion-signal-detected" in ind
    assert "high-severity-event" in ind  # CRITICAL present
    assert "privilege-escalation-attempt" in ind
    assert "data-exfiltration-signal" in ind
    assert "multi-vector-activity" in ind  # 3 distinct kinds


def test_multi_vector_needs_three_distinct_kinds():
    two_kinds = [
        _event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.LOW),
        _event(PeerSecurityEventKind.ROUTING_ANOMALY, PeerThreatLevel.LOW),
    ]
    assert "multi-vector-activity" not in ThreatDetector.detect_indicators(
        two_kinds, timedelta(minutes=5)
    )


def test_indicator_order_is_deterministic():
    events = [
        _event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.HIGH),
        _event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.HIGH),
        _event(PeerSecurityEventKind.AUTH_ATTEMPT, PeerThreatLevel.HIGH),
        _event(PeerSecurityEventKind.INTRUSION_SIGNAL, PeerThreatLevel.HIGH),
        _event(PeerSecurityEventKind.PRIVILEGE_ATTEMPT, PeerThreatLevel.HIGH),
        _event(PeerSecurityEventKind.DATA_EXFILTRATION, PeerThreatLevel.HIGH),
    ]
    ind = ThreatDetector.detect_indicators(events, timedelta(minutes=5))
    # Same evaluation order as the C# method body.
    assert ind == [
        "repeated-auth-attempts",
        "intrusion-signal-detected",
        "high-severity-event",
        "multi-vector-activity",
        "privilege-escalation-attempt",
        "data-exfiltration-signal",
    ]
