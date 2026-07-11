"""circle_ai.ambient — port of the CircleAI.Ambient assembly.

(3.3.0) Real domain types + in-memory board for the Ambient vertical:
environmental readings (temperature / humidity / lux / noise) and per-location
comfort preferences, with a comfort check. C# is the exact spec.

The C# assembly has no ``AmbientDomainContext``; its ``AmbientCompanionMonitor``
(an ICompanionSession/host decorator) is intentionally not ported.
"""
from __future__ import annotations

from .ambient_primitives import (
    AmbientPreference,
    AmbientReading,
    IAmbientBoard,
    InMemoryAmbientBoard,
)

__all__ = [
    "AmbientReading",
    "AmbientPreference",
    "IAmbientBoard",
    "InMemoryAmbientBoard",
]
