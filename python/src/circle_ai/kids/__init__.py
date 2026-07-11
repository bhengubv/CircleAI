"""circle_ai.kids — port of the CircleAI.Kids assembly.

(3.3.0) Real domain types + in-memory board for the Kids vertical: age-banded
content, daily time limits, time logs, over-limit checks — plus the static
domain context and the AgeAppropriateness enum. C# is the exact spec.

The C# ``KidsCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .kids_domain_context import KidsDomainContext
from .kids_primitives import (
    AgeAppropriateness,
    DailyTime,
    IKidsBoard,
    InMemoryKidsBoard,
    KidsContent,
    TimeLog,
)

__all__ = [
    "AgeAppropriateness",
    "KidsContent",
    "DailyTime",
    "TimeLog",
    "IKidsBoard",
    "InMemoryKidsBoard",
    "KidsDomainContext",
]
