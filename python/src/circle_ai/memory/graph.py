# memory/graph.py
#
# Personal knowledge graph + HippoRAG multi-hop recall (Personalised PageRank).
#
# Ported from CircleAI.Domain (MemoryItem / MemoryHit / IHippoRagStore) and
# CircleAI.Companion (SqliteKnowledgeGraph, SqliteHippoRagStore) — the C#
# reference — and mirrors the TypeScript pilot (memory/graph.ts) and Go port
# (memory_graph.go). This is the in-memory port: identical algorithms, no SQLite.
#
# HippoRAG (Wang et al. 2024): each memory item is a node in the personal KG; at
# recall time the query's entities seed a Personalised PageRank walk, and the
# nodes with the highest steady-state probability are the multi-hop matches.

from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional, Protocol, runtime_checkable


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# Shared recall currency (CircleAI.Domain Contracts)
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class MemoryItem:
    """One recallable memory with optional string metadata."""

    id: str
    text: str
    metadata: Optional[dict[str, str]] = None


@dataclass(frozen=True)
class MemoryHit:
    """A recalled memory paired with its relevance score."""

    item: MemoryItem
    score: float


# ---------------------------------------------------------------------------
# Knowledge-graph node + triple
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class KnowledgeNode:
    """A node in the personal knowledge graph."""

    id: str
    kind: str
    name: str
    properties: Optional[dict[str, str]] = None


@dataclass(frozen=True)
class KnowledgeTriple:
    """One (subject, predicate, object) triple with provenance (source + confidence)."""

    subject: str
    predicate: str
    object: str
    source: Optional[str]
    confidence: float
    recorded_at_utc: datetime = field(default_factory=_utc_now)


@runtime_checkable
class IHippoRagStore(Protocol):
    """HippoRAG-pattern memory + knowledge-graph + Personalised PageRank recall."""

    @property
    def backend_id(self) -> str:
        """Stable identifier for the backend implementation."""
        ...

    async def index_async(self, item: MemoryItem, *, ct: Optional[object] = None) -> None:
        """Ensure the memory item exists as a node the walker can land on."""
        ...

    async def multi_hop_recall_async(
        self, query: str, top_k: int = 5, *, ct: Optional[object] = None
    ) -> list[MemoryHit]:
        """Seed a Personalised PageRank walk from the query's terms; return top-k reached nodes."""
        ...


# ---------------------------------------------------------------------------
# InMemoryKnowledgeGraph
# ---------------------------------------------------------------------------

_TRIPLE_SEP = " "


class InMemoryKnowledgeGraph:
    """In-memory personal knowledge graph.

    Triples are keyed by (subject, predicate, object) — re-adding the same
    triple replaces its provenance, matching the C# SQLite store's
    ``INSERT OR REPLACE`` on the composite primary key.
    """

    def __init__(self) -> None:
        self._nodes: dict[str, KnowledgeNode] = {}
        self._triples: dict[str, KnowledgeTriple] = {}

    def upsert_node(self, node: KnowledgeNode) -> None:
        """Insert or replace a node by id."""
        if node is None or len(node.id.strip()) == 0:
            raise ValueError("node.id required")
        self._nodes[node.id] = node

    def get_node(self, id: str) -> Optional[KnowledgeNode]:
        """Return the node with the given id, or ``None``."""
        return self._nodes.get(id)

    def add_triple(
        self,
        subject: str,
        predicate: str,
        object: str,
        source: Optional[str],
        confidence: float,
    ) -> None:
        """Add (or replace) a triple with full provenance."""
        if not subject or len(subject.strip()) == 0:
            raise ValueError("subject required")
        if not predicate or len(predicate.strip()) == 0:
            raise ValueError("predicate required")
        if not object or len(object.strip()) == 0:
            raise ValueError("object required")
        if confidence < 0 or confidence > 1:
            raise ValueError("confidence must be in [0,1]")

        key = subject + _TRIPLE_SEP + predicate + _TRIPLE_SEP + object
        self._triples[key] = KnowledgeTriple(
            subject=subject,
            predicate=predicate,
            object=object,
            source=source,
            confidence=confidence,
            recorded_at_utc=_utc_now(),
        )

    def all_triples(self) -> list[KnowledgeTriple]:
        """All triples — used by HippoRAG for the graph walk."""
        return list(self._triples.values())

    def read_triples(self, subject: str) -> list[KnowledgeTriple]:
        """Raw triples for one subject (inspection / debugging)."""
        if not subject or len(subject.strip()) == 0:
            raise ValueError("subject required")
        return [t for t in self._triples.values() if t.subject == subject]


# ---------------------------------------------------------------------------
# InMemoryHippoRagStore — Personalised PageRank multi-hop recall
# ---------------------------------------------------------------------------

