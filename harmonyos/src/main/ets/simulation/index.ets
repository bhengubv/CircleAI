/**
 * simulation/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// simulation/index.ts
//
// Full-parity port of CircleAI.Simulation (C#). C# is the exact spec.
//
// Offline network-health simulation over a knowledge graph extracted from
// episodic memory:
//   • GraphNode / GraphEdge / KnowledgeGraph          (graph primitives + BFS/merge)
//   • ScenarioKind / SimulationScenario               (scenario record + Create)
//   • SimulationOutcome / SimulationResult            (result record)
//   • IGraphBuilder / EpisodicGraphExtractor          (episodic → graph)
//   • ISimulationEngine / LocalSimulationEngine       (deterministic diffusion)
//   • MiroFishAdapter                                 (external-engine adapter)
//   • NetworkHealthSimulator                          (build + forecast facade)
//   • ThreatPropagationScenario                       (Security ↔ Simulation bridge)
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ Create factory)
//   Guid                                  → string (UUID)
//   DateTimeOffset                        → Date (ToString("O") → toISOString())
//   IReadOnlyDictionary<Guid,GraphNode>   → ReadonlyMap<string, GraphNode>
//   IReadOnlyDictionary<string,string>    → ReadonlyMap<string, string>
//   IEnumerable<GraphEdge>                → GraphEdge[]
//   float Weight / HealthScore            → number (Math.fround at float sites)
//   Math.Clamp(x, 0f, 1f)                 → clampF(x)
//   CancellationToken                     → optional AbortSignal
//   switch expression on health           → if/else band ladder

import { randomUUID } from "../platform";
import type { EpisodicMemoryEntry } from "../memory/index";
import { type AnomalySignal, ThreatVector } from "../security/index";

// ─────────────────────────────────────────────────────────────────────────────
// GraphNode — an entity extracted from episodic memory. Mirrors C# `GraphNode`.
// ─────────────────────────────────────────────────────────────────────────────

/** A node in the knowledge graph. Mirrors C# `GraphNode` record. */
export interface GraphNode {
  readonly id: string;
  /** Canonical entity label. */
  readonly label: string;
  /** "person" | "topic" | "app" | "event" | "system". */
  readonly kind: string;
  /** Arbitrary key-value metadata. */
  readonly properties: ReadonlyMap<string, string>;
  readonly extractedAt: Date;
}

/** Constructs a {@link GraphNode}. Mirrors the C# positional record ctor. */
export function graphNode(
  id: string,
  label: string,
  kind: string,
  properties: ReadonlyMap<string, string>,
  extractedAt: Date,
): GraphNode {
  return { id, label, kind, properties, extractedAt };
}

/** Creates a {@link GraphNode} with a fresh id + current UTC timestamp. Mirrors C# `GraphNode.Create`. */
export function createGraphNode(
  label: string,
  kind: string,
  properties?: ReadonlyMap<string, string>,
): GraphNode {
  return graphNode(randomUUID(), label, kind, properties ?? new Map<string, string>(), new Date());
}

// ─────────────────────────────────────────────────────────────────────────────
// GraphEdge — a directed, weighted edge. Mirrors C# `GraphEdge` record.
// ─────────────────────────────────────────────────────────────────────────────

/** A directed, weighted edge between two {@link GraphNode}s. Mirrors C# `GraphEdge` record. */
export interface GraphEdge {
  readonly id: string;
  readonly sourceId: string;
  readonly targetId: string;
  /** e.g. "mentions", "causes", "resolves", "depends_on". */
  readonly relation: string;
  /** 0.0–1.0; strength of the relationship. */
  readonly weight: number;
  readonly createdAt: Date;
}

/** Constructs a {@link GraphEdge}. Mirrors the C# positional record ctor. */
export function graphEdge(
  id: string,
  sourceId: string,
  targetId: string,
  relation: string,
  weight: number,
  createdAt: Date,
): GraphEdge {
  return { id, sourceId, targetId, relation, weight, createdAt };
}

/**
 * Creates a {@link GraphEdge} with a fresh id + current UTC timestamp. `weight`
 * is clamped to [0.0, 1.0]. Mirrors C# `GraphEdge.Create`.
 */
export function createGraphEdge(
  sourceId: string,
  targetId: string,
  relation: string,
  weight = 1.0,
): GraphEdge {
  return graphEdge(randomUUID(), sourceId, targetId, relation, clampF(weight), new Date());
}

