"""circle_ai.doc_analytics — port of the CircleAI.DocAnalytics assembly.

(2.9.0 contracts / 3.3.0 in-memory) Document-analytics surface: view tracking +
insight computation (total / unique viewers / average duration) with a real
in-memory tracker and fail-safe null defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    DocumentInsight,
    DocumentView,
    IDocumentInsights,
    IDocumentTracker,
)
from .in_memory_document_tracker import InMemoryDocumentTracker
from .null_implementations import NullDocumentInsights, NullDocumentTracker

__all__ = [
    "DocumentView",
    "DocumentInsight",
    "IDocumentTracker",
    "IDocumentInsights",
    "InMemoryDocumentTracker",
    "NullDocumentTracker",
    "NullDocumentInsights",
]
