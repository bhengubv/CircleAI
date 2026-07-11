# engines.py
#
# Port of CircleAI.Simulation ISimulationEngine.cs / IGraphBuilder.cs /
# EpisodicGraphExtractor.cs / NetworkHealthSimulator.cs (LocalSimulationEngine) /
# MiroFishAdapter.cs (C# — the EXACT spec).
#
#   • ISimulationEngine / IGraphBuilder — the contracts.
#   • EpisodicGraphExtractor — offline heuristic graph extraction from episodic
#     memory (event nodes + app/topic nodes + temporal "followed_by" edges).
#   • LocalSimulationEngine — deterministic graph-diffusion health forecast.
#   • MiroFishAdapter — prefers an external engine, else the local fallback.
#   • NetworkHealthSimulator — build graph from history, run scenario.
#
# Float sites (health decay, weight compares) use float32 (struct pack) to match
# the C# `float` arithmetic. The C# `HashSet<string> highImpact` has unspecified
# iteration order; this port preserves first-insertion order (a dict) so findings
# are deterministic across runs.

from __future__ import annotations

import struct
from abc import ABC, abstractmethod
from datetime import datetime, timezone
from typing import Dict, List, Optional, Sequence

from ..memory.episodic_memory import EpisodicMemoryEntry
from .graph import GraphEdge, GraphNode, KnowledgeGraph
from .scenario import SimulationOutcome, SimulationResult, SimulationScenario


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


class ISimulationEngine(ABC):
    """Runs a scenario against a knowledge graph. Mirrors
    ``CircleAI.Simulation.ISimulationEngine``."""

    @abstractmethod
    async def run_async(
        self,
        scenario: SimulationScenario,
        graph: KnowledgeGraph,
        ct: Optional[object] = None,
    ) -> SimulationResult:
        ...


class IGraphBuilder(ABC):
    """Builds a :class:`KnowledgeGraph` from episodic memory. Mirrors
    ``CircleAI.Simulation.IGraphBuilder``."""

    @abstractmethod
    def build(self, entries: Sequence[EpisodicMemoryEntry]) -> KnowledgeGraph:
        ...


class EpisodicGraphExtractor(IGraphBuilder):
    """Offline heuristic graph extractor. Mirrors
    ``CircleAI.Simulation.EpisodicGraphExtractor``."""

    def build(self, entries: Sequence[EpisodicMemoryEntry]) -> KnowledgeGraph:
        if entries is None:
            raise ValueError("entries")
        graph = KnowledgeGraph()
        app_nodes: Dict[str, GraphNode] = {}
        topic_nodes: Dict[str, GraphNode] = {}
        prev: Optional[GraphNode] = None
        prev_time: Optional[datetime] = None

        for entry in sorted(entries, key=lambda e: e.recorded_at_utc):
            label = entry.user_text[:60] if len(entry.user_text) > 60 else entry.user_text
            ev_node = GraphNode.create(label, "event", {"episode_id": str(entry.id)})
            graph.add_node(ev_node)

            # App context -> node + edge
            if entry.app_context is not None and entry.app_context.strip() != "":
                key = entry.app_context.lower()
                app_node = app_nodes.get(key)
                if app_node is None:
                    app_node = GraphNode.create(entry.app_context, "app")
                    app_nodes[key] = app_node
                    graph.add_node(app_node)
                graph.add_edge(GraphEdge.create(ev_node.id, app_node.id, "occurred_in"))

            # Tags -> topic nodes + edges
            if entry.tags is not None:
                for tag in entry.tags.keys():
                    tkey = tag.lower()
                    topic_node = topic_nodes.get(tkey)
                    if topic_node is None:
                        topic_node = GraphNode.create(tag, "topic")
                        topic_nodes[tkey] = topic_node
                        graph.add_node(topic_node)
                    graph.add_edge(GraphEdge.create(ev_node.id, topic_node.id, "tagged_with"))

            # Temporal sequence — connect to previous event if within 1 hour
            if prev is not None and prev_time is not None:
                delta_hours = (entry.recorded_at_utc - prev_time).total_seconds() / 3600.0
                if delta_hours <= 1.0:
                    graph.add_edge(GraphEdge.create(prev.id, ev_node.id, "followed_by", 0.5))

            prev = ev_node
            prev_time = entry.recorded_at_utc

        return graph


