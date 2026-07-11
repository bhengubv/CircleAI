# contracts.py
#
# Port of CircleAI.Domain Contracts.cs (C# — the EXACT spec).
#
# (2.4.0) The CircleAI.Domain contract surface — every domain-specialist plug
# point: food embeddings (EPICure), finance retrieval + agent (quant-mind /
# dexter), presentation generation (presenton), job-search pipeline (career-ops),
# long-term memory (mempalace / HippoRAG), swarm coordination (MiroFish), and
# on-device personalisation (personal LoRA).
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. float[] -> List[float]. IReadOnlyDictionary<string,string>? ->
# Optional[Mapping[str,str]]. The optional CancellationToken -> opt-in `ct` arg.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Mapping, Optional, Sequence


# ─── Food (EPICure) ──────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class Ingredient:
    """Mirrors ``CircleAI.Domain.Ingredient`` — ``record(string Name,
    string? Canonical = null, string? Quantity = null)``.
    """

    name: str
    canonical: Optional[str] = None
    quantity: Optional[str] = None


class IFoodEmbeddings(ABC):
    """(2.4.0) Food / ingredient embedding store (EPICure-backed)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def embed_async(
        self, ingredient: Ingredient, ct: Optional[object] = None
    ) -> List[float]:
        ...

    @abstractmethod
    async def substitutes_async(
        self, ingredient: Ingredient, top_k: int = 5, ct: Optional[object] = None
    ) -> List[Ingredient]:
        ...


# ─── Finance (quant-mind + dexter) ───────────────────────────────────────


@dataclass(frozen=True, slots=True)
class FinanceSnippet:
    """Mirrors ``CircleAI.Domain.FinanceSnippet`` — ``record(string Text,
    string Source, float Score)``.
    """

    text: str
    source: str
    score: float


class IFinanceRetrieval(ABC):
    """(2.4.0) Quant-finance RAG retrieval."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def retrieve_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[FinanceSnippet]:
        ...


@dataclass(frozen=True, slots=True)
class FinanceFinding:
    """Mirrors ``CircleAI.Domain.FinanceFinding`` — ``record(string Subject,
    string Summary, IReadOnlyList<string> Citations)``.
    """

    subject: str
    summary: str
    citations: Sequence[str]


class IFinancialAgent(ABC):
    """(2.4.0) Autonomous financial-research agent (dexter pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def research_async(
        self, question: str, ct: Optional[object] = None
    ) -> List[FinanceFinding]:
        ...


# ─── Presentations (presenton) ──────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class SlideOutline:
    """Mirrors ``CircleAI.Domain.SlideOutline`` — ``record(string Title,
    string Body, IReadOnlyList<string>? Bullets = null)``.
    """

    title: str
    body: str
    bullets: Optional[Sequence[str]] = None


@dataclass(frozen=True, slots=True)
class GeneratedPresentation:
    """Mirrors ``CircleAI.Domain.GeneratedPresentation`` —
    ``record(IReadOnlyList<SlideOutline> Slides, string Theme, string Format)``.
    """

    slides: Sequence[SlideOutline]
    theme: str
    format: str


class IPresentationGenerator(ABC):
    """(2.4.0) AI presentation generator (presenton pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def generate_async(
        self,
        topic: str,
        target_slide_count: int = 10,
        theme: Optional[str] = None,
        ct: Optional[object] = None,
    ) -> GeneratedPresentation:
        ...


# ─── Job search (career-ops, TheJobCenter target) ───────────────────────


@dataclass(frozen=True, slots=True)
class JobApplicationDraft:
    """Mirrors ``CircleAI.Domain.JobApplicationDraft`` — ``record(string
    ResumeText, string CoverLetterText, IReadOnlyList<string> KeyMatches)``.
    """

    resume_text: str
    cover_letter_text: str
    key_matches: Sequence[str]


class IJobSearchPipeline(ABC):
    """(2.4.0) Job-search pipeline — resume + cover letter + match (career-ops)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def draft_application_async(
        self,
        role_description: str,
        candidate_profile_text: str,
        ct: Optional[object] = None,
    ) -> JobApplicationDraft:
        ...


# ─── Memory upgrades (mempalace + HippoRAG) ─────────────────────────────


@dataclass(frozen=True, slots=True)
class MemoryItem:
    """Mirrors ``CircleAI.Domain.MemoryItem`` — ``record(string Id, string Text,
    IReadOnlyDictionary<string,string>? Metadata = null)``.
    """

    id: str
    text: str
    metadata: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class MemoryHit:
    """Mirrors ``CircleAI.Domain.MemoryHit`` — ``record(MemoryItem Item,
    float Score)``.
    """

    item: MemoryItem
    score: float


class IMemPalaceStore(ABC):
    """(2.4.0) MemPalace-pattern long-term memory."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def upsert_async(
        self, item: MemoryItem, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def recall_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[MemoryHit]:
        ...


class IHippoRagStore(ABC):
    """(2.4.0) HippoRAG-pattern memory + knowledge-graph + Personalized PageRank."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def index_async(
        self, item: MemoryItem, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def multi_hop_recall_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[MemoryHit]:
        ...


# ─── Swarm (MiroFish) ───────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class SwarmPeer:
    """Mirrors ``CircleAI.Domain.SwarmPeer`` — ``record(string PeerId,
    string Capability, float Health)``.
    """

    peer_id: str
    capability: str
    health: float


class ISwarmCoordinator(ABC):
    """(2.4.0) Multi-device coordination over AetherNet (MiroFish-pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def list_peers_async(
        self, ct: Optional[object] = None
    ) -> List[SwarmPeer]:
        ...

    @abstractmethod
    async def choose_delegate_async(
        self, capability: str, ct: Optional[object] = None
    ) -> Optional[str]:
        ...


# ─── Personal LoRA (RT-10, conditional) ─────────────────────────────────


@dataclass(frozen=True, slots=True)
class LoRATrainingSummary:
    """Mirrors ``CircleAI.Domain.LoRATrainingSummary`` — ``record(string
    AdapterId, int StepsTrained, float FinalLoss)``.
    """

    adapter_id: str
    steps_trained: int
    final_loss: float


class IPersonalLoRA(ABC):
    """(2.4.0) On-device personalisation via LoRA fine-tuning (RT-10)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def train_async(
        self,
        adapter_id: str,
        conversation_samples: Sequence[str],
        ct: Optional[object] = None,
    ) -> LoRATrainingSummary:
        ...

    @abstractmethod
    async def load_adapter_async(
        self, adapter_id: str, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def unload_adapter_async(
        self, adapter_id: str, ct: Optional[object] = None
    ) -> None:
        ...
