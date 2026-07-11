"""circle_ai.faith — port of the CircleAI.Faith assembly.

(3.3.0) Real domain types + in-memory board for the Faith vertical: services,
prayer requests, scripture references — plus the static domain context. C# is
the exact spec.

The C# ``FaithCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .faith_domain_context import FaithDomainContext
from .faith_primitives import (
    FaithService,
    IFaithBoard,
    InMemoryFaithBoard,
    PrayerRequest,
    ScriptureReference,
)

__all__ = [
    "FaithService",
    "PrayerRequest",
    "ScriptureReference",
    "IFaithBoard",
    "InMemoryFaithBoard",
    "FaithDomainContext",
]
