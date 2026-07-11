"""circle_ai.construction — port of the CircleAI.Construction assembly.

(3.3.0) Real domain types + in-memory board for the Construction vertical:
projects, tasks, cost entries, spend + remaining budget — plus the static
domain context. C# is the exact spec.

The C# ``ConstructionCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .construction_domain_context import ConstructionDomainContext
from .construction_primitives import (
    ConstructionTask,
    CostEntry,
    IConstructionBoard,
    InMemoryConstructionBoard,
    Project,
)

__all__ = [
    "Project",
    "ConstructionTask",
    "CostEntry",
    "IConstructionBoard",
    "InMemoryConstructionBoard",
    "ConstructionDomainContext",
]