// ─────────────────────────────────────────────────────────────────────────────
// KnowledgeGraph — in-memory entity-relationship graph. Mirrors C# `KnowledgeGraph`.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An in-memory entity–relationship graph extracted from episodic memory. Nodes
 * and edges are immutable once added; graphs are composable via {@link merge}.
 * Mirrors C# `KnowledgeGraph`.
 */
export class KnowledgeGraph {
  private readonly _nodes = new Map<string, GraphNode>();
  private readonly _edges = new Map<string, GraphEdge>();

  /** All nodes in the graph, keyed by their id. */
  get nodes(): ReadonlyMap<string, GraphNode> {
    return this._nodes;
  }

  /** All edges in the graph, keyed by their id. */
  get edges(): ReadonlyMap<string, GraphEdge> {
    return this._edges;
  }

  /** Adds or replaces a node (last-write wins on id collision). */
  addNode(node: GraphNode): void {
    if (node == null) throw new Error("node required");
    this._nodes.set(node.id, node);
  }

  /** Adds or replaces an edge (last-write wins on id collision). */
  addEdge(edge: GraphEdge): void {
    if (edge == null) throw new Error("edge required");
    this._edges.set(edge.id, edge);
  }

  /** All edges where `nodeId` is the source or target. */
  edgesFor(nodeId: string): GraphEdge[] {
    return [...this._edges.values()].filter(
      (e) => e.sourceId === nodeId || e.targetId === nodeId,
    );
  }

  /** All nodes reachable from `startId` by BFS (including the start node itself). */
  reachableFrom(startId: string): readonly GraphNode[] {
    const visited = new Set<string>();
    const queue: string[] = [startId];
    const result: GraphNode[] = [];

    while (queue.length > 0) {
      const current = queue.shift() as string;
      if (visited.has(current)) continue;
      visited.add(current);
      const node = this._nodes.get(current);
      if (node !== undefined) result.push(node);
      for (const edge of this.edgesFor(current)) {
        const next = edge.sourceId === current ? edge.targetId : edge.sourceId;
        if (!visited.has(next)) queue.push(next);
      }
    }
    return result;
  }

