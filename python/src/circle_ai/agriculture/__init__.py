"""circle_ai.agriculture — port of the CircleAI.Agriculture assembly.

(3.3.0) Real domain types + in-memory board for the Agriculture vertical:
fields, crops, yield records, per-variety average yield — plus the static
domain context. C# is the exact spec.

The C# ``AgricultureCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .agriculture_domain_context import AgricultureDomainContext
from .agriculture_primitives import (
    Crop,
    Field,
    IFarmBoard,
    InMemoryFarmBoard,
    YieldRecord,
)

__all__ = [
    "Field",
    "Crop",
    "YieldRecord",
    "IFarmBoard",
    "InMemoryFarmBoard",
    "AgricultureDomainContext",
]