class LocalSimulationEngine(ISimulationEngine):
    """Deterministic graph-diffusion engine. Mirrors the internal
    ``CircleAI.Simulation.LocalSimulationEngine``."""

    _DECAY_PER_STEP = _f32(0.01)
    _HIGH_IMPACT_THRESHOLD = _f32(0.7)

    async def run_async(
        self,
        scenario: SimulationScenario,
        graph: KnowledgeGraph,
        ct: Optional[object] = None,
    ) -> SimulationResult:
        health = _f32(1.0)
        # HashSet<string> in C#; dict preserves first-insertion order deterministically.
        high_impact: Dict[str, None] = {}

        step = 0
        while step < scenario.step_count and health > 0.0:
            for edge in graph.edges.values():
                health = _f32(health - _f32(_f32(1.0 - edge.weight) * self._DECAY_PER_STEP))
                if edge.weight >= self._HIGH_IMPACT_THRESHOLD:
                    src = graph.nodes.get(edge.source_id)
                    if src is not None:
                        high_impact.setdefault(src.label, None)
            step += 1

        health = _f32(_clamp(health, 0.0, 1.0))

        if health >= 0.8:
            outcome = SimulationOutcome.HEALTHY
        elif health >= 0.5:
            outcome = SimulationOutcome.DEGRADED
        elif health >= 0.2:
            outcome = SimulationOutcome.CRITICAL
        else:
            outcome = SimulationOutcome.UNKNOWN

        if len(high_impact) > 0:
            findings: List[str] = [f"High-impact node detected: {label}" for label in high_impact]
        else:
            findings = ["No high-impact nodes detected."]

        if outcome in (SimulationOutcome.DEGRADED, SimulationOutcome.CRITICAL):
            recs: List[str] = [
                "Review high-weight edges before deployment.",
                "Consider incremental rollout.",
            ]
        else:
            recs = ["Network health nominal — proceed with deployment."]

        return SimulationResult(
            scenario.id, outcome, health, findings, recs, scenario.step_count, _utc_now()
        )


def _clamp(x: float, lo: float, hi: float) -> float:
    return lo if x < lo else (hi if x > hi else x)


class MiroFishAdapter(ISimulationEngine):
    """Adapter for the MiroFish engine (falls back to the local engine). Mirrors
    ``CircleAI.Simulation.MiroFishAdapter``."""

    def __init__(self, external_engine: Optional[ISimulationEngine] = None) -> None:
        self._inner = external_engine if external_engine is not None else LocalSimulationEngine()

    async def run_async(
        self,
        scenario: SimulationScenario,
        graph: KnowledgeGraph,
        ct: Optional[object] = None,
    ) -> SimulationResult:
        return await self._inner.run_async(scenario, graph, ct)


class NetworkHealthSimulator:
    """Offline network-health simulator. Mirrors
    ``CircleAI.Simulation.NetworkHealthSimulator``."""

    def __init__(
        self,
        extractor: Optional[IGraphBuilder] = None,
        engine: Optional[ISimulationEngine] = None,
    ) -> None:
        self._extractor = extractor if extractor is not None else EpisodicGraphExtractor()
        self._engine = engine if engine is not None else MiroFishAdapter()

    async def forecast_async(
        self,
        history: Sequence[EpisodicMemoryEntry],
        scenario: SimulationScenario,
        ct: Optional[object] = None,
    ) -> SimulationResult:
        if history is None:
            raise ValueError("history")
        if scenario is None:
            raise ValueError("scenario")
        graph = self._extractor.build(history)
        return await self._engine.run_async(scenario, graph, ct)
