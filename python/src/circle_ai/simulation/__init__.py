"""circle_ai.simulation — port of the CircleAI.Simulation assembly.

Offline network-health simulation over a knowledge graph extracted from episodic
memory:

  * GraphNode / GraphEdge / KnowledgeGraph — the entity-relationship graph.
  * ScenarioKind / SimulationScenario / SimulationOutcome / SimulationResult.
  * IGraphBuilder / EpisodicGraphExtractor — heuristic graph extraction.
  * ISimulationEngine / LocalSimulationEngine / MiroFishAdapter — diffusion engine.
  * NetworkHealthSimulator — build graph from history + run a scenario.
  * ThreatPropagationScenario — build a scenario from a Security AnomalySignal.

C# is the exact spec. The C# ``LocalSimulationEngine`` is ``internal``; it is
exposed here for host composition + tests.
"""
from __future__ import annotations

from .engines import (
    EpisodicGraphExtractor,
    IGraphBuilder,
    ISimulationEngine,
    LocalSimulationEngine,
    MiroFishAdapter,
    NetworkHealthSimulator,
)
from .graph import GraphEdge, GraphNode, KnowledgeGraph
from .scenario import (
    ScenarioKind,
    SimulationOutcome,
    SimulationResult,
    SimulationScenario,
)
from .threat_propagation_scenario import ThreatPropagationScenario

__all__ = [
    "GraphNode",
    "GraphEdge",
    "KnowledgeGraph",
    "ScenarioKind",
    "SimulationScenario",
    "SimulationOutcome",
    "SimulationResult",
    "IGraphBuilder",
    "EpisodicGraphExtractor",
    "ISimulationEngine",
    "LocalSimulationEngine",
    "MiroFishAdapter",
    "NetworkHealthSimulator",
    "ThreatPropagationScenario",
]
