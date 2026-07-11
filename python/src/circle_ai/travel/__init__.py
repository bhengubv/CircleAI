"""circle_ai.travel — port of the CircleAI.Travel assembly.

(3.3.0) Real domain types + in-memory board for the Travel vertical: flights,
hotel stays, trips, trip-cost totals — plus the static domain context. C# is the
exact spec. The C# ``Add(Flight)`` / ``Add(HotelStay)`` overloads become
``add_flight`` / ``add_stay`` (Python has no overloading).

The C# ``TravelCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .travel_domain_context import TravelDomainContext
from .travel_primitives import (
    Flight,
    HotelStay,
    ITravelBoard,
    InMemoryTravelBoard,
    TravelTrip,
)

__all__ = [
    "Flight",
    "HotelStay",
    "TravelTrip",
    "ITravelBoard",
    "InMemoryTravelBoard",
    "TravelDomainContext",
]
