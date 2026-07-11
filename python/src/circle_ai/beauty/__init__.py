"""circle_ai.beauty — port of the CircleAI.Beauty assembly.

(3.3.0) Real domain types + in-memory board for the Beauty vertical: priced
treatments, appointments, skin profiles, concern-driven recommendations — plus
the static domain context. C# is the exact spec.

The C# ``BeautyCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .beauty_domain_context import BeautyDomainContext
from .beauty_primitives import (
    Appointment,
    IBeautyBoard,
    InMemoryBeautyBoard,
    SkinProfile,
    Treatment,
)

__all__ = [
    "Treatment",
    "Appointment",
    "SkinProfile",
    "IBeautyBoard",
    "InMemoryBeautyBoard",
    "BeautyDomainContext",
]
