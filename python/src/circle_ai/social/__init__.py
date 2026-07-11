"""circle_ai.social — port of the CircleAI.Social assembly.

(3.3.0) Real domain types + in-memory board for the Social vertical: posts,
reactions, a follow graph, a follow-based feed — plus the static domain context.
C# is the exact spec.

The C# ``SocialCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .social_domain_context import SocialDomainContext
from .social_primitives import (
    Follow,
    ISocialBoard,
    InMemorySocialBoard,
    Reaction,
    SocialPost,
)

__all__ = [
    "SocialPost",
    "Reaction",
    "Follow",
    "ISocialBoard",
    "InMemorySocialBoard",
    "SocialDomainContext",
]
