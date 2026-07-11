# graph.py
#
# Port of CircleAI.Simulation GraphNode.cs / GraphEdge.cs / KnowledgeGraph.cs
# (C# — the EXACT spec).
#
#   • GraphNode — entity node (person/topic/app/event/system) + Create factory.
#   • GraphEdge — directed weighted edge (weight clamped to [0,1]) + Create.
#   • KnowledgeGraph — in-memory entity–relationship graph: add/replace nodes+edges
#     (last-write wins on id), incident-edge query, BFS reachability, merge.
#
# GraphNode/GraphEdge are fixture-validated records -> frozen slotted dataclasses.
# Guid -> uuid.UUID. DateTimeOffset -> datetime. Weight is float32 (struct pack).
# BFS uses a FIFO queue and adds-to-visited on dequeue, matching the C# order.

from __future__ import annotations

import struct
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Mapping, Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


def _clamp(x: float, lo: float, hi: float) -> float:
    return lo if x < lo else (hi if x > hi else x)


@dataclass(frozen=True, slots=True)
class GraphNode:
    """Mirrors ``CircleAI.Simulation.GraphNode`` — ``record(Guid Id, string Label,
    string Kind, IReadOnlyDictionary<string,string> Properties,
    DateTimeOffset ExtractedAt)``.
    """

    id: uuid.UUID
    label: str
    kind: str
    properties: Mapping[str, str]
    extracted_at: datetime

    @staticmethod
    def create(
        label: str, kind: str, properties: Optional[Mapping[str, str]] = None
    ) -> "GraphNode":
        """New node with a generated id + current UTC. Mirrors ``GraphNode.Create``."""
        return GraphNode(
            uuid.uuid4(), label, kind, dict(properties) if properties else {}, _utc_now()
        )


@dataclass(frozen=True, slots=True)
class GraphEdge:
    """Mirrors ``CircleAI.Simulation.GraphEdge`` — ``record(Guid Id, Guid SourceId,
    Guid TargetId, string Relation, float Weight, DateTimeOffset CreatedAt)``.
    """

    id: uuid.UUID
    source_id: uuid.UUID
    target_id: uuid.UUID
    relation: str
    weight: float
    created_at: datetime

    @staticmethod
    def create(
        source_id: uuid.UUID,
        target_id: uuid.UUID,
        relation: str,
        weight: float = 1.0,
    ) -> "GraphEdge":
        """New edge with a generated id + current UTC; weight clamped to [0,1].
        Mirrors ``GraphEdge.Create``."""
        return GraphEdge(
            uuid.uuid4(),
            source_id,
            target_id,
            relation,
            _f32(_clamp(weight, 0.0, 1.0)),
            _utc_now(),
        )


class KnowledgeGraph:
    """In-memory entity–relationship graph. Mirrors
    ``CircleAI.Simulation.KnowledgeGraph``."""

    def __init__(self) -> None:
        self._nodes: Dict[uuid.UUID, GraphNode] = {}
        self._edges: Dict[uuid.UUID, GraphEdge] = {}

    @property
    def nodes(self) -> Mapping[uuid.UUID, GraphNode]:
        return self._nodes

    @property
    def edges(self) -> Mapping[uuid.UUID, GraphEdge]:
        return self._edges

    def add_node(self, node: GraphNode) -> None:
        if node is None:
            raise ValueError("node")
        self._nodes[node.id] = node

    def add_edge(self, edge: GraphEdge) -> None:
        if edge is None:
            raise ValueError("edge")
        self._edges[edge.id] = edge

    def edges_for(self, node_id: uuid.UUID) -> List[GraphEdge]:
        """All edges where ``node_id`` is the source or target."""
        return [e for e in self._edges.values() if e.source_id == node_id or e.target_id == node_id]

    def reachable_from(self, start_id: uuid.UUID) -> List[GraphNode]:
        """All nodes reachable from ``start_id`` by BFS (including the start)."""
        visited = set()
        queue: List[uuid.UUID] = [start_id]
        result: List[GraphNode] = []
        head = 0
        while head < len(queue):
            current = queue[head]
            head += 1
            if current in visited:
                continue
            visited.add(current)
            node = self._nodes.get(current)
            if node is not None:
                result.append(node)
            for edge in self.edges_for(current):
                nxt = edge.target_id if edge.source_id == current else edge.source_id
                if nxt not in visited:
                    queue.append(nxt)
        return result

    def merge(self, other: "KnowledgeGraph") -> None:
        """Merge another graph's nodes + edges (last-write wins on id)."""
        if other is None:
            raise ValueError("other")
        for n in other._nodes.values():
            self._nodes[n.id] = n
        for e in other._edges.values():
            self._edges[e.id] = e
