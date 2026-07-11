"""circle_ai.civic — port of the CircleAI.Civic assembly.

(3.3.0) Real domain types + in-memory board for the Civic vertical: reported
issues, representatives, civic events — plus the static domain context. C# is
the exact spec.

The C# ``CivicCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .civic_domain_context import CivicDomainContext
from .civic_primitives import (
    CivicEvent,
    CivicIssue,
    ICivicBoard,
    InMemoryCivicBoard,
    Representative,
)

__all__ = [
    "CivicIssue",
    "Representative",
    "CivicEvent",
    "ICivicBoard",
    "InMemoryCivicBoard",
    "CivicDomainContext",
]
