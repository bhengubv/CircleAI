"""circle_ai.safety — port of the CircleAI.Safety assembly (personal-safety pack).

(3.3.0) Real domain types + in-memory store for the Safety vertical: incidents,
hazards, emergency contacts, severity-routing — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Distinct from :mod:`circle_ai.content_policy` (the ``CircleAI.ContentPolicy``
safety-guardrails assembly) — this pack is the personal-safety / emergency
domain.

Public surface:

  * IncidentSeverity   — Info / Warning / Critical / Emergency.
  * Incident / Hazard / EmergencyContact — domain records.
  * ISafetyBoard       — log/query incidents, hazards, contacts.
  * InMemorySafetyBoard — thread-safe in-memory board.
  * SafetyDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``SafetyCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .safety_domain_context import SafetyDomainContext
from .safety_primitives import (
    EmergencyContact,
    Hazard,
    Incident,
    IncidentSeverity,
    InMemorySafetyBoard,
    ISafetyBoard,
)

__all__ = [
    "IncidentSeverity",
    "Incident",
    "Hazard",
    "EmergencyContact",
    "ISafetyBoard",
    "InMemorySafetyBoard",
    "SafetyDomainContext",
]
