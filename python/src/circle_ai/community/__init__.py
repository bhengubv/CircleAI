"""circle_ai.community — port of the CircleAI.Community assembly.

(3.3.0) Real domain types + in-memory board for the Community vertical: groups,
announcements, volunteer opportunities — plus the static domain context. C# is
the exact spec.

The C# ``CommunityCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .community_domain_context import CommunityDomainContext
from .community_primitives import (
    Announcement,
    CommunityGroup,
    ICommunityBoard,
    InMemoryCommunityBoard,
    VolunteerOpportunity,
)

__all__ = [
    "CommunityGroup",
    "Announcement",
    "VolunteerOpportunity",
    "ICommunityBoard",
    "InMemoryCommunityBoard",
    "CommunityDomainContext",
]
