//! simulation — CircleAI.Simulation (Rust port).
//!
//! Port of the simple entity–relationship graph + offline network-health
//! forecaster. This is DISTINCT from the HippoRAG memory graph in
//! [`crate::memory::graph`]: this graph is a flat, in-memory node/edge store
//! built from episodic memory by keyword/tag heuristics, and the simulator runs
//! a deterministic diffusion model over it.
//!
//! Ports:
//!   - `CircleAI.Simulation.GraphNode`      → [`GraphNode`]
//!   - `CircleAI.Simulation.GraphEdge`      → [`GraphEdge`]
//!   - `CircleAI.Simulation.KnowledgeGraph` → [`KnowledgeGraph`] (BFS
//!     `reachable_from`, `merge`, `edges_for`)
//!   - `CircleAI.Simulation.SimulationScenario` / `ScenarioKind` → [`SimulationScenario`]
//!   - `CircleAI.Simulation.SimulationResult` / `SimulationOutcome` → [`SimulationResult`]
//!   - `CircleAI.Simulation.IGraphBuilder`  → [`IGraphBuilder`]
//!   - `CircleAI.Simulation.ISimulationEngine` → [`ISimulationEngine`]
//!   - `CircleAI.Simulation.EpisodicGraphExtractor` → [`EpisodicGraphExtractor`]
//!   - `CircleAI.Simulation.NetworkHealthSimulator` → [`NetworkHealthSimulator`]
//!   - internal `LocalSimulationEngine` → [`LocalSimulationEngine`]
//!
//! Async/`CancellationToken` seams collapse to synchronous calls per crate
//! convention (the diffusion math is pure and CPU-bound); the `IGraphBuilder` /
//! `ISimulationEngine` seams remain so tests can inject fakes.

use std::collections::{HashMap, HashSet, VecDeque};

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::memory::EpisodicMemoryEntry;

// ─────────────────────────────────────────────────────────────────────────────
// GraphNode / GraphEdge
// ─────────────────────────────────────────────────────────────────────────────

/// A node in the Circle AI knowledge graph — any entity extracted from episodic
/// memory (person, topic, app, event, system). Mirrors `GraphNode`.
#[derive(Debug, Clone, PartialEq)]
pub struct GraphNode {
    pub id: Uuid,
    /// Canonical entity label.
    pub label: String,
    /// `"person" | "topic" | "app" | "event" | "system"`.
    pub kind: String,
    /// Arbitrary key-value metadata.
    pub properties: HashMap<String, String>,
    pub extracted_at: DateTime<Utc>,
}

impl GraphNode {
    /// Creates a node with a fresh id and the current UTC timestamp. Mirrors
    /// `GraphNode.Create`.
    pub fn create(
        label: impl Into<String>,
        kind: impl Into<String>,
        properties: Option<HashMap<String, String>>,
    ) -> Self {
        Self {
            id: Uuid::new_v4(),
            label: label.into(),
            kind: kind.into(),
            properties: properties.unwrap_or_default(),
            extracted_at: Utc::now(),
        }
    }
}

/// A directed, weighted edge between two [`GraphNode`]s. Mirrors `GraphEdge`.
#[derive(Debug, Clone, PartialEq)]
pub struct GraphEdge {
    pub id: Uuid,
    pub source_id: Uuid,
    pub target_id: Uuid,
    /// e.g. `"mentions"`, `"causes"`, `"resolves"`, `"depends_on"`.
    pub relation: String,
    /// 0.0–1.0; strength of the relationship (clamped at construction).
    pub weight: f32,
    pub created_at: DateTime<Utc>,
}

