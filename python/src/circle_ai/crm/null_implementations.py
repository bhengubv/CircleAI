# null_implementations.py
#
# Port of CircleAI.CRM NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-closed / no-op CRM defaults. The C# `static readonly Instance`
# singleton maps to a module-level singleton bound after the class body. All
# reads return empty / None and all writes are no-ops.

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    Activity,
    Contact,
    Deal,
    IActivityLog,
    IContactStore,
    IDealPipeline,
)


class NullContactStore(IContactStore):
    Instance: "NullContactStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def upsert_async(self, c: Contact, ct: Optional[object] = None) -> None:
        return None

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Contact]:
        return None

    async def search_async(
        self, q: str, top_k: int = 20, ct: Optional[object] = None
    ) -> List[Contact]:
        return []


class NullDealPipeline(IDealPipeline):
    Instance: "NullDealPipeline"

    @property
    def backend_id(self) -> str:
        return "null"

    async def upsert_async(self, d: Deal, ct: Optional[object] = None) -> None:
        return None

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[Deal]:
        return None

    async def list_by_stage_async(
        self, stage: str, ct: Optional[object] = None
    ) -> List[Deal]:
        return []


class NullActivityLog(IActivityLog):
    Instance: "NullActivityLog"

    @property
    def backend_id(self) -> str:
        return "null"

    async def append_async(self, a: Activity, ct: Optional[object] = None) -> None:
        return None

    async def read_for_contact_async(
        self, c: str, limit: int = 100, ct: Optional[object] = None
    ) -> List[Activity]:
        return []


NullContactStore.Instance = NullContactStore()
NullDealPipeline.Instance = NullDealPipeline()
NullActivityLog.Instance = NullActivityLog()
