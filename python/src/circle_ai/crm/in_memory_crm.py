# in_memory_crm.py
#
# Port of CircleAI.CRM InMemoryCrm.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory CRM: contact store with name/email substring search,
# deal pipeline indexed by stage, activity log per contact.
#
# C# ConcurrentDictionary stores map to plain dicts guarded by a lock; the
# activity log's per-contact lists are guarded by a single lock (mirroring the
# C# monitor lock). C# ValueTask methods map to async def. String comparisons are
# ordinal / ordinal-ignore-case as in C#: SearchAsync matches FullName or Email
# case-insensitively and orders by FullName (case-insensitive); ListByStageAsync
# matches Stage case-insensitively and orders by Value descending;
# ReadForContactAsync returns newest-first, limited. Blank ids/stage raise
# ValueError; a None query raises ValueError; topK<=0 raises ValueError.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import (
    Activity,
    Contact,
    Deal,
    IActivityLog,
    IContactStore,
    IDealPipeline,
)


def _blank(s: Optional[str]) -> bool:
    return s is None or not s.strip()


class InMemoryContactStore(IContactStore):
    """Thread-safe in-memory :class:`IContactStore`."""

    def __init__(self) -> None:
        self._items: Dict[str, Contact] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def upsert_async(self, c: Contact, ct: Optional[object] = None) -> None:
        if c is None:
            raise ValueError("contact must not be None")
        if _blank(c.contact_id):
            raise ValueError("ContactId required")
        with self._lock:
            self._items[c.contact_id] = c

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Contact]:
        if _blank(id):
            raise ValueError("id required")
        with self._lock:
            return self._items.get(id)

    async def search_async(
        self, query: str, top_k: int = 20, ct: Optional[object] = None
    ) -> List[Contact]:
        if query is None:
            raise ValueError("query must not be None")
        if top_k <= 0:
            raise ValueError("top_k")
        q = query.casefold()
        with self._lock:
            hits = [
                c
                for c in self._items.values()
                if q in c.full_name.casefold()
                or (c.email is not None and q in c.email.casefold())
            ]
        hits.sort(key=lambda c: c.full_name.casefold())
        return hits[:top_k]


class InMemoryDealPipeline(IDealPipeline):
    """Thread-safe in-memory :class:`IDealPipeline`."""

    def __init__(self) -> None:
        self._items: Dict[str, Deal] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def upsert_async(self, d: Deal, ct: Optional[object] = None) -> None:
        if d is None:
            raise ValueError("deal must not be None")
        if _blank(d.deal_id):
            raise ValueError("DealId required")
        with self._lock:
            self._items[d.deal_id] = d

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Deal]:
        with self._lock:
            return self._items.get(id)

    async def list_by_stage_async(
        self, stage: str, ct: Optional[object] = None
    ) -> List[Deal]:
        if _blank(stage):
            raise ValueError("stage required")
        s = stage.casefold()
        with self._lock:
            hits = [d for d in self._items.values() if d.stage.casefold() == s]
        hits.sort(key=lambda d: d.value, reverse=True)
        return hits


class InMemoryActivityLog(IActivityLog):
    """Thread-safe in-memory :class:`IActivityLog`."""

    def __init__(self) -> None:
        self._by_contact: Dict[str, List[Activity]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def append_async(self, a: Activity, ct: Optional[object] = None) -> None:
        if a is None:
            raise ValueError("activity must not be None")
        if _blank(a.contact_id):
            raise ValueError("ContactId required")
        with self._lock:
            self._by_contact.setdefault(a.contact_id, []).append(a)

    async def read_for_contact_async(
        self, contact_id: str, limit: int = 100, ct: Optional[object] = None
    ) -> List[Activity]:
        if _blank(contact_id):
            raise ValueError("contact_id required")
        with self._lock:
            lst = self._by_contact.get(contact_id)
            if lst is None:
                return []
            ordered = sorted(lst, key=lambda a: a.at_utc, reverse=True)
        return ordered[:limit]
