# scenario.py
#
# Port of CircleAI.Simulation SimulationScenario.cs + SimulationResult.cs
# (C# — the EXACT spec).
#
#   • ScenarioKind — configuration-shift / data-pipeline / software-deployment /
#     security-patch / threat-propagation (IntEnum, declaration order).
#   • SimulationScenario — kind + params + step count (+ Create factory).
#   • SimulationOutcome — healthy / degraded / critical / unknown (IntEnum).
#   • SimulationResult — health score + findings + recommendations.
#
# C# records -> frozen slotted dataclasses. Guid -> uuid.UUID. DateTimeOffset ->
# datetime. HealthScore is float32.

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Mapping, Optional, Sequence


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class ScenarioKind(IntEnum):
    """Mirrors ``CircleAI.Simulation.ScenarioKind`` (declaration order)."""

    CONFIGURATION_SHIFT = 0
    DATA_PIPELINE_CHANGE = 1
    SOFTWARE_DEPLOYMENT = 2
    SECURITY_PATCH = 3
    THREAT_PROPAGATION = 4


class SimulationOutcome(IntEnum):
    """Mirrors ``CircleAI.Simulation.SimulationOutcome`` (declaration order)."""

    HEALTHY = 0
    DEGRADED = 1
    CRITICAL = 2
    UNKNOWN = 3


@dataclass(frozen=True, slots=True)
class SimulationScenario:
    """Mirrors ``CircleAI.Simulation.SimulationScenario`` — ``record(Guid Id,
    ScenarioKind Kind, string Description, IReadOnlyDictionary<string,string>
    Parameters, int StepCount, DateTimeOffset CreatedAt)``.
    """

    id: uuid.UUID
    kind: ScenarioKind
    description: str
    parameters: Mapping[str, str]
    step_count: int
    created_at: datetime

    @staticmethod
    def create(
        kind: ScenarioKind,
        description: str,
        parameters: Optional[Mapping[str, str]] = None,
        steps: int = 10,
    ) -> "SimulationScenario":
        """New scenario with a generated id + current UTC. Mirrors
        ``SimulationScenario.Create``."""
        return SimulationScenario(
            uuid.uuid4(),
            kind,
            description,
            dict(parameters) if parameters else {},
            steps,
            _utc_now(),
        )


@dataclass(frozen=True, slots=True)
class SimulationResult:
    """Mirrors ``CircleAI.Simulation.SimulationResult`` — ``record(Guid ScenarioId,
    SimulationOutcome Outcome, float HealthScore, IReadOnlyList<string> Findings,
    IReadOnlyList<string> Recommendations, int StepsRun, DateTimeOffset CompletedAt)``.
    """

    scenario_id: uuid.UUID
    outcome: SimulationOutcome
    health_score: float
    findings: Sequence[str]
    recommendations: Sequence[str]
    steps_run: int
    completed_at: datetime
