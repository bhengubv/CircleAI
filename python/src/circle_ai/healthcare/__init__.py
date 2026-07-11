"""circle_ai.healthcare — port of the CircleAI.Healthcare assembly.

(3.3.0) Real domain types + in-memory board for the Healthcare vertical:
patients, appointments, prescriptions — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Patient / HealthAppointment / Prescription — domain records.
  * IHealthcareBoard        — patient / appointment / prescription board.
  * InMemoryHealthcareBoard — thread-safe in-memory board.
  * HealthcareDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``HealthcareCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .healthcare_domain_context import HealthcareDomainContext
from .healthcare_primitives import (
    HealthAppointment,
    IHealthcareBoard,
    InMemoryHealthcareBoard,
    Patient,
    Prescription,
)

__all__ = [
    "Patient",
    "HealthAppointment",
    "Prescription",
    "IHealthcareBoard",
    "InMemoryHealthcareBoard",
    "HealthcareDomainContext",
]