_TOKEN_SPLIT = re.compile(r"[^A-Za-z0-9]+")


class InMemoryHippoRagStore:
    """Real HippoRAG recall over an :class:`InMemoryKnowledgeGraph`.

    Walks the personal graph via Personalised PageRank (power iteration) seeded
    from the query's terms. Three precision guarantees carried from the C#
    reference:

    1. No query term touches the graph -> returns empty (never fabricates an
       association from arbitrary nodes).
    2. Seed nodes are excluded from results (recall returns the *associated*
       nodes the walk reached, not the query echoed back).
    3. Edge spread is confidence-weighted — a high-confidence edge carries more
       of the walk's mass than a guessed one, so a shaky belief does not steer
       recall like a stated fact.
    """

    def __init__(
        self,
        kg: InMemoryKnowledgeGraph,
        walk_iterations: int = 32,
        damping: float = 0.85,
    ) -> None:
        if kg is None:
            raise ValueError("kg required")
        self._kg = kg
        self._walk_iterations = walk_iterations
        self._damping = damping

    @property
    def backend_id(self) -> str:
        return "inmemory-hippo-ppr"

    async def index_async(self, item: MemoryItem, *, ct: Optional[object] = None) -> None:
        """Register the item (and its metadata) as graph triples."""
        if item is None:
            raise ValueError("item required")
        # The graph is populated by the KnowledgeGraphExtractor — here we just
        # ensure the memory item exists as a node so the walker can land on it.
        self._kg.add_triple(item.id, "memory_text", item.text, item.id, 1.0)
        if item.metadata:
            for k, v in item.metadata.items():
                self._kg.add_triple(item.id, k, v, item.id, 0.9)

    async def multi_hop_recall_async(
        self, query: str, top_k: int = 5, *, ct: Optional[object] = None
    ) -> list[MemoryHit]:
        if not query or len(query.strip()) == 0:
            raise ValueError("query required")
        if top_k <= 0:
            raise ValueError("top_k must be positive")

        triples = self._kg.all_triples()
        if len(triples) == 0:
            return []

        # Adjacency list: subject -> [(object, confidence)].
        outgoing: dict[str, list[tuple[str, float]]] = {}
        all_nodes: set[str] = set()
        for t in triples:
            all_nodes.add(t.subject)
            all_nodes.add(t.object)
            outgoing.setdefault(t.subject, []).append((t.object, t.confidence))

        # Seed the personalisation vector from query terms that appear as nodes.
        query_terms = {
            s.lower() for s in _TOKEN_SPLIT.split(query) if len(s) > 0
        }
        seed_nodes = [n for n in all_nodes if n.lower() in query_terms]
        # Precision guarantee 1: no genuine association -> return nothing.
        if len(seed_nodes) == 0:
            return []

        rank: dict[str, float] = {n: 0.0 for n in all_nodes}
        seed_mass = 1.0 / len(seed_nodes)
        for s in seed_nodes:
            rank[s] = seed_mass

        # Power-iteration Personalised PageRank.
        for _ in range(self._walk_iterations):
            nxt: dict[str, float] = {n: 0.0 for n in all_nodes}

            # Random-jump component (personalisation): mass returns to the seeds.
            for seed in seed_nodes:
                nxt[seed] += (1 - self._damping) * seed_mass

            # Walk component.
            for node, mass in rank.items():
                if mass <= 0:
                    continue
                nbrs = outgoing.get(node)
                if not nbrs or len(nbrs) == 0:
                    # Dangling node: redistribute via personalisation.
                    for seed in seed_nodes:
                        nxt[seed] += (self._damping * mass) / len(seed_nodes)
                    continue
                # Precision guarantee 3: confidence-weighted spread. With equal
                # confidences this reduces to the plain 1/count split.
                total_conf = 0.0
                for _nbr, conf in nbrs:
                    total_conf += conf
                for nbr, conf in nbrs:
                    weight = (conf / total_conf) if total_conf > 0 else (1.0 / len(nbrs))
                    nxt[nbr] += self._damping * mass * weight

            rank = nxt

        # Precision guarantee 2: exclude the seeds — they are the query's own terms.
        seed_set = set(seed_nodes)
        ranked = [
            (key, value)
            for key, value in rank.items()
            if value > 0 and key not in seed_set
        ]
        ranked.sort(key=lambda kv: kv[1], reverse=True)

        hits: list[MemoryHit] = []
        for key, value in ranked[:top_k]:
            node = self._kg.get_node(key)
            item = MemoryItem(
                id=key,
                text=node.name if node is not None else key,
                metadata=node.properties if node is not None else None,
            )
            hits.append(MemoryHit(item=item, score=value))
        return hits
