"""circle_ai.pets — port of the CircleAI.Pets assembly.

(3.3.0) Real domain types + in-memory store for the Pets vertical: pets,
vaccinations, weight history, vet appointments — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Pet / Vaccination / WeightSample / VetAppointment — domain records.
  * IPetsBoard        — pet / vaccination / weight / appointment board.
  * InMemoryPetsBoard — thread-safe in-memory board.
  * PetsDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``PetsCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .pets_domain_context import PetsDomainContext
from .pets_primitives import (
    IPetsBoard,
    InMemoryPetsBoard,
    Pet,
    Vaccination,
    VetAppointment,
    WeightSample,
)

__all__ = [
    "Pet",
    "Vaccination",
    "WeightSample",
    "VetAppointment",
    "IPetsBoard",
    "InMemoryPetsBoard",
    "PetsDomainContext",
]
