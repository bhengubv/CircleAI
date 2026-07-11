"""circle_ai.hr — port of the CircleAI.HR assembly.

(3.3.0) Real domain types + in-memory board for the HR vertical: employees,
leave requests, performance reviews — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Employee / LeaveRequest / PerformanceReview — domain records.
  * IHRBoard        — employee / leave / review board.
  * InMemoryHRBoard — thread-safe in-memory board.
  * HRDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``HRCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .hr_domain_context import HRDomainContext
from .hr_primitives import (
    Employee,
    IHRBoard,
    InMemoryHRBoard,
    LeaveRequest,
    PerformanceReview,
)

__all__ = [
    "Employee",
    "LeaveRequest",
    "PerformanceReview",
    "IHRBoard",
    "InMemoryHRBoard",
    "HRDomainContext",
]
