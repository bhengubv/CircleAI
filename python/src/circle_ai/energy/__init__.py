"""circle_ai.energy — port of the CircleAI.Energy assembly.

(3.3.0) Real domain types + in-memory board for the Energy vertical: meter
readings, tariffs, outages, kWh totals + cost estimates — plus the static
domain context. C# is the exact spec.

The C# ``EnergyCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .energy_domain_context import EnergyDomainContext
from .energy_primitives import (
    EnergyTariff,
    IEnergyBoard,
    InMemoryEnergyBoard,
    MeterReading,
    Outage,
)

__all__ = [
    "MeterReading",
    "EnergyTariff",
    "Outage",
    "IEnergyBoard",
    "InMemoryEnergyBoard",
    "EnergyDomainContext",
]
