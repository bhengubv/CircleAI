"""circle_ai.legal — port of the CircleAI.Legal assembly.

(3.3.0) Real domain types + in-memory board for the Legal vertical: matters,
contracts, deadlines, clause library — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Matter / Contract / LegalDeadline / Clause — domain records.
  * ILegalBoard        — matter / contract / deadline / clause board.
  * InMemoryLegalBoard — thread-safe in-memory board.
  * LegalDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``LegalCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .legal_domain_context import LegalDomainContext
from .legal_primitives import (
    Clause,
    Contract,
    ILegalBoard,
    InMemoryLegalBoard,
    LegalDeadline,
    Matter,
)

__all__ = [
    "Matter",
    "Contract",
    "LegalDeadline",
    "Clause",
    "ILegalBoard",
    "InMemoryLegalBoard",
    "LegalDomainContext",
]
