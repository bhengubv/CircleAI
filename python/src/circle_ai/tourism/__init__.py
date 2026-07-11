"""circle_ai.tourism — port of the CircleAI.Tourism assembly.

(3.3.0) Real domain types + in-memory board for the Tourism vertical:
attractions, itineraries, bookings, city/tag search — plus the static domain
context. C# is the exact spec.

The C# ``TourismCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .tourism_domain_context import TourismDomainContext
from .tourism_primitives import (
    Attraction,
    ITourismBoard,
    InMemoryTourismBoard,
    Itinerary,
    ItineraryItem,
    TourismBooking,
)

__all__ = [
    "Attraction",
    "ItineraryItem",
    "Itinerary",
    "TourismBooking",
    "ITourismBoard",
    "InMemoryTourismBoard",
    "TourismDomainContext",
]
