# null_implementations.py
#
# Port of CircleAI.Research NullImplementations.cs (C# — the EXACT spec).
#
# (3.0.0) Fail-safe defaults for every Research contract: get returns None,
# search / citations return empty. Each exposes a singleton `INSTANCE` mirroring
# the C# `static readonly ... Instance`.

from __future__ import annotations

from typing import List, Optional

from .contracts import Citation, ICitationGraph, IPaperRetrieval, IResearchCorpus, ResearchPaper


class NullResearchCorpus(IResearchCorpus):
    INSTANCE: "NullResearchCorpus"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> Optional[ResearchPaper]:
        return None

    async def search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[ResearchPaper]:
        return []


class NullPaperRetrieval(IPaperRetrieval):
    INSTANCE: "NullPaperRetrieval"

    @property
    def backend_id(self) -> str:
        return "null"

    async def fetch_full_text_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> Optional[bytes]:
        return None


class NullCitationGraph(ICitationGraph):
    INSTANCE: "NullCitationGraph"

    @property
    def backend_id(self) -> str:
        return "null"

    async def forward_citations_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> List[Citation]:
        return []

    async def backward_citations_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> List[Citation]:
        return []


NullResearchCorpus.INSTANCE = NullResearchCorpus()
NullPaperRetrieval.INSTANCE = NullPaperRetrieval()
NullCitationGraph.INSTANCE = NullCitationGraph()
