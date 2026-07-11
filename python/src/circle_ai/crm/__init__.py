"""circle_ai.crm — port of the CircleAI.CRM assembly.

(2.8.0 contracts / 3.3.0 in-memory impl) CRM domain: contacts, companies, deals,
activities, and the contact-store / deal-pipeline / activity-log contracts, with
concurrent in-memory backends (name/email substring search, stage-indexed deals,
per-contact activity log) and fail-closed null defaults. C# is the exact spec.

Public surface:

  * Contact / Company / Deal / Activity — domain records.
  * IContactStore / IDealPipeline / IActivityLog — backend contracts.
  * InMemoryContactStore / InMemoryDealPipeline / InMemoryActivityLog.
  * NullContactStore / NullDealPipeline / NullActivityLog — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    Activity,
    Company,
    Contact,
    Deal,
    IActivityLog,
    IContactStore,
    IDealPipeline,
)
from .in_memory_crm import (
    InMemoryActivityLog,
    InMemoryContactStore,
    InMemoryDealPipeline,
)
from .null_implementations import (
    NullActivityLog,
    NullContactStore,
    NullDealPipeline,
)

__all__ = [
    "Contact",
    "Company",
    "Deal",
    "Activity",
    "IContactStore",
    "IDealPipeline",
    "IActivityLog",
    "InMemoryContactStore",
    "InMemoryDealPipeline",
    "InMemoryActivityLog",
    "NullContactStore",
    "NullDealPipeline",
    "NullActivityLog",
]
