"""circle_ai.family — port of the CircleAI.Family assembly.

(3.3.0) Real domain types + in-memory board for the Family vertical: family
members, shared calendar events, shared expenses — plus the static domain
context (system-prompt snippet, compliance flags, suggested tools). C# is the
exact spec.

Public surface:

  * FamilyMember / FamilyEvent / SharedExpense — domain records.
  * IFamilyBoard        — member / event / expense board.
  * InMemoryFamilyBoard — thread-safe in-memory board.
  * FamilyDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``FamilyCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .family_domain_context import FamilyDomainContext
from .family_primitives import (
    FamilyEvent,
    FamilyMember,
    IFamilyBoard,
    InMemoryFamilyBoard,
    SharedExpense,
)

__all__ = [
    "FamilyMember",
    "FamilyEvent",
    "SharedExpense",
    "IFamilyBoard",
    "InMemoryFamilyBoard",
    "FamilyDomainContext",
]
