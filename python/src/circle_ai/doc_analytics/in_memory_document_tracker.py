# in_memory_document_tracker.py
#
# Port of CircleAI.DocAnalytics InMemoryDocumentTracker.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory IDocumentTracker + IDocumentInsights. Records every view
# in a per-document list and computes insights on demand (total views, distinct
# viewers, average duration in seconds). C# ConcurrentDictionary + lock -> a
# plain dict guarded by a threading.Lock.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import (
    DocumentInsight,
    DocumentView,
    IDocumentInsights,
    IDocumentTracker,
)


class InMemoryDocumentTracker(IDocumentTracker, IDocumentInsights):
    """Thread-safe in-memory tracker + insights. Mirrors
    ``CircleAI.DocAnalytics.InMemoryDocumentTracker`` (implements both
    ``IDocumentTracker`` and ``IDocumentInsights``)."""

    def __init__(self) -> None:
        self._by_doc: Dict[str, List[DocumentView]] = {}
        self._write_lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def record_view_async(
        self, view: DocumentView, ct: Optional[object] = None
    ) -> None:
        if view is None:
            raise ValueError("view")
        if view.document_id is None or view.document_id.strip() == "":
            raise ValueError("DocumentId required")
        with self._write_lock:
            self._by_doc.setdefault(view.document_id, []).append(view)

    async def list_views_async(
        self, document_id: str, ct: Optional[object] = None
    ) -> List[DocumentView]:
        if document_id is None or document_id.strip() == "":
            raise ValueError("documentId required")
        with self._write_lock:
            got = self._by_doc.get(document_id)
            return list(got) if got is not None else []

    async def compute_async(
        self, document_id: str, ct: Optional[object] = None
    ) -> Optional[DocumentInsight]:
        if document_id is None or document_id.strip() == "":
            raise ValueError("documentId required")
        with self._write_lock:
            views = self._by_doc.get(document_id)
            if views is None or len(views) == 0:
                return None
            total = len(views)
            unique = len({v.viewer_id for v in views})
            avg_seconds = sum(v.duration.total_seconds() for v in views) / total
            return DocumentInsight(document_id, total, unique, avg_seconds)
