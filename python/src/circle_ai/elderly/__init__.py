"""circle_ai.elderly — port of the CircleAI.Elderly assembly.

(3.3.0) Real domain types + in-memory board for the Elderly-care vertical: care
plans, medication reminders, wellbeing check-ins — plus the static domain
context (system-prompt snippet, compliance flags, suggested tools). C# is the
exact spec.

Public surface:

  * CarePlan / MedReminder / CheckIn — domain records.
  * IElderlyCareBoard        — care-plan / reminder / check-in board.
  * InMemoryElderlyCareBoard — thread-safe in-memory board.
  * ElderlyDomainContext     — static system-prompt + compliance/tool metadata.

Note: the C# ``ElderlyCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .elderly_domain_context import ElderlyDomainContext
from .elderly_primitives import (
    CarePlan,
    CheckIn,
    IElderlyCareBoard,
    InMemoryElderlyCareBoard,
    MedReminder,
)

__all__ = [
    "CarePlan",
    "MedReminder",
    "CheckIn",
    "IElderlyCareBoard",
    "InMemoryElderlyCareBoard",
    "ElderlyDomainContext",
]
