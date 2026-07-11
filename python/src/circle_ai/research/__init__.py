"""circle_ai.research — port of the CircleAI.Research assembly.

(3.0.0 contracts / 3.3.0 in-memory) Research-corpus vertical: papers + citations,
with a substring-scored in-memory corpus, an in-memory full-text store, an
adjacency-list citation graph, and fail-safe null defaults. C# is the exact spec.

Public surface:

  * ResearchPaper / Citation — domain records.
  * IResearchCorpus / IPaperRetrieval / ICitationGraph — backend contracts.
  * InMemoryResearchCorpus / InMemoryPaperRetrieval / InMemoryCitationGraph.
  * NullResearchCorpus / NullPaperRetrieval / NullCitationGraph.
"""
from __future__ import annotations

from .contracts import (
    Citation,
    ICitationGraph,
    IPaperRetrieval,
    IResearchCorpus,
    ResearchPaper,
)
from .in_memory_research import (
    InMemoryCitationGraph,
    InMemoryPaperRetrieval,
    InMemoryResearchCorpus,
)
from .null_implementations import (
    NullCitationGraph,
    NullPaperRetrieval,
    NullResearchCorpus,
)

__all__ = [
    "ResearchPaper",
    "Citation",
    "IResearchCorpus",
    "IPaperRetrieval",
    "ICitationGraph",
    "InMemoryResearchCorpus",
    "InMemoryPaperRetrieval",
    "InMemoryCitationGraph",
    "NullResearchCorpus",
    "NullPaperRetrieval",
    "NullCitationGraph",
]