impl GraphEdge {
    /// Creates an edge with a fresh id and the current UTC timestamp; `weight`
    /// is clamped to `[0.0, 1.0]`. Mirrors `GraphEdge.Create`.
    pub fn create(
        source_id: Uuid,
        target_id: Uuid,
        relation: impl Into<String>,
        weight: f32,
    ) -> Self {
        Self {
            id: Uuid::new_v4(),
            source_id,
            target_id,
            relation: relation.into(),
            weight: weight.clamp(0.0, 1.0),
            created_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// KnowledgeGraph
// ─────────────────────────────────────────────────────────────────────────────

/// An in-memory entity–relationship graph. Nodes/edges are last-write-wins on
/// id collision; graphs compose via [`KnowledgeGraph::merge`]. Mirrors the
/// simple `CircleAI.Simulation.KnowledgeGraph`.
#[derive(Debug, Clone, Default)]
pub struct KnowledgeGraph {
    nodes: HashMap<Uuid, GraphNode>,
    edges: HashMap<Uuid, GraphEdge>,
}

impl KnowledgeGraph {
    /// Creates an empty graph.
    pub fn new() -> Self {
        Self::default()
    }

    /// All nodes, keyed by id.
    pub fn nodes(&self) -> &HashMap<Uuid, GraphNode> {
        &self.nodes
    }

    /// All edges, keyed by id.
    pub fn edges(&self) -> &HashMap<Uuid, GraphEdge> {
        &self.edges
    }

    /// Adds or replaces a node (last-write wins on id collision).
    pub fn add_node(&mut self, node: GraphNode) {
        self.nodes.insert(node.id, node);
    }

    /// Adds or replaces an edge (last-write wins on id collision).
    pub fn add_edge(&mut self, edge: GraphEdge) {
        self.edges.insert(edge.id, edge);
    }

    /// Returns all edges where `node_id` is the source or target.
    pub fn edges_for(&self, node_id: Uuid) -> Vec<GraphEdge> {
        self.edges
            .values()
            .filter(|e| e.source_id == node_id || e.target_id == node_id)
            .cloned()
            .collect()
    }

    /// Returns all nodes reachable from `start_id` by BFS (including the start
    /// node itself, when present). Order matches the C# reference: start node
    /// first, then discovery order.
    pub fn reachable_from(&self, start_id: Uuid) -> Vec<GraphNode> {
        let mut visited: HashSet<Uuid> = HashSet::new();
        let mut queue: VecDeque<Uuid> = VecDeque::new();
        queue.push_back(start_id);
        let mut result: Vec<GraphNode> = Vec::new();

        while let Some(current) = queue.pop_front() {
            if !visited.insert(current) {
                continue;
            }
            if let Some(node) = self.nodes.get(&current) {
                result.push(node.clone());
            }
            for edge in self.edges_for(current) {
                let next = if edge.source_id == current {
                    edge.target_id
                } else {
                    edge.source_id
                };
                if !visited.contains(&next) {
                    queue.push_back(next);
                }
            }
        }
        result
    }

    /// Merges another graph's nodes and edges into this graph (last-write wins).
    pub fn merge(&mut self, other: &KnowledgeGraph) {
        for n in other.nodes.values() {
            self.nodes.insert(n.id, n.clone());
        }
        for e in other.edges.values() {
            self.edges.insert(e.id, e.clone());
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SimulationScenario / SimulationResult
// ─────────────────────────────────────────────────────────────────────────────

/// The kind of simulation scenario. Mirrors `ScenarioKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScenarioKind {
    /// Model what happens if a configuration key changes.
    ConfigurationShift,
    /// Model a new data-sharing pipeline being introduced.
    DataPipelineChange,
    /// Model a code deployment propagating through the peer network.
    SoftwareDeployment,
    /// Model a security patch propagating through the peer network.
    SecurityPatch,
    /// Model how a confirmed runtime threat would propagate if not contained.
    ThreatPropagation,
}

/// Describes a single simulation scenario. Mirrors `SimulationScenario`.
#[derive(Debug, Clone, PartialEq)]
pub struct SimulationScenario {
    pub id: Uuid,
    pub kind: ScenarioKind,
    pub description: String,
    pub parameters: HashMap<String, String>,
    /// Simulation depth; defaults to 10 via [`SimulationScenario::create`].
    pub step_count: i32,
    pub created_at: DateTime<Utc>,
}

impl SimulationScenario {
    /// Creates a scenario with a fresh id and the current UTC timestamp.
    /// Mirrors `SimulationScenario.Create` (`steps` defaults to 10).
    pub fn create(
        kind: ScenarioKind,
        description: impl Into<String>,
        parameters: Option<HashMap<String, String>>,
        steps: i32,
    ) -> Self {
        Self {
            id: Uuid::new_v4(),
            kind,
            description: description.into(),
            parameters: parameters.unwrap_or_default(),
            step_count: steps,
            created_at: Utc::now(),
        }
    }
}

/// The overall health outcome of a simulation run. Mirrors `SimulationOutcome`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimulationOutcome {
    /// Health score is 0.8 or above; network is operating normally.
    Healthy,
    /// Health score is between 0.5 and 0.8; performance may be reduced.
    Degraded,
    /// Health score is between 0.2 and 0.5; service is significantly impaired.
    Critical,
    /// Health score is below 0.2; state is indeterminate.
    Unknown,
}

/// Outcome of a single simulation run. Mirrors `SimulationResult`.
#[derive(Debug, Clone, PartialEq)]
pub struct SimulationResult {
    pub scenario_id: Uuid,
    pub outcome: SimulationOutcome,
    /// 0.0–1.0; higher = healthier.
    pub health_score: f32,
    pub findings: Vec<String>,
    pub recommendations: Vec<String>,
    pub steps_run: i32,
    pub completed_at: DateTime<Utc>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Seams: IGraphBuilder / ISimulationEngine
// ─────────────────────────────────────────────────────────────────────────────

/// Builds a [`KnowledgeGraph`] from a list of episodic memory entries. Mirrors
/// `IGraphBuilder`.
pub trait IGraphBuilder {
    /// Builds and returns a graph extracted from `entries`.
    fn build(&self, entries: &[EpisodicMemoryEntry]) -> KnowledgeGraph;
}

/// Runs a scenario against a graph and returns a [`SimulationResult`]. Mirrors
/// `ISimulationEngine` (synchronous — the diffusion math is pure/CPU-bound).
pub trait ISimulationEngine {
    /// Executes the simulation.
    fn run(&self, scenario: &SimulationScenario, graph: &KnowledgeGraph) -> SimulationResult;
}

// ─────────────────────────────────────────────────────────────────────────────
// EpisodicGraphExtractor
// ─────────────────────────────────────────────────────────────────────────────

/// Extracts a [`KnowledgeGraph`] from episodic memory using keyword/tag
/// heuristics — fully offline, no LLM. Mirrors `EpisodicGraphExtractor`.
///
/// Rules, in order:
///   1. Each entry becomes an `"event"` node (label = first 60 chars of user text).
///   2. Each tag key becomes a `"topic"` node; edge event → topic (`"tagged_with"`, 1.0).
///   3. `app_context` becomes an `"app"` node; edge event → app (`"occurred_in"`, 1.0).
///   4. Consecutive entries within 1 hour are linked by a `"followed_by"` edge (0.5).
#[derive(Debug, Default, Clone)]
pub struct EpisodicGraphExtractor;

impl EpisodicGraphExtractor {
    /// Creates a new extractor.
    pub fn new() -> Self {
        Self
    }
}

impl IGraphBuilder for EpisodicGraphExtractor {
    fn build(&self, entries: &[EpisodicMemoryEntry]) -> KnowledgeGraph {
        let mut graph = KnowledgeGraph::new();
        // Case-insensitive keys for app/topic dedup (matches OrdinalIgnoreCase).
        let mut app_nodes: HashMap<String, GraphNode> = HashMap::new();
        let mut topic_nodes: HashMap<String, GraphNode> = HashMap::new();
        let mut prev: Option<GraphNode> = None;
        let mut prev_time: Option<DateTime<Utc>> = None;

        // Order by recorded time (stable), mirroring OrderBy(e => e.RecordedAtUtc).
        let mut ordered: Vec<&EpisodicMemoryEntry> = entries.iter().collect();
        ordered.sort_by(|a, b| a.recorded_at_utc.cmp(&b.recorded_at_utc));

        for entry in ordered {
            // Label = first 60 chars of user text (char-based, matching the C#
            // substring on a UTF-16 length would differ on surrogates; we take
            // Unicode scalar values which is the faithful "characters" intent).
            let label: String = if entry.user_text.chars().count() > 60 {
                entry.user_text.chars().take(60).collect()
            } else {
                entry.user_text.clone()
            };
            let mut ev_props = HashMap::new();
            ev_props.insert("episode_id".to_string(), entry.id.to_string());
            let ev_node = GraphNode::create(label, "event", Some(ev_props));
            graph.add_node(ev_node.clone());

            // App context → node + edge.
            if let Some(app_ctx) = entry.app_context.as_ref() {
                if !app_ctx.trim().is_empty() {
                    let key = app_ctx.to_lowercase();
                    let app_node = app_nodes.entry(key).or_insert_with(|| {
                        let n = GraphNode::create(app_ctx.clone(), "app", None);
                        graph.add_node(n.clone());
                        n
                    });
                    graph.add_edge(GraphEdge::create(
                        ev_node.id,
                        app_node.id,
                        "occurred_in",
                        1.0,
                    ));
                }
            }

            // Tags → topic nodes + edges.
            if let Some(tags) = entry.tags.as_ref() {
                for tag in tags.keys() {
                    let key = tag.to_lowercase();
                    let topic_node = topic_nodes.entry(key).or_insert_with(|| {
                        let n = GraphNode::create(tag.clone(), "topic", None);
                        graph.add_node(n.clone());
                        n
                    });
                    graph.add_edge(GraphEdge::create(
                        ev_node.id,
                        topic_node.id,
                        "tagged_with",
                        1.0,
                    ));
                }
            }

            // Temporal sequence — connect to previous event if within 1 hour.
            if let (Some(p), Some(pt)) = (prev.as_ref(), prev_time) {
                let delta = entry.recorded_at_utc - pt;
                if (delta.num_milliseconds() as f64) / 3_600_000.0 <= 1.0 {
                    graph.add_edge(GraphEdge::create(p.id, ev_node.id, "followed_by", 0.5));
                }
            }

            prev = Some(ev_node);
            prev_time = Some(entry.recorded_at_utc);
        }

        graph
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LocalSimulationEngine (deterministic diffusion)
// ─────────────────────────────────────────────────────────────────────────────

/// Deterministic graph-diffusion engine used when no external engine is
/// injected. Mirrors the internal `LocalSimulationEngine`.
#[derive(Debug, Default, Clone)]
pub struct LocalSimulationEngine;

impl LocalSimulationEngine {
    const DECAY_PER_STEP: f32 = 0.01;
    const HIGH_IMPACT_THRESHOLD: f32 = 0.7;

    /// Creates a new engine.
    pub fn new() -> Self {
        Self
    }
}

impl ISimulationEngine for LocalSimulationEngine {
    fn run(&self, scenario: &SimulationScenario, graph: &KnowledgeGraph) -> SimulationResult {
        let mut health: f32 = 1.0;
        let mut high_impact: HashSet<String> = HashSet::new();

        let mut step = 0;
        while step < scenario.step_count && health > 0.0 {
            for edge in graph.edges.values() {
                health -= (1.0 - edge.weight) * Self::DECAY_PER_STEP;

                if edge.weight >= Self::HIGH_IMPACT_THRESHOLD {
                    if let Some(src) = graph.nodes.get(&edge.source_id) {
                        high_impact.insert(src.label.clone());
                    }
                }
            }
            step += 1;
        }

        health = health.clamp(0.0, 1.0);

        let outcome = if health >= 0.8 {
            SimulationOutcome::Healthy
        } else if health >= 0.5 {
            SimulationOutcome::Degraded
        } else if health >= 0.2 {
            SimulationOutcome::Critical
        } else {
            SimulationOutcome::Unknown
        };

        let findings: Vec<String> = if !high_impact.is_empty() {
            let mut v: Vec<String> = high_impact
                .iter()
                .map(|l| format!("High-impact node detected: {l}"))
                .collect();
            v.sort();
            v
        } else {
            vec!["No high-impact nodes detected.".to_string()]
        };

        let recommendations: Vec<String> = if matches!(
            outcome,
            SimulationOutcome::Degraded | SimulationOutcome::Critical
        ) {
            vec![
                "Review high-weight edges before deployment.".to_string(),
                "Consider incremental rollout.".to_string(),
            ]
        } else {
            vec!["Network health nominal — proceed with deployment.".to_string()]
        };

        SimulationResult {
            scenario_id: scenario.id,
            outcome,
            health_score: health,
            findings,
            recommendations,
            steps_run: scenario.step_count,
            completed_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NetworkHealthSimulator
// ─────────────────────────────────────────────────────────────────────────────

/// Offline network-health simulator. Extracts a knowledge graph from episodic
/// memory, then runs a deterministic diffusion model to forecast the health
/// impact of a scenario. Mirrors `NetworkHealthSimulator`.
///
/// The default engine here is [`LocalSimulationEngine`] (the C# reference
/// defaults to a `MiroFishAdapter` external engine, which is out of scope for
/// the portable core — callers inject their own via [`NetworkHealthSimulator::with_parts`]).
pub struct NetworkHealthSimulator {
    extractor: Box<dyn IGraphBuilder + Send + Sync>,
    engine: Box<dyn ISimulationEngine + Send + Sync>,
}

impl Default for NetworkHealthSimulator {
    fn default() -> Self {
        Self::new()
    }
}

impl NetworkHealthSimulator {
    /// Creates a simulator with the default extractor and engine.
    pub fn new() -> Self {
        Self {
            extractor: Box::new(EpisodicGraphExtractor::new()),
            engine: Box::new(LocalSimulationEngine::new()),
        }
    }

    /// Creates a simulator with caller-supplied extractor and engine seams.
    pub fn with_parts(
        extractor: Box<dyn IGraphBuilder + Send + Sync>,
        engine: Box<dyn ISimulationEngine + Send + Sync>,
    ) -> Self {
        Self { extractor, engine }
    }

    /// Builds a graph from `history` and runs `scenario` through the engine.
    /// Mirrors `ForecastAsync` (synchronous here).
    pub fn forecast(
        &self,
        history: &[EpisodicMemoryEntry],
        scenario: &SimulationScenario,
    ) -> SimulationResult {
        let graph = self.extractor.build(history);
        self.engine.run(scenario, &graph)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(user: &str, app: Option<&str>, tags: &[&str]) -> EpisodicMemoryEntry {
        let mut e = EpisodicMemoryEntry::default();
        e.user_text = user.to_string();
        e.app_context = app.map(|s| s.to_string());
        if !tags.is_empty() {
            let mut m = HashMap::new();
            for t in tags {
                m.insert(t.to_string(), "1".to_string());
            }
            e.tags = Some(m);
        }
        e
    }

    #[test]
    fn reachable_and_edges_for() {
        let mut g = KnowledgeGraph::new();
        let a = GraphNode::create("a", "event", None);
        let b = GraphNode::create("b", "topic", None);
        let c = GraphNode::create("c", "topic", None);
        g.add_node(a.clone());
        g.add_node(b.clone());
        g.add_node(c.clone());
        g.add_edge(GraphEdge::create(a.id, b.id, "tagged_with", 1.0));
        // c is isolated.
        let reach = g.reachable_from(a.id);
        assert_eq!(reach.len(), 2);
        assert_eq!(reach[0].id, a.id);
        assert_eq!(g.edges_for(a.id).len(), 1);
        assert_eq!(g.edges_for(c.id).len(), 0);
    }

    #[test]
    fn merge_last_write_wins() {
        let mut g1 = KnowledgeGraph::new();
        let n = GraphNode::create("x", "event", None);
        g1.add_node(n.clone());
        let mut g2 = KnowledgeGraph::new();
        let n2 = GraphNode {
            label: "x2".into(),
            ..n.clone()
        };
        g2.add_node(n2);
        g1.merge(&g2);
        assert_eq!(g1.nodes().get(&n.id).unwrap().label, "x2");
    }

    #[test]
    fn extractor_builds_event_topic_app() {
        let ex = EpisodicGraphExtractor::new();
        let g = ex.build(&[entry("hello world", Some("tgn.bidbaas"), &["greeting"])]);
        // 1 event + 1 app + 1 topic.
        assert_eq!(g.nodes().len(), 3);
        // occurred_in + tagged_with.
        assert_eq!(g.edges().len(), 2);
    }

    #[test]
    fn forecast_healthy_on_empty() {
        let sim = NetworkHealthSimulator::new();
        let scenario = SimulationScenario::create(
            ScenarioKind::SoftwareDeployment,
            "deploy",
            None,
            10,
        );
        let res = sim.forecast(&[], &scenario);
        assert_eq!(res.outcome, SimulationOutcome::Healthy);
        assert_eq!(res.steps_run, 10);
    }
}
