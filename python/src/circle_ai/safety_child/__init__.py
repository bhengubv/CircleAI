"""circle_ai.safety_child — port of the CircleAI.Safety.Child assembly.

(3.3.0) Real domain types + in-memory store for the Child Safety vertical:
trusted-adult ring, geofences, check-in events — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * TrustedAdult / Geofence / CheckIn — domain records.
  * IChildSafetyBoard        — ring / geofence / check-in board.
  * InMemoryChildSafetyBoard — thread-safe in-memory board (Haversine geofencing).
  * SafetyChildDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``SafetyChildCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .child_safety_primitives import (
    CheckIn,
    Geofence,
    IChildSafetyBoard,
    InMemoryChildSafetyBoard,
    TrustedAdult,
)
from .safety_child_domain_context import SafetyChildDomainContext

__all__ = [
    "TrustedAdult",
    "Geofence",
    "CheckIn",
    "IChildSafetyBoard",
    "InMemoryChildSafetyBoard",
    "SafetyChildDomainContext",
]
