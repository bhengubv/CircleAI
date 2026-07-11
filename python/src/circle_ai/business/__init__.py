"""circle_ai.business — port of the CircleAI.Business assembly.

(3.3.0) Real domain types + in-memory board for the Business vertical: business
units (parent/child tree), KPI samples, quarterly targets — plus the static
domain context (system-prompt snippet, compliance flags, suggested tools).
C# is the exact spec.

Public surface:

  * BusinessUnit / KpiSample / QuarterTarget — domain records.
  * IBusinessBoard        — unit / KPI / target board.
  * InMemoryBusinessBoard — thread-safe in-memory board.
  * BusinessDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``BusinessCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .business_domain_context import BusinessDomainContext
from .business_primitives import (
    BusinessUnit,
    IBusinessBoard,
    InMemoryBusinessBoard,
    KpiSample,
    QuarterTarget,
)

__all__ = [
    "BusinessUnit",
    "KpiSample",
    "QuarterTarget",
    "IBusinessBoard",
    "InMemoryBusinessBoard",
    "BusinessDomainContext",
]
