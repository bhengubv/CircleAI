"""circle_ai.home — port of the CircleAI.Home assembly.

(3.3.0) Real domain types + in-memory board for the Home vertical: rooms,
smart-home devices, maintenance tasks — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Room / HomeDevice / MaintenanceTask — domain records.
  * IHomeBoard        — room / device / task board.
  * InMemoryHomeBoard — thread-safe in-memory board.
  * HomeDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``HomeCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .home_domain_context import HomeDomainContext
from .home_primitives import (
    HomeDevice,
    IHomeBoard,
    InMemoryHomeBoard,
    MaintenanceTask,
    Room,
)

__all__ = [
    "Room",
    "HomeDevice",
    "MaintenanceTask",
    "IHomeBoard",
    "InMemoryHomeBoard",
    "HomeDomainContext",
]
