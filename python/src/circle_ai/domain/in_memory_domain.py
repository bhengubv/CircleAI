# in_memory_domain.py
#
# Port of CircleAI.Domain InMemoryDomain.cs (C# — the EXACT spec).
#
# (3.3.0) Real-but-lightweight in-memory backings for every CircleAI.Domain
# contract — the deterministic in-process fallbacks production hosts swap out
# one-by-one as real specialists get vendored.
#
# Notes on faithful porting:
#   • InMemoryFoodEmbeddings.embed_async: when no embedding is registered the C#
#     builds an 8-dim vector from `name.GetHashCode(OrdinalIgnoreCase)` nibbles.
#     .NET's string hash is randomised per-process (not portable), so this port
#     uses a fixed FNV-1a over the lower-cased name — same shape (8 floats in
#     [0,1] from 4-bit nibbles), deterministic across runs. float32 at the /15f.
#   • MultiPassFinancialAgent: decompose on " and " + long-question leading
#     clause, dedup preserving order, retrieve per sub-question, group by source,
#     summarise the top-3-by-score snippets " | "-joined.
#   • InMemoryPersonalLoRA: simulated training loss
#     1/(1+ln(1+steps)) + 1/(1+totalChars/1000) as float32.
#   • dict iteration order == insertion order in both CPython and C#
#     ConcurrentDictionary snapshots for these read-all paths.

from __future__ import annotations

import math
import struct
import threading
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional, Sequence

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


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


def _stable_hash(text: str) -> int:
    # Deterministic 32-bit FNV-1a over the lower-cased text (stand-in for the
    # per-process-randomised .NET string GetHashCode used by the C# fallback).
    h = 2166136261
    for b in text.lower().encode("utf-8"):
        h ^= b
        h = (h * 16777619) & 0xFFFFFFFF
    return h


