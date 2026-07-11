"""circle_ai.real_estate — port of the CircleAI.RealEstate assembly.

(3.3.0) Real domain types + in-memory store for the RealEstate vertical:
properties, listings, valuations, viewings + a suburb-average comparable — plus
the static domain context (system-prompt snippet, compliance flags, suggested
tools). C# is the exact spec.

Public surface:

  * PropertyKind — property-kind enum (IntEnum, C# ordinals).
  * Property / Listing / Valuation / Viewing — domain records.
  * IRealEstateBoard        — property / listing / valuation board.
  * InMemoryRealEstateBoard — thread-safe in-memory board.
  * RealEstateDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``RealEstateCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .real_estate_domain_context import RealEstateDomainContext
from .real_estate_primitives import (
    IRealEstateBoard,
    InMemoryRealEstateBoard,
    Listing,
    Property,
    PropertyKind,
    Valuation,
    Viewing,
)

__all__ = [
    "PropertyKind",
    "Property",
    "Listing",
    "Valuation",
    "Viewing",
    "IRealEstateBoard",
    "InMemoryRealEstateBoard",
    "RealEstateDomainContext",
]
