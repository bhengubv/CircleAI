"""circle_ai.hospitality — port of the CircleAI.Hospitality assembly.

(3.3.0) Real domain types + in-memory board for the Hospitality vertical: hotel
rooms, guest reservations, front-desk notes, availability + housekeeping state —
plus the static domain context. C# is the exact spec.

The C# ``HospitalityCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .hospitality_domain_context import HospitalityDomainContext
from .hospitality_primitives import (
    FrontDeskNote,
    GuestReservation,
    HotelRoom,
    IHospitalityBoard,
    InMemoryHospitalityBoard,
)

__all__ = [
    "HotelRoom",
    "GuestReservation",
    "FrontDeskNote",
    "IHospitalityBoard",
    "InMemoryHospitalityBoard",
    "HospitalityDomainContext",
]
