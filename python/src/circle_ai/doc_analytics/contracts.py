# contracts.py
#
# Port of CircleAI.DocAnalytics Contracts.cs (C# — the EXACT spec).
#
# (2.9.0) Document-analytics contracts: view + insight records and the tracker /
# insights interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. TimeSpan -> timedelta. DateTimeOffset -> datetime.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class DocumentView:
    """Mirrors ``CircleAI.DocAnalytics.DocumentView`` — ``record(string DocumentId,
    string ViewerId, DateTimeOffset AtUtc, TimeSpan Duration, int PagesViewed)``.
    """

    document_id: str
    viewer_id: str
    at_utc: datetime
    duration: timedelta
    pages_viewed: int


@dataclass(frozen=True, slots=True)
class DocumentInsight:
    """Mirrors ``CircleAI.DocAnalytics.DocumentInsight`` — ``record(string
    DocumentId, int TotalViews, int UniqueViewers, double AvgDurationSeconds)``.
    """

    document_id: str
    total_views: int
    unique_viewers: int
    avg_duration_seconds: float


class IDocumentTracker(ABC):
    """(2.9.0) Document view tracker."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def record_view_async(
        self, view: DocumentView, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def list_views_async(
        self, document_id: str, ct: Optional[object] = None
    ) -> List[DocumentView]:
        ...


class IDocumentInsights(ABC):
    """(2.9.0) Document insight computation."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def compute_async(
        self, document_id: str, ct: Optional[object] = None
    ) -> Optional[DocumentInsight]:
        ...
