"""circle_ai.relationships — port of the CircleAI.Relationships assembly.

(3.3.0) Real domain types + in-memory CRM-lite for personal relationships:
contacts, important dates, a last-contact tracker — plus the static domain
context. C# is the exact spec.

The C# ``RelationshipsCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .relationships_domain_context import RelationshipsDomainContext
from .relationships_primitives import (
    ContactEvent,
    IRelationshipsBoard,
    ImportantDate,
    InMemoryRelationshipsBoard,
    PersonContact,
)

__all__ = [
    "PersonContact",
    "ImportantDate",
    "ContactEvent",
    "IRelationshipsBoard",
    "InMemoryRelationshipsBoard",
    "RelationshipsDomainContext",
]
