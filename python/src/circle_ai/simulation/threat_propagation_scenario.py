# threat_propagation_scenario.py
#
# Port of CircleAI.Simulation ThreatPropagationScenario.cs (C# — the EXACT spec).
#
# Factory that maps a CircleAI.Security AnomalySignal into a SimulationScenario of
# kind ThreatPropagation that NetworkHealthSimulator can run to forecast how the
# threat would spread through the peer network if not contained. This is the
# Simulation <-> Security integration point.
#
#   • step count derives from the ThreatVector (deeper for higher-severity vectors)
#     unless a step override is supplied.
#   • parameters seed from the signal's evidence plus signal_id / vector /
#     confidence (F3) / affected_module / detected_at (ISO-O).
#
# C# ``confidence.ToString("F3")`` -> ``f"{c:.3f}"``; ``confidence:P0`` (percent,
# no decimals) -> ``f"{c:.0%}"``. DateTimeOffset.ToString("O") -> isoformat.

from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import Dict, Optional

from ..security.anomaly_signal import AnomalySignal
from ..security.threat_vector import ThreatVector
from .scenario import ScenarioKind, SimulationScenario


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _step_count_for(vector: ThreatVector) -> int:
    if vector == ThreatVector.NETWORK_PIVOT:
        return 30
    if vector == ThreatVector.CONTROL_FLOW_DRIFT:
        return 25
    if vector == ThreatVector.PRIVILEGE_ESCALATION:
        return 25
    if vector == ThreatVector.STATE_CORRUPTION:
        return 20
    if vector == ThreatVector.MEMORY_ANOMALY:
        return 15
    if vector == ThreatVector.AGENT_PATCH_REJECTED:
        return 15
    if vector == ThreatVector.BIOMETRIC_SPOOF_ATTEMPT:
        return 12
    return 10


class ThreatPropagationScenario:
    """Factory for :class:`ScenarioKind.THREAT_PROPAGATION` scenarios from an
    :class:`AnomalySignal`. Mirrors the static
    ``CircleAI.Simulation.ThreatPropagationScenario``."""

    @staticmethod
    def from_anomaly_signal(
        signal: AnomalySignal, step_override: Optional[int] = None
    ) -> SimulationScenario:
        if signal is None:
            raise ValueError("signal")

        parameters: Dict[str, str] = dict(signal.evidence)
        parameters["signal_id"] = str(signal.id)
        parameters["vector"] = signal.vector.name  # C# Vector.ToString() -> enum name
        parameters["confidence"] = f"{signal.confidence:.3f}"
        parameters["affected_module"] = signal.affected_module
        parameters["detected_at"] = signal.detected_at.isoformat()

        steps = step_override if step_override is not None else _step_count_for(signal.vector)

        return SimulationScenario(
            id=uuid.uuid4(),
            kind=ScenarioKind.THREAT_PROPAGATION,
            description=(
                f"threat-propagation: {signal.vector.name} in {signal.affected_module} "
                f"(confidence {signal.confidence:.0%})"
            ),
            parameters=parameters,
            step_count=steps,
            created_at=_utc_now(),
        )
