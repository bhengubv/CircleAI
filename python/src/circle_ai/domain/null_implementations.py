# null_implementations.py
#
# Port of CircleAI.Domain NullImplementations.cs (C# — the EXACT spec).
#
# (2.4.0) Fail-safe defaults for every Domain contract. Each exposes a singleton
# `INSTANCE` mirroring the C# `static readonly ... Instance`.

from __future__ import annotations

from typing import List, Optional, Sequence

from .contracts import (
    FinanceFinding,
    FinanceSnippet,
    GeneratedPresentation,
    IFinancialAgent,
    IFinanceRetrieval,
    IFoodEmbeddings,
    IHippoRagStore,
    IJobSearchPipeline,
    IMemPalaceStore,
    IPersonalLoRA,
    IPresentationGenerator,
    ISwarmCoordinator,
    Ingredient,
    JobApplicationDraft,
    LoRATrainingSummary,
    MemoryHit,
    MemoryItem,
    SwarmPeer,
)


class NullFoodEmbeddings(IFoodEmbeddings):
    INSTANCE: "NullFoodEmbeddings"

    @property
    def backend_id(self) -> str:
        return "null"

    async def embed_async(
        self, ingredient: Ingredient, ct: Optional[object] = None
    ) -> List[float]:
        return [0.0] * 300

    async def substitutes_async(
        self, ingredient: Ingredient, top_k: int = 5, ct: Optional[object] = None
    ) -> List[Ingredient]:
        return []


class NullFinanceRetrieval(IFinanceRetrieval):
    INSTANCE: "NullFinanceRetrieval"

    @property
    def backend_id(self) -> str:
        return "null"

    async def retrieve_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[FinanceSnippet]:
        return []


class NullFinancialAgent(IFinancialAgent):
    INSTANCE: "NullFinancialAgent"

    @property
    def backend_id(self) -> str:
        return "null"

    async def research_async(
        self, question: str, ct: Optional[object] = None
    ) -> List[FinanceFinding]:
        return []


class NullPresentationGenerator(IPresentationGenerator):
    INSTANCE: "NullPresentationGenerator"

    @property
    def backend_id(self) -> str:
        return "null"

    async def generate_async(
        self,
        topic: str,
        target_slide_count: int = 10,
        theme: Optional[str] = None,
        ct: Optional[object] = None,
    ) -> GeneratedPresentation:
        return GeneratedPresentation([], theme if theme is not None else "default", "json")


class NullJobSearchPipeline(IJobSearchPipeline):
    INSTANCE: "NullJobSearchPipeline"

    @property
    def backend_id(self) -> str:
        return "null"

    async def draft_application_async(
        self,
        role_description: str,
        candidate_profile_text: str,
        ct: Optional[object] = None,
    ) -> JobApplicationDraft:
        return JobApplicationDraft("", "", [])


class NullMemPalaceStore(IMemPalaceStore):
    INSTANCE: "NullMemPalaceStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def upsert_async(
        self, item: MemoryItem, ct: Optional[object] = None
    ) -> None:
        return None

    async def recall_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[MemoryHit]:
        return []


class NullHippoRagStore(IHippoRagStore):
    INSTANCE: "NullHippoRagStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def index_async(
        self, item: MemoryItem, ct: Optional[object] = None
    ) -> None:
        return None

    async def multi_hop_recall_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[MemoryHit]:
        return []


class NullSwarmCoordinator(ISwarmCoordinator):
    INSTANCE: "NullSwarmCoordinator"

    @property
    def backend_id(self) -> str:
        return "null"

    async def list_peers_async(
        self, ct: Optional[object] = None
    ) -> List[SwarmPeer]:
        return []

    async def choose_delegate_async(
        self, capability: str, ct: Optional[object] = None
    ) -> Optional[str]:
        return None


class NullPersonalLoRA(IPersonalLoRA):
    INSTANCE: "NullPersonalLoRA"

    @property
    def backend_id(self) -> str:
        return "null"

    async def train_async(
        self,
        adapter_id: str,
        conversation_samples: Sequence[str],
        ct: Optional[object] = None,
    ) -> LoRATrainingSummary:
        return LoRATrainingSummary(adapter_id, 0, 0.0)

    async def load_adapter_async(
        self, adapter_id: str, ct: Optional[object] = None
    ) -> None:
        return None

    async def unload_adapter_async(
        self, adapter_id: str, ct: Optional[object] = None
    ) -> None:
        return None


NullFoodEmbeddings.INSTANCE = NullFoodEmbeddings()
NullFinanceRetrieval.INSTANCE = NullFinanceRetrieval()
NullFinancialAgent.INSTANCE = NullFinancialAgent()
NullPresentationGenerator.INSTANCE = NullPresentationGenerator()
NullJobSearchPipeline.INSTANCE = NullJobSearchPipeline()
NullMemPalaceStore.INSTANCE = NullMemPalaceStore()
NullHippoRagStore.INSTANCE = NullHippoRagStore()
NullSwarmCoordinator.INSTANCE = NullSwarmCoordinator()
NullPersonalLoRA.INSTANCE = NullPersonalLoRA()
