"""circle_ai.personal_mental — port of the CircleAI.Personal.Mental assembly.

(3.3.0) Real domain types + in-memory board for the mental-health vertical:
mood logs, journal entries, coping-strategy library, 7-day trend — plus the
static domain context (system-prompt snippet, compliance flags, suggested
tools). C# is the exact spec.

Public surface:

  * Mood — mood enum (stable ordinals).
  * MoodLog / JournalEntry / CopingStrategy — domain records.
  * IMentalHealthBoard        — mood / journal / strategy board.
  * InMemoryMentalHealthBoard — thread-safe in-memory board (7-day mood trend).
  * PersonalMentalDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``PersonalMentalCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .personal_mental_domain_context import PersonalMentalDomainContext
from .personal_mental_primitives import (
    CopingStrategy,
    IMentalHealthBoard,
    InMemoryMentalHealthBoard,
    JournalEntry,
    Mood,
    MoodLog,
)

__all__ = [
    "Mood",
    "MoodLog",
    "JournalEntry",
    "CopingStrategy",
    "IMentalHealthBoard",
    "InMemoryMentalHealthBoard",
    "PersonalMentalDomainContext",
]
