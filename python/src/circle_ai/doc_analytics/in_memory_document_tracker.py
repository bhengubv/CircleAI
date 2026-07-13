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
from typing import Dict, List, Optional, Tuple

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

    @property
    def document_count(self) -> int:
        """Number of distinct documents that have at least one recorded view
        (C#: ``DocumentCount``).
        """
        with self._write_lock:
            return len(self._by_doc)

    @property
    def total_views(self) -> int:
        """Total views recorded across every tracked document
        (C#: ``TotalViews``).
        """
        with self._write_lock:
            return sum(len(v) for v in self._by_doc.values())

    def clear(self, document_id: str) -> bool:
        """Drop all recorded views for ``document_id``. Returns True if anything
        was removed (C#: ``Clear``).
        """
        if document_id is None or document_id.strip() == "":
            raise ValueError("documentId required")
        with self._write_lock:
            return self._by_doc.pop(document_id, None) is not None

    def top_documents(self, top_k: int = 5) -> List[Tuple[str, int]]:
        """The most-viewed documents, highest first, capped at ``top_k``
        (C#: ``TopDocuments`` — ``(DocumentId, Views)`` pairs).
        """
        if top_k <= 0:
            raise ValueError("top_k must be positive")
        with self._write_lock:
            pairs = [(k, len(v)) for k, v in self._by_doc.items()]
        pairs.sort(key=lambda t: t[1], reverse=True)
        return pairs[:top_k]

    def recent_views(
        self, document_id: str, limit: int = 20
    ) -> List[DocumentView]:
        """Most recent views for ``document_id``, newest first
        (C#: ``RecentViews``).
        """
        if document_id is None or document_id.strip() == "":
            raise ValueError("documentId required")
        if limit <= 0:
            raise ValueError("limit must be positive")
        with self._write_lock:
            views = self._by_doc.get(document_id)
            if views is None:
                return []
            ordered = sorted(views, key=lambda v: v.at_utc, reverse=True)
        return ordered[:limit]

    def total_pages_viewed(self, document_id: str) -> int:
        """Sum of pages viewed across every recorded view of ``document_id``
        (C#: ``TotalPagesViewed`` — 0 when the document is unknown).
        """
        if document_id is None or document_id.strip() == "":
            raise ValueError("documentId required")
        with self._write_lock:
            views = self._by_doc.get(document_id)
            return sum(v.pages_viewed for v in views) if views is not None else 0

    def most_engaged_viewer(self, document_id: str) -> Optional[str]:
        """The viewer who spent the most cumulative time on ``document_id``, if
        any (C#: ``MostEngagedViewer`` — groups by viewer, orders by total
        duration descending, takes the first; ties keep first-seen order).
        """
        if document_id is None or document_id.strip() == "":
            raise ValueError("documentId required")
        with self._write_lock:
            views = self._by_doc.get(document_id)
            if views is None or len(views) == 0:
                return None
            totals: Dict[str, float] = {}
            for v in views:
                totals[v.viewer_id] = (
                    totals.get(v.viewer_id, 0.0) + v.duration.total_seconds()
                )
        ordered = sorted(
            totals.items(), key=lambda kv: kv[1], reverse=True
        )
        return ordered[0][0]