  /** Merges another graph's nodes and edges (last-write wins on id collision). */
  merge(other: KnowledgeGraph): void {
    if (other == null) throw new Error("other required");
    for (const n of other._nodes.values()) this._nodes.set(n.id, n);
    for (const e of other._edges.values()) this._edges.set(e.id, e);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ScenarioKind / SimulationScenario — mirrors C# `ScenarioKind` / `SimulationScenario`.
// ─────────────────────────────────────────────────────────────────────────────

/** Kinds of simulation scenarios. Mirrors C# `ScenarioKind` enum (ordinals 0..4). */
export enum ScenarioKind {
  /** Model what happens if a configuration key changes. */
  ConfigurationShift = 0,
  /** Model a new data-sharing pipeline being introduced. */
  DataPipelineChange = 1,
  /** Model a code deployment propagating through the peer network. */
  SoftwareDeployment = 2,
  /** Model a security patch propagating through the peer network. */
  SecurityPatch = 3,
  /** Model how a confirmed runtime threat would propagate if not contained. */
  ThreatPropagation = 4,
}

/** Describes a single simulation scenario. Mirrors C# `SimulationScenario` record. */
export interface SimulationScenario {
  readonly id: string;
  readonly kind: ScenarioKind;
  readonly description: string;
  /** Scenario-specific config. */
  readonly parameters: ReadonlyMap<string, string>;
  /** Simulation depth, default 10. */
  readonly stepCount: number;
  readonly createdAt: Date;
}

/** Constructs a {@link SimulationScenario}. Mirrors the C# positional record ctor. */
export function simulationScenario(
  id: string,
  kind: ScenarioKind,
  description: string,
  parameters: ReadonlyMap<string, string>,
  stepCount: number,
  createdAt: Date,
): SimulationScenario {
  return { id, kind, description, parameters, stepCount, createdAt };
}

/** Creates a {@link SimulationScenario} with a fresh id + current UTC timestamp. Mirrors C# `SimulationScenario.Create`. */
export function createSimulationScenario(
  kind: ScenarioKind,
  description: string,
  parameters?: ReadonlyMap<string, string>,
  steps = 10,
): SimulationScenario {
  return simulationScenario(
    randomUUID(),
    kind,
    description,
    parameters ?? new Map<string, string>(),
    steps,
    new Date(),
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// SimulationOutcome / SimulationResult — mirrors C# `SimulationOutcome` / `SimulationResult`.
// ─────────────────────────────────────────────────────────────────────────────

/** The overall health outcome of a simulation run. Mirrors C# `SimulationOutcome` enum (ordinals 0..3). */
export enum SimulationOutcome {
  /** Health score is 0.8 or above; network is operating normally. */
  Healthy = 0,
  /** Health score is between 0.5 and 0.8; performance may be reduced. */
  Degraded = 1,
  /** Health score is between 0.2 and 0.5; service is significantly impaired. */
  Critical = 2,
  /** Health score is below 0.2; state is indeterminate. */
  Unknown = 3,
}

/** Captures the outcome of a single simulation run. Mirrors C# `SimulationResult` record. */
export interface SimulationResult {
  readonly scenarioId: string;
  readonly outcome: SimulationOutcome;
  /** 0.0–1.0; higher = healthier. */
  readonly healthScore: number;
  /** Human-readable simulation findings. */
  readonly findings: readonly string[];
  readonly recommendations: readonly string[];
  readonly stepsRun: number;
  readonly completedAt: Date;
}

/** Constructs a {@link SimulationResult}. Mirrors the C# positional record ctor. */
export function simulationResult(
  scenarioId: string,
  outcome: SimulationOutcome,
  healthScore: number,
  findings: readonly string[],
  recommendations: readonly string[],
  stepsRun: number,
  completedAt: Date,
): SimulationResult {
  return { scenarioId, outcome, healthScore, findings, recommendations, stepsRun, completedAt };
}

// ─────────────────────────────────────────────────────────────────────────────
// IGraphBuilder / EpisodicGraphExtractor — episodic memory → knowledge graph.
// ─────────────────────────────────────────────────────────────────────────────

/** Builds a {@link KnowledgeGraph} from episodic memory entries. Mirrors C# `IGraphBuilder`. */
export interface IGraphBuilder {
  build(entries: readonly EpisodicMemoryEntry[]): KnowledgeGraph;
}

/**
 * Extracts a {@link KnowledgeGraph} from episodic memory using keyword and tag
 * heuristics. Fully offline — no LLM dependency. Mirrors C#
 * `EpisodicGraphExtractor`.
 */
export class EpisodicGraphExtractor implements IGraphBuilder {
  build(entries: readonly EpisodicMemoryEntry[]): KnowledgeGraph {
    if (entries == null) throw new Error("entries required");
    const graph = new KnowledgeGraph();
    const appNodes = new Map<string, GraphNode>();
    const topicNodes = new Map<string, GraphNode>();
    let prev: GraphNode | null = null;
    let prevTime = -Infinity;

    const ordered = [...entries].sort(
      (a, b) => a.recordedAtUtc.getTime() - b.recordedAtUtc.getTime(),
    );

    for (const entry of ordered) {
      const label = entry.userText.length > 60 ? entry.userText.substring(0, 60) : entry.userText;
      const evNode = createGraphNode(
        label,
        "event",
        new Map<string, string>([["episode_id", entry.id]]),
      );
      graph.addNode(evNode);

      // App context → node + edge (case-insensitive dedupe).
      if (entry.appContext != null && entry.appContext.trim().length > 0) {
        const appKey = entry.appContext.toLowerCase();
        let appNode = appNodes.get(appKey);
        if (appNode === undefined) {
          appNode = createGraphNode(entry.appContext, "app");
          appNodes.set(appKey, appNode);
          graph.addNode(appNode);
        }
        graph.addEdge(createGraphEdge(evNode.id, appNode.id, "occurred_in"));
      }

      // Tags → topic nodes + edges (case-insensitive dedupe).
      if (entry.tags != null) {
        for (const tag of Object.keys(entry.tags)) {
          const topicKey = tag.toLowerCase();
          let topicNode = topicNodes.get(topicKey);
          if (topicNode === undefined) {
            topicNode = createGraphNode(tag, "topic");
            topicNodes.set(topicKey, topicNode);
            graph.addNode(topicNode);
          }
          graph.addEdge(createGraphEdge(evNode.id, topicNode.id, "tagged_with"));
        }
      }

      // Temporal sequence — connect to previous event if within 1 hour.
      const deltaHours = (entry.recordedAtUtc.getTime() - prevTime) / 3_600_000;
      if (prev !== null && deltaHours <= 1.0)
        graph.addEdge(createGraphEdge(prev.id, evNode.id, "followed_by", 0.5));

      prev = evNode;
      prevTime = entry.recordedAtUtc.getTime();
    }

    return graph;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ISimulationEngine / LocalSimulationEngine — deterministic diffusion model.
// ─────────────────────────────────────────────────────────────────────────────

/** Runs a scenario against a knowledge graph. Mirrors C# `ISimulationEngine`. */
export interface ISimulationEngine {
  runAsync(
    scenario: SimulationScenario,
    graph: KnowledgeGraph,
    signal?: AbortSignal,
  ): Promise<SimulationResult>;
}

const DECAY_PER_STEP = Math.fround(0.01);
const HIGH_IMPACT_THRESHOLD = Math.fround(0.7);

/**
 * Deterministic graph-diffusion engine used when no external MiroFish engine is
 * registered. Mirrors C# internal `LocalSimulationEngine`.
 */
export class LocalSimulationEngine implements ISimulationEngine {
  async runAsync(
    scenario: SimulationScenario,
    graph: KnowledgeGraph,
    signal?: AbortSignal,
  ): Promise<SimulationResult> {
    throwIfAborted(signal);

    let health = Math.fround(1.0);
    const highImpact = new Set<string>();

    for (let step = 0; step < scenario.stepCount && health > 0; step++) {
      for (const edge of graph.edges.values()) {
        health = Math.fround(health - Math.fround(Math.fround(1 - edge.weight) * DECAY_PER_STEP));

        if (edge.weight >= HIGH_IMPACT_THRESHOLD) {
          const src = graph.nodes.get(edge.sourceId);
          if (src !== undefined) highImpact.add(src.label);
        }
      }
      throwIfAborted(signal);
    }

    health = clampF(health);

    let outcome: SimulationOutcome;
    if (health >= 0.8) outcome = SimulationOutcome.Healthy;
    else if (health >= 0.5) outcome = SimulationOutcome.Degraded;
    else if (health >= 0.2) outcome = SimulationOutcome.Critical;
    else outcome = SimulationOutcome.Unknown;

    const findings: string[] =
      highImpact.size > 0
        ? [...highImpact].map((l) => `High-impact node detected: ${l}`)
        : ["No high-impact nodes detected."];

    const recs: string[] =
      outcome === SimulationOutcome.Degraded || outcome === SimulationOutcome.Critical
        ? ["Review high-weight edges before deployment.", "Consider incremental rollout."]
        : ["Network health nominal — proceed with deployment."];

    return simulationResult(
      scenario.id,
      outcome,
      health,
      findings,
      recs,
      scenario.stepCount,
      new Date(),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// MiroFishAdapter — external MiroFish GraphRAG engine adapter (falls back to
// LocalSimulationEngine). Mirrors C# `MiroFishAdapter`.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Adapter for the MiroFish GraphRAG simulation engine. When a real MiroFish
 * engine is provided it is preferred; otherwise the built-in
 * {@link LocalSimulationEngine} is used. Mirrors C# `MiroFishAdapter`.
 */
export class MiroFishAdapter implements ISimulationEngine {
  private readonly inner: ISimulationEngine;

  /**
   * Initialises the adapter. If `externalEngine` is null the built-in
   * {@link LocalSimulationEngine} is used as the fallback.
   */
  constructor(externalEngine?: ISimulationEngine | null) {
    this.inner = externalEngine ?? new LocalSimulationEngine();
  }

  runAsync(
    scenario: SimulationScenario,
    graph: KnowledgeGraph,
    signal?: AbortSignal,
  ): Promise<SimulationResult> {
    return this.inner.runAsync(scenario, graph, signal);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// NetworkHealthSimulator — build a graph from history, then forecast a scenario.
// Mirrors C# `NetworkHealthSimulator`.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Offline network health simulator. Extracts a knowledge graph from episodic
 * memory, then runs a deterministic diffusion model to forecast the health
 * impact of the given scenario. Mirrors C# `NetworkHealthSimulator`.
 */
export class NetworkHealthSimulator {
  private readonly extractor: IGraphBuilder;
  private readonly engine: ISimulationEngine;

  /**
   * Initialises the simulator with optional overrides for the graph builder and
   * simulation engine. Defaults to {@link EpisodicGraphExtractor} and
   * {@link MiroFishAdapter} respectively.
   */
  constructor(extractor?: IGraphBuilder | null, engine?: ISimulationEngine | null) {
    this.extractor = extractor ?? new EpisodicGraphExtractor();
    this.engine = engine ?? new MiroFishAdapter();
  }

  /**
   * Builds a knowledge graph from `history` and runs the given `scenario`
   * through the simulation engine. Mirrors C# `NetworkHealthSimulator.ForecastAsync`.
   */
  async forecastAsync(
    history: readonly EpisodicMemoryEntry[],
    scenario: SimulationScenario,
    signal?: AbortSignal,
  ): Promise<SimulationResult> {
    if (history == null) throw new Error("history required");
    if (scenario == null) throw new Error("scenario required");
    const graph = this.extractor.build(history);
    return this.engine.runAsync(scenario, graph, signal);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ThreatPropagationScenario — Security ↔ Simulation bridge. Builds a
// ThreatPropagation scenario from an AnomalySignal. Mirrors C#
// `ThreatPropagationScenario`.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Factory for building {@link SimulationScenario} instances of
 * {@link ScenarioKind.ThreatPropagation} from an {@link AnomalySignal}. Mirrors
 * C# static `ThreatPropagationScenario`.
 */
export class ThreatPropagationScenario {
  private constructor() {
    /* static-only */
  }

  /**
   * Number of diffusion steps for a given {@link ThreatVector}. Higher-severity
   * vectors warrant deeper simulation depth. Mirrors C#
   * `ThreatPropagationScenario.StepCountFor`.
   */
  private static stepCountFor(vector: ThreatVector): number {
    switch (vector) {
      case ThreatVector.NetworkPivot:
        return 30;
      case ThreatVector.ControlFlowDrift:
        return 25;
      case ThreatVector.PrivilegeEscalation:
        return 25;
      case ThreatVector.StateCorruption:
        return 20;
      case ThreatVector.MemoryAnomaly:
        return 15;
      case ThreatVector.AgentPatchRejected:
        return 15;
      case ThreatVector.BiometricSpoofAttempt:
        return 12;
      default:
        return 10;
    }
  }

  /**
   * Creates a {@link SimulationScenario} describing how the threat described by
   * `signal` would propagate through the peer network if unmitigated. Mirrors
   * C# `ThreatPropagationScenario.FromAnomalySignal`.
   */
  static fromAnomalySignal(signal: AnomalySignal, stepOverride?: number | null): SimulationScenario {
    if (signal == null) throw new Error("signal required");

    const parameters = new Map<string, string>();
    // Seed from the signal's evidence (C# `new Dictionary(signal.Evidence)`).
    for (const [k, v] of Object.entries(signal.evidence)) parameters.set(k, v);
    parameters.set("signal_id", signal.id);
    parameters.set("vector", ThreatVector[signal.vector]);
    parameters.set("confidence", formatF3(signal.confidence));
    parameters.set("affected_module", signal.affectedModule);
    parameters.set("detected_at", signal.detectedAt.toISOString());

    const steps = stepOverride ?? ThreatPropagationScenario.stepCountFor(signal.vector);

    return simulationScenario(
      randomUUID(),
      ScenarioKind.ThreatPropagation,
      `threat-propagation: ${ThreatVector[signal.vector]} in ${signal.affectedModule} ` +
        `(confidence ${formatP0(signal.confidence)})`,
      parameters,
      steps,
      new Date(),
    );
  }
}

// ── helpers ───────────────────────────────────────────────────────────────────

/** Math.Clamp(x, 0f, 1f) with single-precision rounding. */
function clampF(x: number): number {
  return Math.fround(Math.min(1, Math.max(0, x)));
}

/** C# double.ToString("F3", InvariantCulture) — fixed 3 decimals. */
function formatF3(x: number): string {
  return x.toFixed(3);
}

/** C# {x:P0} — percentage, 0 decimals, invariant (e.g. 0.87 → "87%"). */
function formatP0(x: number): string {
  return `${Math.round(x * 100)}%`;
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
}