# ─── Food (substitute-by-canonical-name) ───────────────────────────────
class InMemoryFoodEmbeddings(IFoodEmbeddings):
    """Real in-memory :class:`IFoodEmbeddings`. Mirrors
    ``CircleAI.Domain.InMemoryFoodEmbeddings``."""

    def __init__(self) -> None:
        self._embeds: Dict[str, List[float]] = {}
        self._subs: Dict[str, List[Ingredient]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register_embedding(self, name: str, v: List[float]) -> None:
        if v is None:
            raise ValueError("v")
        with self._lock:
            self._embeds[name.lower()] = list(v)

    def register_substitute(self, name: str, alt: Ingredient) -> None:
        if alt is None:
            raise ValueError("alt")
        with self._lock:
            self._subs.setdefault(name.lower(), []).append(alt)

    async def embed_async(
        self, ingredient: Ingredient, ct: Optional[object] = None
    ) -> List[float]:
        if ingredient is None:
            raise ValueError("i")
        with self._lock:
            got = self._embeds.get(ingredient.name.lower())
        if got is not None:
            return list(got)
        # Deterministic hash-based 8-dim vector if no embedding was registered.
        v2 = [0.0] * 8
        h = _stable_hash(ingredient.name)
        for k in range(8):
            v2[k] = _f32(((h >> (k * 4)) & 0xF) / 15.0)
        return v2

    async def substitutes_async(
        self, ingredient: Ingredient, top_k: int = 5, ct: Optional[object] = None
    ) -> List[Ingredient]:
        if ingredient is None:
            raise ValueError("i")
        if top_k <= 0:
            raise ValueError("topK")
        with self._lock:
            got = self._subs.get(ingredient.name.lower())
            if got is None:
                return []
            return list(got[:top_k])


# ─── Finance ───────────────────────────────────────────────────────────
class InMemoryFinanceRetrieval(IFinanceRetrieval):
    """Real in-memory :class:`IFinanceRetrieval`. Mirrors
    ``CircleAI.Domain.InMemoryFinanceRetrieval``."""

    def __init__(self) -> None:
        self._corpus: List[FinanceSnippet] = []
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def add(self, s: FinanceSnippet) -> None:
        if s is None:
            raise ValueError("s")
        with self._lock:
            self._corpus.append(s)

    async def retrieve_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[FinanceSnippet]:
        if query is None:
            raise ValueError("query")
        if top_k <= 0:
            raise ValueError("topK")
        ql = query.lower()
        with self._lock:
            hits = [s for s in self._corpus if ql in s.text.lower()]
        hits.sort(key=lambda s: s.score, reverse=True)
        return hits[:top_k]


class MultiPassFinancialAgent(IFinancialAgent):
    """Real financial agent — multi-pass retrieval + per-source summaries.
    Mirrors ``CircleAI.Domain.MultiPassFinancialAgent``."""

    def __init__(self, r: IFinanceRetrieval) -> None:
        if r is None:
            raise ValueError("r")
        self._retr = r

    @property
    def backend_id(self) -> str:
        return "multi-pass"

    async def research_async(
        self, question: str, ct: Optional[object] = None
    ) -> List[FinanceFinding]:
        if question is None:
            raise ValueError("question")
        sub_questions = self._decompose(question)
        findings: List[FinanceFinding] = []
        for sub in sub_questions:
            snippets = await self._retr.retrieve_async(sub, 5, ct)
            if len(snippets) == 0:
                continue
            # GroupBy(Source) — preserve first-seen source order.
            by_source: Dict[str, List[FinanceSnippet]] = {}
            for s in snippets:
                by_source.setdefault(s.source, []).append(s)
            for source, grp in by_source.items():
                top3 = sorted(grp, key=lambda s: s.score, reverse=True)[:3]
                summary = " | ".join(s.text for s in top3)
                findings.append(
                    FinanceFinding(subject=sub, summary=summary, citations=[source])
                )
        return findings

    @staticmethod
    def _decompose(question: str) -> List[str]:
        subs: List[str] = [question]
        if " and " in question.lower():
            # Split case-insensitively on " and " (mirror the C# split on the
            # literal " and " token; .NET Split is ordinal here).
            for part in _split_ci(question, " and "):
                if len(part.strip()) > 6:
                    subs.append(part.strip())
        if len(question) > 60:
            subs.append(question.split(",")[0].strip())
        # Distinct preserving order.
        seen = set()
        out: List[str] = []
        for s in subs:
            if s not in seen:
                seen.add(s)
                out.append(s)
        return out


def _split_ci(text: str, sep: str) -> List[str]:
    # Case-insensitive split on `sep`, RemoveEmptyEntries.
    parts: List[str] = []
    low = text.lower()
    seplow = sep.lower()
    start = 0
    while True:
        idx = low.find(seplow, start)
        if idx < 0:
            piece = text[start:]
            if piece:
                parts.append(piece)
            break
        piece = text[start:idx]
        if piece:
            parts.append(piece)
        start = idx + len(sep)
    return parts


# ─── Presentations ──────────────────────────────────────────────────────
class TemplatePresentationGenerator(IPresentationGenerator):
    """Template presentation generator. Mirrors
    ``CircleAI.Domain.TemplatePresentationGenerator``."""

    @property
    def backend_id(self) -> str:
        return "template"

    async def generate_async(
        self,
        topic: str,
        target_slide_count: int = 10,
        theme: Optional[str] = None,
        ct: Optional[object] = None,
    ) -> GeneratedPresentation:
        if topic is None or topic.strip() == "":
            raise ValueError("topic required")
        if target_slide_count <= 0:
            raise ValueError("targetSlideCount")
        slides: List[SlideOutline] = []
        slides.append(
            SlideOutline(
                topic,
                "Overview",
                ["What is " + topic, "Why it matters", "What we'll cover"],
            )
        )
        for i in range(2, target_slide_count):
            slides.append(
                SlideOutline(
                    f"{topic} — Part {i - 1}",
                    f"Detail for part {i - 1}",
                    ["Point A", "Point B", "Point C"],
                )
            )
        slides.append(
            SlideOutline(
                "Conclusion", f"Summary of {topic}", ["Recap", "Next steps", "Questions"]
            )
        )
        return GeneratedPresentation(slides, theme if theme is not None else "default", "markdown")


# ─── Job search ─────────────────────────────────────────────────────────
class TemplateJobSearchPipeline(IJobSearchPipeline):
    """Template job-search pipeline. Mirrors
    ``CircleAI.Domain.TemplateJobSearchPipeline``."""

    @property
    def backend_id(self) -> str:
        return "template"

    async def draft_application_async(
        self,
        role_description: str,
        candidate_profile_text: str,
        ct: Optional[object] = None,
    ) -> JobApplicationDraft:
        if role_description is None:
            raise ValueError("roleDescription")
        if candidate_profile_text is None:
            raise ValueError("candidateProfileText")
        role_words = self._extract_key_words(role_description)
        cand_words = set(self._extract_key_words(candidate_profile_text))
        # Intersect preserving role-word order, take 10.
        matches: List[str] = []
        for w in role_words:
            if w in cand_words and w not in matches:
                matches.append(w)
            if len(matches) == 10:
                break
        resume = f"{candidate_profile_text.strip()}\n\nMatched skills: {', '.join(matches)}"
        cover = (
            "Dear Hiring Team,\n\nI am applying because my background "
            f"({', '.join(matches[:3])}) fits the role.\n\nRegards."
        )
        return JobApplicationDraft(resume, cover, matches)

    @staticmethod
    def _extract_key_words(text: str) -> List[str]:
        seps = [" ", "\n", "\r", "\t", ",", ".", ";", ":", "(", ")"]
        raw = [text]
        for sep in seps:
            nxt: List[str] = []
            for chunk in raw:
                nxt.extend(chunk.split(sep))
            raw = nxt
        out: List[str] = []
        seen = set()
        for w in raw:
            if len(w) > 3:
                lw = w.strip().lower()
                if lw not in seen:
                    seen.add(lw)
                    out.append(lw)
        return out


# ─── Memory upgrades ────────────────────────────────────────────────────
class InMemoryMemPalaceStore(IMemPalaceStore):
    """Real in-memory :class:`IMemPalaceStore`. Mirrors
    ``CircleAI.Domain.InMemoryMemPalaceStore``."""

    def __init__(self) -> None:
        self._items: Dict[str, MemoryItem] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def upsert_async(
        self, item: MemoryItem, ct: Optional[object] = None
    ) -> None:
        if item is None:
            raise ValueError("item")
        if item.id is None or item.id.strip() == "":
            raise ValueError("Id required")
        with self._lock:
            self._items[item.id] = item

    async def recall_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[MemoryHit]:
        if query is None:
            raise ValueError("query")
        if top_k <= 0:
            raise ValueError("topK")
        with self._lock:
            items = list(self._items.values())
        hits = [MemoryHit(i, self._score(i.text, query)) for i in items]
        hits = [h for h in hits if h.score > 0]
        hits.sort(key=lambda h: h.score, reverse=True)
        return hits[:top_k]

    @staticmethod
    def _score(body: str, query: str) -> float:
        if not body or not query:
            return 0.0
        q = query.strip()
        idx = body.lower().find(q.lower())
        return 0.0 if idx < 0 else _f32(1.0 / (1.0 + idx))


class InMemoryHippoRagStore(IHippoRagStore):
    """Real in-memory :class:`IHippoRagStore` (multi-hop over a MemPalace).
    Mirrors ``CircleAI.Domain.InMemoryHippoRagStore``."""

    def __init__(self) -> None:
        self._base = InMemoryMemPalaceStore()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def index_async(
        self, item: MemoryItem, ct: Optional[object] = None
    ) -> None:
        await self._base.upsert_async(item, ct)

    async def multi_hop_recall_async(
        self, query: str, top_k: int = 5, ct: Optional[object] = None
    ) -> List[MemoryHit]:
        first = await self._base.recall_async(query, top_k, ct)
        if len(first) == 0:
            return first
        seed = first[0].item.text
        second = await self._base.recall_async(seed, top_k, ct)
        # Concat, GroupBy(Item.Id).First(), OrderByDescending(Score), Take(topK).
        merged: Dict[str, MemoryHit] = {}
        for h in list(first) + list(second):
            if h.item.id not in merged:
                merged[h.item.id] = h
        out = list(merged.values())
        out.sort(key=lambda h: h.score, reverse=True)
        return out[:top_k]


# ─── Swarm ──────────────────────────────────────────────────────────────
class InMemorySwarmCoordinator(ISwarmCoordinator):
    """Real in-memory :class:`ISwarmCoordinator`. Mirrors
    ``CircleAI.Domain.InMemorySwarmCoordinator``."""

    def __init__(self) -> None:
        self._peers: Dict[str, SwarmPeer] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register(self, p: SwarmPeer) -> None:
        if p is None:
            raise ValueError("p")
        with self._lock:
            self._peers[p.peer_id] = p

    async def list_peers_async(
        self, ct: Optional[object] = None
    ) -> List[SwarmPeer]:
        with self._lock:
            return list(self._peers.values())

    async def choose_delegate_async(
        self, capability: str, ct: Optional[object] = None
    ) -> Optional[str]:
        if capability is None or capability.strip() == "":
            raise ValueError("capability required")
        cl = capability.lower()
        with self._lock:
            candidates = [p for p in self._peers.values() if p.capability.lower() == cl]
        if not candidates:
            return None
        # OrderByDescending(Health).FirstOrDefault() — stable; keep first on ties.
        pick = max(candidates, key=lambda p: p.health)
        # max() returns the first max on ties (matches OrderByDescending+First).
        best_health = pick.health
        for p in candidates:
            if p.health == best_health:
                return p.peer_id
        return pick.peer_id


# ─── Personal LoRA ──────────────────────────────────────────────────────
@dataclass(frozen=True, slots=True)
class LoRAAdapterState:
    """Mirrors ``CircleAI.Domain.LoRAAdapterState`` — ``record(string AdapterId,
    int Steps, float FinalLoss, DateTimeOffset TrainedAtUtc)``.
    """

    adapter_id: str
    steps: int
    final_loss: float
    trained_at_utc: datetime


class InMemoryPersonalLoRA(IPersonalLoRA):
    """Real in-memory :class:`IPersonalLoRA` with a simulated training loop.
    Mirrors ``CircleAI.Domain.InMemoryPersonalLoRA``."""

    def __init__(self) -> None:
        self._adapters: Dict[str, LoRAAdapterState] = {}
        self._loaded: Dict[str, int] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def train_async(
        self,
        adapter_id: str,
        conversation_samples: Sequence[str],
        ct: Optional[object] = None,
    ) -> LoRATrainingSummary:
        if adapter_id is None or adapter_id.strip() == "":
            raise ValueError("adapterId required")
        if conversation_samples is None:
            raise ValueError("samples")
        samples = list(conversation_samples)
        if len(samples) == 0:
            raise ValueError("at least one sample required")
        steps = len(samples)
        total_chars = sum(len(s) if s is not None else 0 for s in samples)
        final_loss = _f32(
            1.0 / (1.0 + math.log(1 + steps)) + 1.0 / (1.0 + total_chars / 1000.0)
        )
        state = LoRAAdapterState(adapter_id, steps, final_loss, datetime.now(timezone.utc))
        with self._lock:
            self._adapters[adapter_id] = state
        return LoRATrainingSummary(adapter_id, steps, final_loss)

    async def load_adapter_async(
        self, adapter_id: str, ct: Optional[object] = None
    ) -> None:
        if adapter_id is None or adapter_id.strip() == "":
            raise ValueError("adapterId required")
        with self._lock:
            if adapter_id not in self._adapters:
                raise RuntimeError(f"Adapter '{adapter_id}' not trained.")
            self._loaded[adapter_id] = 1

    async def unload_adapter_async(
        self, adapter_id: str, ct: Optional[object] = None
    ) -> None:
        if adapter_id is None or adapter_id.strip() == "":
            raise ValueError("adapterId required")
        with self._lock:
            self._loaded.pop(adapter_id, None)

    def is_loaded(self, adapter_id: str) -> bool:
        with self._lock:
            return adapter_id in self._loaded

    def state_of(self, adapter_id: str) -> Optional[LoRAAdapterState]:
        with self._lock:
            return self._adapters.get(adapter_id)
