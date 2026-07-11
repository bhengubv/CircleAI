# in_memory_research.py
#
# Port of CircleAI.Research InMemoryResearch.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory research corpus + full-text retrieval + citation graph.
# Search scores by substring hit on title (+3) / abstract (+1) / any author (+1),
# keeps score > 0, orders by score desc (stable), takes top-k. Citations are a
# plain forward/backward adjacency list. C# ConcurrentDictionary + lock -> plain
# dicts guarded by a threading.Lock.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import Citation, ICitationGraph, IPaperRetrieval, IResearchCorpus, ResearchPaper


class InMemoryResearchCorpus(IResearchCorpus):
    """Real in-memory :class:`IResearchCorpus`. Mirrors
    ``CircleAI.Research.InMemoryResearchCorpus``."""

    def __init__(self) -> None:
        self._papers: Dict[str, ResearchPaper] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def add(self, paper: ResearchPaper) -> None:
        if paper is None:
            raise ValueError("paper")
        with self._lock:
            self._papers[paper.paper_id] = paper

    async def get_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> Optional[ResearchPaper]:
        if paper_id is None or paper_id.strip() == "":
            raise ValueError("paperId required")
        with self._lock:
            return self._papers.get(paper_id)

    async def search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[ResearchPaper]:
        if query is None:
            raise ValueError("query")
        if top_k <= 0:
            raise ValueError("topK")
        with self._lock:
            papers = list(self._papers.values())
        scored = [(p, self._score(p, query)) for p in papers]
        hits = [(p, s) for (p, s) in scored if s > 0]
        # OrderByDescending is stable in C#; Python sorted is stable — keep it so.
        hits.sort(key=lambda t: t[1], reverse=True)
        return [p for (p, _s) in hits[:top_k]]

    @staticmethod
    def _score(p: ResearchPaper, q: str) -> int:
        s = 0
        ql = q.lower()
        if p.title is not None and ql in p.title.lower():
            s += 3
        if p.abstract is not None and ql in p.abstract.lower():
            s += 1
        if p.authors is not None and any(ql in a.lower() for a in p.authors):
            s += 1
        return s


class InMemoryPaperRetrieval(IPaperRetrieval):
    """Real in-memory :class:`IPaperRetrieval`. Mirrors
    ``CircleAI.Research.InMemoryPaperRetrieval``."""

    def __init__(self) -> None:
        self._texts: Dict[str, bytes] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def add(self, paper_id: str, full_text: bytes) -> None:
        if paper_id is None or paper_id.strip() == "":
            raise ValueError("paperId required")
        with self._lock:
            self._texts[paper_id] = bytes(full_text)

    async def fetch_full_text_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> Optional[bytes]:
        if paper_id is None or paper_id.strip() == "":
            raise ValueError("paperId required")
        with self._lock:
            return self._texts.get(paper_id)


class InMemoryCitationGraph(ICitationGraph):
    """Real in-memory :class:`ICitationGraph`. Mirrors
    ``CircleAI.Research.InMemoryCitationGraph``."""

    def __init__(self) -> None:
        self._forward: Dict[str, List[Citation]] = {}
        self._backward: Dict[str, List[Citation]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def link(self, c: Citation) -> None:
        if c is None:
            raise ValueError("citation")
        with self._lock:
            self._forward.setdefault(c.from_paper_id, []).append(c)
            self._backward.setdefault(c.to_paper_id, []).append(c)

    async def forward_citations_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> List[Citation]:
        if paper_id is None or paper_id.strip() == "":
            raise ValueError("paperId required")
        with self._lock:
            got = self._forward.get(paper_id)
            return list(got) if got is not None else []

    async def backward_citations_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> List[Citation]:
        if paper_id is None or paper_id.strip() == "":
            raise ValueError("paperId required")
        with self._lock:
            got = self._backward.get(paper_id)
            return list(got) if got is not None else []
