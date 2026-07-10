# threat_detector.py
#
# Port of CircleAI.Security.ThreatDetector (C# — the EXACT spec).
#
# Pure static threat logic — no state, no DI, fully testable in isolation.
#
# Two responsibilities:
#   1. compute_degradation: how much trust a single security event should cost.
#   2. detect_indicators:   which behavioural patterns are visible in a window.
#
# Transport-agnostic: operates on PeerSecurityEvent / PeerSecurityEventKind /
# PeerThreatLevel — no dependency on any specific transport package.

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from typing import Iterable, List

from .peer_security_types import (
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerThreatLevel,
)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ── Degradation weights by event kind ────────────────────────────────────────

_BASE_WEIGHTS = {
    PeerSecurityEventKind.AUTH_ATTEMPT: 0.05,
    PeerSecurityEventKind.ROUTING_ANOMALY: 0.10,
    PeerSecurityEventKind.BEHAVIOUR_CHANGE: 0.08,
    PeerSecurityEventKind.ENCRYPTION_EVENT: 0.06,
    PeerSecurityEventKind.INTRUSION_SIGNAL: 0.15,
    PeerSecurityEventKind.PRIVILEGE_ATTEMPT: 0.12,
    PeerSecurityEventKind.CONNECTION_ANOMALY: 0.07,
    PeerSecurityEventKind.DATA_EXFILTRATION: 0.14,
    PeerSecurityEventKind.DENIAL_OF_SERVICE: 0.13,
}

# ── Multipliers by threat level ──────────────────────────────────────────────

_THREAT_MULTIPLIERS = {
    PeerThreatLevel.NONE: 0.0,
    PeerThreatLevel.LOW: 0.5,
    PeerThreatLevel.MEDIUM: 1.0,
    PeerThreatLevel.HIGH: 2.0,
    PeerThreatLevel.CRITICAL: 3.0,
}


class ThreatDetector:
    """Stateless threat analysis helpers used by :class:`SecurityLayerService`
    and :class:`PeerIntelligenceService`.

    All members are static; the class is never instantiated.
    """

    @staticmethod
    def _base_weight(kind: PeerSecurityEventKind) -> float:
        # Default 0.05 for Unknown / unmapped kinds (matches C# `_ => 0.05`).
        return _BASE_WEIGHTS.get(kind, 0.05)

    @staticmethod
    def _threat_multiplier(level: PeerThreatLevel) -> float:
        # Default 1.0 for unmapped levels (matches C# `_ => 1.0`).
        return _THREAT_MULTIPLIERS.get(level, 1.0)

    @staticmethod
    def compute_degradation(e: PeerSecurityEvent) -> float:
        """Return the trust-score degradation amount for a security event,
        calculated as ``base_weight(kind) * threat_multiplier(level)``.

        Returns 0 when :attr:`PeerThreatLevel.NONE`.
        """
        return ThreatDetector._base_weight(e.kind) * ThreatDetector._threat_multiplier(
            e.threat_level
        )

    @staticmethod
    def detect_indicators(
        recent_events: Iterable[PeerSecurityEvent], window: timedelta
    ) -> List[str]:
        """Derive human-readable threat indicator tags from a set of recent
        events within the given ``window``.

        Returns an empty list when no patterns are detected.
        """
        cutoff = _utc_now() - window
        windowed = [e for e in recent_events if e.occurred_at >= cutoff]

        if len(windowed) == 0:
            return []

        indicators: List[str] = []

        # >= 3 auth attempts within the window -> brute-force signal
        if sum(1 for e in windowed if e.kind == PeerSecurityEventKind.AUTH_ATTEMPT) >= 3:
            indicators.append("repeated-auth-attempts")

        # Any intrusion signal -> explicit probe or exploit
        if any(e.kind == PeerSecurityEventKind.INTRUSION_SIGNAL for e in windowed):
            indicators.append("intrusion-signal-detected")

        # High or Critical event -> severity flag
        if any(
            e.threat_level in (PeerThreatLevel.HIGH, PeerThreatLevel.CRITICAL)
            for e in windowed
        ):
            indicators.append("high-severity-event")

        # >= 3 distinct event kinds -> multi-vector activity
        if len({e.kind for e in windowed}) >= 3:
            indicators.append("multi-vector-activity")

        # Privilege escalation attempt
        if any(e.kind == PeerSecurityEventKind.PRIVILEGE_ATTEMPT for e in windowed):
            indicators.append("privilege-escalation-attempt")

        # Data exfiltration signal
        if any(e.kind == PeerSecurityEventKind.DATA_EXFILTRATION for e in windowed):
            indicators.append("data-exfiltration-signal")

        return indicators
