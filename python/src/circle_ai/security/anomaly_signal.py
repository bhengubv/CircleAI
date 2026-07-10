# anomaly_signal.py
#
# Port of CircleAI.Security.AnomalySignal (C# — the EXACT spec).
#
# Carries the details of a locally-detected runtime anomaly from the detection
# site to the ISecurityWatchdog handler.
#
# The signal is IMMUTABLE — detection sites create it and hand it off. The
# watchdog (and any ops-security agent) reads it and decides the response.
#
# The Evidence property is wired to RedactedEvidenceJsonConverter in C#; the
# Python port exposes :meth:`to_redacted_dict` for the same effect on the
# serialisation path.

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, Mapping, Optional
from uuid import UUID, uuid4

from .redacted_evidence_json_converter import RedactedEvidenceJsonConverter
from .threat_vector import ThreatVector


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _clamp(value: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, value))


@dataclass(frozen=True, slots=True)
class AnomalySignal:
    """An immutable record describing a locally-detected runtime anomaly.

    Created at the detection site (e.g. the companion pipeline, the biometric
    verifier, or an agent patch gate) and consumed by the host-side
    ``ISecurityWatchdog.on_anomaly_detected_async`` handler.

    Use :py:meth:`create` to construct — it stamps a fresh UUID, the current
    UTC timestamp, and clamps ``confidence`` to ``[0.0, 1.0]``.
    """

    # Unique identifier for this signal instance.
    id: UUID

    # Classification of the detected threat.
    vector: ThreatVector

    # Confidence that this is a genuine threat, in [0.0, 1.0].
    # 1.0 = definitive; 0.0 = speculative.
    confidence: float

    # The module or subsystem where the anomaly was detected
    # (e.g. "CircleAI.Companion", "CircleAI.Identity").
    affected_module: str

    # Human-readable description of the anomaly.
    description: str

    # Optional structured evidence attached by the detection site.
    # Keys are evidence labels; values are serialised data or hashes.
    evidence: Mapping[str, str] = field(default_factory=dict)

    # UTC timestamp of detection.
    detected_at: datetime = field(default_factory=_utc_now)

    @classmethod
    def create(
        cls,
        vector: ThreatVector,
        confidence: float,
        affected_module: str,
        description: str,
        evidence: Optional[Mapping[str, str]] = None,
    ) -> "AnomalySignal":
        """Create an :class:`AnomalySignal` with a new UUID and current UTC time.

        Confidence is clamped to ``[0.0, 1.0]``.
        """
        return cls(
            id=uuid4(),
            vector=vector,
            confidence=_clamp(confidence, 0.0, 1.0),
            affected_module=affected_module,
            description=description,
            evidence=dict(evidence) if evidence else {},
            detected_at=_utc_now(),
        )

    def to_redacted_dict(self) -> Dict[str, object]:
        """Serialise to a plain dict with :attr:`evidence` values redacted to
        their SHA-256 tags via :class:`RedactedEvidenceJsonConverter`.

        Mirrors the ``[JsonConverter(typeof(RedactedEvidenceJsonConverter))]``
        attribute on the C# ``Evidence`` property: labels are preserved, raw
        values are never emitted in clear.
        """
        return {
            "id": str(self.id),
            "vector": int(self.vector),
            "confidence": self.confidence,
            "affectedModule": self.affected_module,
            "description": self.description,
            "evidence": RedactedEvidenceJsonConverter().write(self.evidence),
            "detectedAt": self.detected_at.isoformat(),
        }
