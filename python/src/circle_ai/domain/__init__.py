"""circle_ai.domain — port of the CircleAI.Domain assembly.

(2.4.0 contracts / 3.3.0 in-memory) Domain-specialist plug points: food
embeddings, finance retrieval + multi-pass agent, presentation generation,
job-search drafting, MemPalace + HippoRAG memory, MiroFish swarm coordination,
and on-device personal-LoRA training — each a contract, a deterministic
in-memory backing, and a fail-safe null default. C# is the exact spec.
"""
from __future__ import annotations

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
    SlideOutline,
    SwarmPeer,
)
from .in_memory_domain import (
    InMemoryFinanceRetrieval,
    InMemoryFoodEmbeddings,
    InMemoryHippoRagStore,
    InMemoryMemPalaceStore,
    InMemoryPersonalLoRA,
    InMemorySwarmCoordinator,
    LoRAAdapterState,
    MultiPassFinancialAgent,
    TemplateJobSearchPipeline,
    TemplatePresentationGenerator,
)
from .null_implementations import (
    NullFinanceRetrieval,
    NullFinancialAgent,
    NullFoodEmbeddings,
    NullHippoRagStore,
    NullJobSearchPipeline,
    NullMemPalaceStore,
    NullPersonalLoRA,
    NullPresentationGenerator,
    NullSwarmCoordinator,
)

__all__ = [
    # records
    "Ingredient",
    "FinanceSnippet",
    "FinanceFinding",
    "SlideOutline",
    "GeneratedPresentation",
    "JobApplicationDraft",
    "MemoryItem",
    "MemoryHit",
    "SwarmPeer",
    "LoRATrainingSummary",
    "LoRAAdapterState",
    # contracts
    "IFoodEmbeddings",
    "IFinanceRetrieval",
    "IFinancialAgent",
    "IPresentationGenerator",
    "IJobSearchPipeline",
    "IMemPalaceStore",
    "IHippoRagStore",
    "ISwarmCoordinator",
    "IPersonalLoRA",
    # in-memory
    "InMemoryFoodEmbeddings",
    "InMemoryFinanceRetrieval",
    "MultiPassFinancialAgent",
    "TemplatePresentationGenerator",
    "TemplateJobSearchPipeline",
    "InMemoryMemPalaceStore",
    "InMemoryHippoRagStore",
    "InMemorySwarmCoordinator",
    "InMemoryPersonalLoRA",
    # null
    "NullFoodEmbeddings",
    "NullFinanceRetrieval",
    "NullFinancialAgent",
    "NullPresentationGenerator",
    "NullJobSearchPipeline",
    "NullMemPalaceStore",
    "NullHippoRagStore",
    "NullSwarmCoordinator",
    "NullPersonalLoRA",
]
