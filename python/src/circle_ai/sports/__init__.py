"""circle_ai.sports — port of the CircleAI.Sports assembly.

(3.3.0) Real domain types + in-memory board for the Sports vertical: activities
(run/bike/swim/walk/row), personal bests, training sessions, weekly volume —
plus the static domain context. C# is the exact spec.

The C# ``SportsCompanionAdapter`` decorates ``CircleAI.Companion.ICompanionSession``
(not part of the ported Python companion surface) so it is intentionally not
ported here.
"""
from __future__ import annotations

from .sports_domain_context import SportsDomainContext
from .sports_primitives import (
    Activity,
    DistanceKind,
    ISportsBoard,
    InMemorySportsBoard,
    PersonalBest,
    TrainingSession,
)

__all__ = [
    "DistanceKind",
    "Activity",
    "PersonalBest",
    "TrainingSession",
    "ISportsBoard",
    "InMemorySportsBoard",
    "SportsDomainContext",
]
