# null_implementations.py
#
# Port of CircleAI.DocAnalytics NullImplementations.cs (C# — the EXACT spec).
#
# (2.9.0) Fail-safe defaults. Each exposes a singleton `INSTANCE` mirroring the
# C# `static readonly ... Instance`.

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    DocumentInsight,
    DocumentView,
    IDocumentInsights,
    IDocumentTracker,
)


class NullDocumentTracker(IDocumentTracker):
    INSTANCE: "NullDocumentTracker"

    @property
    def backend_id(self) -> str:
        return "null"

    async def record_view_async(
        self, view: DocumentView, ct: Optional[object] = None
    ) -> None:
        return None

    async def list_views_async(
        self, document_id: str, ct: Optional[object] = None
    ) -> List[DocumentView]:
        return []


class NullDocumentInsights(IDocumentInsights):
    INSTANCE: "NullDocumentInsights"

    @property
    def backend_id(self) -> str:
        return "null"

    async def compute_async(
        self, document_id: str, ct: Optional[object] = None
    ) -> Optional[DocumentInsight]:
        return None


NullDocumentTracker.INSTANCE = NullDocumentTracker()
NullDocumentInsights.INSTANCE = NullDocumentInsights()
