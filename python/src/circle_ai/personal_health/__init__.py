"""circle_ai.personal_health — port of the CircleAI.Personal.Health assembly.

(3.3.0) Real domain types + in-memory board for personal health: vitals,
allergies, medications, last-reading helpers — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * VitalKind — vitals enum (stable ordinals).
  * VitalReading / Allergy / Medication — domain records.
  * IPersonalHealthBoard        — vitals / allergy / medication board.
  * InMemoryPersonalHealthBoard — thread-safe in-memory board.
  * PersonalHealthDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``PersonalHealthCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .personal_health_domain_context import PersonalHealthDomainContext
from .personal_health_primitives import (
    Allergy,
    IPersonalHealthBoard,
    InMemoryPersonalHealthBoard,
    Medication,
    VitalKind,
    VitalReading,
)

__all__ = [
    "VitalKind",
    "VitalReading",
    "Allergy",
    "Medication",
    "IPersonalHealthBoard",
    "InMemoryPersonalHealthBoard",
    "PersonalHealthDomainContext",
]
