"""circle_ai.creative — port of the CircleAI.Creative assembly.

(3.3.0) Real domain types + in-memory board for the Creative vertical: works,
inspiration, critiques, average scores — plus the static domain context. C# is
the exact spec.

The C# ``CreativeCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .creative_domain_context import CreativeDomainContext
from .creative_primitives import (
    Critique,
    CreativeWork,
    ICreativeBoard,
    InMemoryCreativeBoard,
    Inspiration,
)

__all__ = [
    "CreativeWork",
    "Inspiration",
    "Critique",
    "ICreativeBoard",
    "InMemoryCreativeBoard",
    "CreativeDomainContext",
]
