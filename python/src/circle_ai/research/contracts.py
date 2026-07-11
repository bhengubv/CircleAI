# contracts.py
#
# Port of CircleAI.Research Contracts.cs (C# — the EXACT spec).
#
# (3.0.0) Research-corpus contracts: paper + citation records and the corpus /
# full-text-retrieval / citation-graph backend interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. ReadOnlyMemory<byte>? -> Optional[bytes]. DateTimeOffset ->
# datetime. The optional CancellationToken is carried as an opt-in `ct` arg.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class ResearchPaper:
    """Mirrors ``CircleAI.Research.ResearchPaper`` — ``record(string PaperId,
    string Title, IReadOnlyList<string> Authors, string Abstract,
    DateTimeOffset PublishedAtUtc, string? Doi)``.
    """

    paper_id: str
    title: str
    authors: Sequence[str]
    abstract: str
    published_at_utc: datetime
    doi: Optional[str]


@dataclass(frozen=True, slots=True)
class Citation:
    """Mirrors ``CircleAI.Research.Citation`` — ``record(string FromPaperId,
    string ToPaperId, string Context)``.
    """

    from_paper_id: str
    to_paper_id: str
    context: str


class IResearchCorpus(ABC):
    """(3.0.0) Research-corpus contract — get + search papers."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> Optional[ResearchPaper]:
        ...

    @abstractmethod
    async def search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[ResearchPaper]:
        ...


class IPaperRetrieval(ABC):
    """(3.0.0) Full-text retrieval contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def fetch_full_text_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> Optional[bytes]:
        ...


class ICitationGraph(ABC):
    """(3.0.0) Citation-graph contract — forward + backward citations."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def forward_citations_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> List[Citation]:
        ...

    @abstractmethod
    async def backward_citations_async(
        self, paper_id: str, ct: Optional[object] = None
    ) -> List[Citation]:
        ...
