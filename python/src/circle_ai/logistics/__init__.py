"""circle_ai.logistics — port of the CircleAI.Logistics assembly.

(3.3.0) Real domain types + in-memory store for the Logistics vertical:
shipments, vehicles, route legs/plans + a simple route-cost estimator — plus
the static domain context (system-prompt snippet, compliance flags, suggested
tools). C# is the exact spec.

Public surface:

  * Shipment / Vehicle / RouteLeg / RoutePlan — domain records.
  * ILogisticsBoard        — shipment / vehicle / route board.
  * InMemoryLogisticsBoard — thread-safe in-memory board.
  * LogisticsDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``LogisticsCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .logistics_domain_context import LogisticsDomainContext
from .logistics_primitives import (
    ILogisticsBoard,
    InMemoryLogisticsBoard,
    RouteLeg,
    RoutePlan,
    Shipment,
    Vehicle,
)

__all__ = [
    "Shipment",
    "Vehicle",
    "RouteLeg",
    "RoutePlan",
    "ILogisticsBoard",
    "InMemoryLogisticsBoard",
    "LogisticsDomainContext",
]
