"""circle_ai.parenting — port of the CircleAI.Parenting assembly.

(3.3.0) Real domain types + in-memory store for the Parenting vertical:
children, milestones, school-day routines — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * DayOfWeek — day-of-week enum (IntEnum, System.DayOfWeek ordinals).
  * Child / Milestone / RoutineEntry / Routine — domain records.
  * IParentingBoard        — child / milestone / routine board.
  * InMemoryParentingBoard — thread-safe in-memory board.
  * ParentingDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``ParentingCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .parenting_domain_context import ParentingDomainContext
from .parenting_primitives import (
    Child,
    DayOfWeek,
    IParentingBoard,
    InMemoryParentingBoard,
    Milestone,
    Routine,
    RoutineEntry,
)

__all__ = [
    "DayOfWeek",
    "Child",
    "Milestone",
    "RoutineEntry",
    "Routine",
    "IParentingBoard",
    "InMemoryParentingBoard",
    "ParentingDomainContext",
]
