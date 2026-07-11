// Simulation.kt
//
// Kotlin port of CircleAI.Simulation — the C# reference is the EXACT spec.
// An offline, deterministic network-health simulator built on a knowledge
// graph extracted from episodic memory.
//
// Covers (C# file -> Kotlin type):
//   GraphNode.cs                 -> GraphNode
//   GraphEdge.cs                 -> GraphEdge
//   KnowledgeGraph.cs            -> KnowledgeGraph
//   IGraphBuilder.cs             -> IGraphBuilder
//   ISimulationEngine.cs         -> ISimulationEngine
//   SimulationScenario.cs        -> ScenarioKind, SimulationScenario
//   SimulationResult.cs          -> SimulationOutcome, SimulationResult
//   EpisodicGraphExtractor.cs    -> EpisodicGraphExtractor
//   NetworkHealthSimulator.cs    -> NetworkHealthSimulator, LocalSimulationEngine
//   MiroFishAdapter.cs           -> MiroFishAdapter
//   ThreatPropagationScenario.cs -> ThreatPropagationScenario
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; static `Create` factories -> companion
//     `create(...)`.
//   * C# `Guid` -> `java.util.UUID`; `Guid.NewGuid()` -> `UUID.randomUUID()`.
//   * C# `DateTimeOffset` -> `java.time.Instant`; `.UtcNow` -> `Instant.now()`.
//   * C# `float` -> Kotlin `Float`; `Math.Clamp(w,0,1)` -> `w.coerceIn(0f,1f)`.
//   * The C# extractor consumes `CircleAI.Memory.EpisodicMemoryEntry`. The Kotlin
//     port's equivalent brain type is `memory.brain.EpisodicEntry` (id/userText/
//     assistantText/recordedAtUtc/appContext/tags:Map) — used here 1:1. Its
//     `RecordedAtUtc` maps to `recordedAtUtc: Instant`; time-window math uses
//     `Duration.between`.
//   * ThreatPropagationScenario consumes `security.AnomalySignal` + `security.
//     ThreatVector` (both already ported).
//   * The C# `[Experimental]` / `[CircleAIVerificationStatus(Reference)]`
//     attributes have no Kotlin analogue in this port; the Reference-level
//     verification caveat is preserved as KDoc.
//   * C# `internal sealed class LocalSimulationEngine` -> `internal class`.
//   * `RunAsync` is synchronous CPU work returning `Task<SimulationResult>`;
//     mapped to a `suspend fun` that honours cancellation cooperatively.

package com.bhengubv.circleai.simulation

import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.security.AnomalySignal
import com.bhengubv.circleai.security.ThreatVector
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.time.Duration
import java.time.Instant
import java.util.Locale
import java.util.UUID

// =====================================================================
// GraphNode (GraphNode.cs)
// =====================================================================

/**
 * A node in the Circle AI knowledge graph. Represents any entity extracted
 * from episodic memory — person, topic, app, system event.
 *
 * This type is fixture-validated: field names and types must not change
 * without updating `fixtures/graph_schema.json`.
 */
data class GraphNode(
    val id: UUID,
    /** Canonical entity label. */
    val label: String,
    /** `"person" | "topic" | "app" | "event" | "system"`. */
    val kind: String,
    /** Arbitrary key-value metadata. */
    val properties: Map<String, String>,
    val extractedAt: Instant,
) {
    companion object {
        /**
         * Creates a new [GraphNode] with a generated [UUID] id and the current
         * UTC timestamp.
         */
        fun create(
            label: String,
            kind: String,
            properties: Map<String, String>? = null,
        ): GraphNode = GraphNode(
            id = UUID.randomUUID(),
            label = label,
            kind = kind,
            properties = properties ?: emptyMap(),
            extractedAt = Instant.now(),
        )
    }
}

// =====================================================================
// GraphEdge (GraphEdge.cs)
// =====================================================================

/**
 * A directed, weighted edge between two [GraphNode] instances.
 * Fixture-validated.
 */
data class GraphEdge(
    val id: UUID,
    val sourceId: UUID,
    val targetId: UUID,
    /** e.g. `"mentions"`, `"causes"`, `"resolves"`, `"depends_on"`. */
    val relation: String,
    /** 0.0–1.0; strength of the relationship. */
    val weight: Float,
    val createdAt: Instant,
) {
    companion object {
        /**
         * Creates a new [GraphEdge] with a generated [UUID] id and the current
         * UTC timestamp. [weight] is clamped to `[0.0, 1.0]`.
         */
        fun create(
            sourceId: UUID,
            targetId: UUID,
            relation: String,
            weight: Float = 1.0f,
        ): GraphEdge = GraphEdge(
            id = UUID.randomUUID(),
            sourceId = sourceId,
            targetId = targetId,
            relation = relation,
            weight = weight.coerceIn(0f, 1f),
            createdAt = Instant.now(),
        )
    }
}

// =====================================================================
// KnowledgeGraph (KnowledgeGraph.cs)
// =====================================================================

/**
 * An in-memory entity–relationship graph extracted from episodic memory.
 * Nodes and edges are immutable once added; graphs are composable via [merge].
 */
class KnowledgeGraph {
    private val _nodes = LinkedHashMap<UUID, GraphNode>()
    private val _edges = LinkedHashMap<UUID, GraphEdge>()

    /** All nodes in the graph, keyed by their id. */
    val nodes: Map<UUID, GraphNode> get() = _nodes

    /** All edges in the graph, keyed by their id. */
    val edges: Map<UUID, GraphEdge> get() = _edges

    /** Adds or replaces a node (last-write wins on id collision). */
    fun addNode(node: GraphNode) {
        _nodes[node.id] = node
    }

    /** Adds or replaces an edge (last-write wins on id collision). */
    fun addEdge(edge: GraphEdge) {
        _edges[edge.id] = edge
    }

    /** Returns all edges where [nodeId] is the source or target. */
    fun edgesFor(nodeId: UUID): List<GraphEdge> =
        _edges.values.filter { it.sourceId == nodeId || it.targetId == nodeId }

    /**
     * Returns all nodes reachable from [startId] by BFS (including the start
     * node itself).
     */
    fun reachableFrom(startId: UUID): List<GraphNode> {
        val visited = HashSet<UUID>()
        val queue = ArrayDeque<UUID>()
        queue.addLast(startId)
        val result = ArrayList<GraphNode>()

        while (queue.isNotEmpty()) {
            val current = queue.removeFirst()
            if (!visited.add(current)) continue
            _nodes[current]?.let { result.add(it) }
            for (edge in edgesFor(current)) {
                val next = if (edge.sourceId == current) edge.targetId else edge.sourceId
                if (next !in visited) queue.addLast(next)
            }
        }
        return result
    }

    /**
     * Merges another graph's nodes and edges into this graph (last-write wins
     * on id collision).
     */
    fun merge(other: KnowledgeGraph) {
        for (n in other._nodes.values) _nodes[n.id] = n
        for (e in other._edges.values) _edges[e.id] = e
    }
}

// =====================================================================
// IGraphBuilder (IGraphBuilder.cs)
// =====================================================================

/**
 * Builds a [KnowledgeGraph] from a list of episodic memory entries.
 */
interface IGraphBuilder {
    /**
     * Builds and returns a [KnowledgeGraph] extracted from the given [entries].
     */
    fun build(entries: List<EpisodicEntry>): KnowledgeGraph
}

// =====================================================================
// SimulationScenario (SimulationScenario.cs)
// =====================================================================

/** Enumerates the kinds of simulation scenarios supported by the engine. */
enum class ScenarioKind {
    /** Model what happens if a configuration key changes. */
    ConfigurationShift,

    /** Model a new data-sharing pipeline being introduced. */
    DataPipelineChange,

    /** Model a code deployment propagating through the peer network. */
    SoftwareDeployment,

    /** Model a security patch propagating through the peer network. */
    SecurityPatch,

    /**
     * Model how a confirmed runtime threat (from an `AnomalySignal`) would
     * propagate through the peer network if not contained. Built by
     * [ThreatPropagationScenario.fromAnomalySignal].
     */
    ThreatPropagation,
}

/**
 * Describes a single simulation scenario, including its kind, parameters, and
 * the number of simulation steps to run.
 */
data class SimulationScenario(
    val id: UUID,
    val kind: ScenarioKind,
    val description: String,
    /** Scenario-specific config. */
    val parameters: Map<String, String>,
    /** Simulation depth, default 10. */
    val stepCount: Int,
    val createdAt: Instant,
) {
    companion object {
        /**
         * Creates a new [SimulationScenario] with a generated id and the current
         * UTC timestamp.
         */
        fun create(
            kind: ScenarioKind,
            description: String,
            parameters: Map<String, String>? = null,
            steps: Int = 10,
        ): SimulationScenario = SimulationScenario(
            id = UUID.randomUUID(),
            kind = kind,
            description = description,
            parameters = parameters ?: emptyMap(),
            stepCount = steps,
            createdAt = Instant.now(),
        )
    }
}

// =====================================================================
// SimulationResult (SimulationResult.cs)
// =====================================================================

/** The overall health outcome of a simulation run. */
enum class SimulationOutcome {
    /** Health score is 0.8 or above; network is operating normally. */
    Healthy,

    /** Health score is between 0.5 and 0.8; performance may be reduced. */
    Degraded,

    /** Health score is between 0.2 and 0.5; service is significantly impaired. */
    Critical,

    /** Health score is below 0.2; state is indeterminate. */
    Unknown,
}

/**
 * Captures the outcome of a single simulation run, including health score,
 * human-readable findings, and recommended actions.
 */
data class SimulationResult(
    val scenarioId: UUID,
    val outcome: SimulationOutcome,
    /** 0.0–1.0; higher = healthier. */
    val healthScore: Float,
    /** Human-readable simulation findings. */
    val findings: List<String>,
    val recommendations: List<String>,
    val stepsRun: Int,
    val completedAt: Instant,
)

// =====================================================================
// ISimulationEngine (ISimulationEngine.cs)
// =====================================================================

/**
 * Runs a simulation scenario against a knowledge graph and returns a
 * [SimulationResult] describing the predicted health outcome.
 */
interface ISimulationEngine {
    /**
     * Executes the simulation. [graph] provides the network topology.
     */
    suspend fun run(scenario: SimulationScenario, graph: KnowledgeGraph): SimulationResult
}

// =====================================================================
// EpisodicGraphExtractor (EpisodicGraphExtractor.cs)
// =====================================================================

/**
 * Extracts a [KnowledgeGraph] from a list of [EpisodicEntry] records using
 * keyword and tag heuristics. Fully offline — no LLM dependency.
 *
 * Extraction rules applied, in order:
 *  1. Each entry becomes an "event" node (label = first 60 characters of
 *     userText).
 *  2. Each tag key becomes a "topic" node; an edge event → topic with relation
 *     "tagged_with" and weight 1.0 is added.
 *  3. appContext becomes an "app" node; an edge event → app with relation
 *     "occurred_in" and weight 1.0 is added.
 *  4. Consecutive entries within 1 hour are connected via a "followed_by" edge
 *     with weight 0.5.
 *
 * Verification: Reference-level — heuristic extractor only, no LLM-grounded
 * entity resolution. The shape of the graph is correct but the choice of
 * nodes/edges is not yet validated against a labelled corpus.
 */
class EpisodicGraphExtractor : IGraphBuilder {
    override fun build(entries: List<EpisodicEntry>): KnowledgeGraph {
        val graph = KnowledgeGraph()
        val appNodes = HashMap<String, GraphNode>()
        val topicNodes = HashMap<String, GraphNode>()
        var prev: GraphNode? = null
        var prevTime: Instant = Instant.MIN

        for (entry in entries.sortedBy { it.recordedAtUtc }) {
            val label = if (entry.userText.length > 60) entry.userText.substring(0, 60) else entry.userText
            val evNode = GraphNode.create(
                label,
                "event",
                mapOf("episode_id" to entry.id),
            )
            graph.addNode(evNode)

            // App context → node + edge
            val app = entry.appContext
            if (!app.isNullOrBlank()) {
                val key = app.lowercase(Locale.ROOT)
                val appNode = appNodes.getOrPut(key) {
                    GraphNode.create(app, "app").also { graph.addNode(it) }
                }
                graph.addEdge(GraphEdge.create(evNode.id, appNode.id, "occurred_in"))
            }

            // Tags → topic nodes + edges
            entry.tags?.let { tags ->
                for (tag in tags.keys) {
                    val key = tag.lowercase(Locale.ROOT)
                    val topicNode = topicNodes.getOrPut(key) {
                        GraphNode.create(tag, "topic").also { graph.addNode(it) }
                    }
                    graph.addEdge(GraphEdge.create(evNode.id, topicNode.id, "tagged_with"))
                }
            }

            // Temporal sequence — connect to previous event if within 1 hour
            val p = prev
            if (p != null && Duration.between(prevTime, entry.recordedAtUtc).toMinutes() <= 60L) {
                graph.addEdge(GraphEdge.create(p.id, evNode.id, "followed_by", 0.5f))
            }

            prev = evNode
            prevTime = entry.recordedAtUtc
        }

        return graph
    }
}

// =====================================================================
// LocalSimulationEngine (NetworkHealthSimulator.cs — internal default engine)
// =====================================================================

/**
 * Deterministic graph-diffusion engine used when no external MiroFish engine is
 * registered. For internal use only.
 */
internal class LocalSimulationEngine : ISimulationEngine {
    override suspend fun run(scenario: SimulationScenario, graph: KnowledgeGraph): SimulationResult {
        currentCoroutineContext().ensureActive()

        var health = 1.0f
        val highImpact = LinkedHashSet<String>()

        var step = 0
        while (step < scenario.stepCount && health > 0f) {
            for (edge in graph.edges.values) {
                health -= (1f - edge.weight) * DECAY_PER_STEP

                if (edge.weight >= HIGH_IMPACT_THRESHOLD) {
                    graph.nodes[edge.sourceId]?.let { highImpact.add(it.label) }
                }
            }
            currentCoroutineContext().ensureActive()
            step++
        }

        health = health.coerceIn(0f, 1f)

        val outcome = when {
            health >= 0.8f -> SimulationOutcome.Healthy
            health >= 0.5f -> SimulationOutcome.Degraded
            health >= 0.2f -> SimulationOutcome.Critical
            else -> SimulationOutcome.Unknown
        }

        val findings: List<String> = if (highImpact.isNotEmpty()) {
            highImpact.map { "High-impact node detected: $it" }
        } else {
            listOf("No high-impact nodes detected.")
        }

        val recs: List<String> = if (
            outcome == SimulationOutcome.Degraded || outcome == SimulationOutcome.Critical
        ) {
            listOf(
                "Review high-weight edges before deployment.",
                "Consider incremental rollout.",
            )
        } else {
            listOf("Network health nominal — proceed with deployment.")
        }

        return SimulationResult(
            scenarioId = scenario.id,
            outcome = outcome,
            healthScore = health,
            findings = findings,
            recommendations = recs,
            stepsRun = scenario.stepCount,
            completedAt = Instant.now(),
        )
    }

    private companion object {
        const val DECAY_PER_STEP = 0.01f
        const val HIGH_IMPACT_THRESHOLD = 0.7f
    }
}

// =====================================================================
// MiroFishAdapter (MiroFishAdapter.cs)
// =====================================================================

/**
 * Adapter for the MiroFish GraphRAG simulation engine. When a real MiroFish
 * engine is registered it is preferred; otherwise falls back to
 * [LocalSimulationEngine].
 *
 * Verification: Reference-level — the fall-back local engine is deterministic
 * and tested, but no real MiroFish engine has yet been wired through this
 * adapter in a production run.
 */
class MiroFishAdapter(externalEngine: ISimulationEngine? = null) : ISimulationEngine {
    private val inner: ISimulationEngine = externalEngine ?: LocalSimulationEngine()

    override suspend fun run(scenario: SimulationScenario, graph: KnowledgeGraph): SimulationResult =
        inner.run(scenario, graph)
}

// =====================================================================
// NetworkHealthSimulator (NetworkHealthSimulator.cs)
// =====================================================================

/**
 * Offline network health simulator. Extracts a knowledge graph from episodic
 * memory, then runs a deterministic diffusion model to forecast the health
 * impact of the given scenario on the peer network.
 *
 * Verification: Reference-level — the diffusion math is deterministic and
 * unit-tested in-process, but no end-to-end wire-proven run has been executed
 * against a populated peer graph in production. Consumers should treat output
 * as advisory until that bar is met.
 */
class NetworkHealthSimulator(
    extractor: IGraphBuilder? = null,
    engine: ISimulationEngine? = null,
) {
    private val extractor: IGraphBuilder = extractor ?: EpisodicGraphExtractor()
    private val engine: ISimulationEngine = engine ?: MiroFishAdapter()

    /**
     * Builds a knowledge graph from [history] and runs the given [scenario]
     * through the simulation engine.
     */
    suspend fun forecast(
        history: List<EpisodicEntry>,
        scenario: SimulationScenario,
    ): SimulationResult {
        val graph = extractor.build(history)
        return engine.run(scenario, graph)
    }
}

// =====================================================================
// ThreatPropagationScenario (ThreatPropagationScenario.cs)
// =====================================================================

/**
 * Factory for building [SimulationScenario] instances of
 * [ScenarioKind.ThreatPropagation] from an [AnomalySignal].
 *
 * This is the Simulation ↔ Security integration point. It lives here so that
 * the SDK's simulation surface stays Security-aware without Security needing to
 * know about Simulation.
 *
 * Verification: Reference-level — depth + spread constants are heuristic and
 * not yet calibrated against observed propagation curves on a live peer mesh.
 */
object ThreatPropagationScenario {
    /**
     * Number of diffusion steps the simulator should run for a given
     * [ThreatVector]. Higher-severity vectors warrant deeper simulation depth
     * to surface long-range pivot risk.
     */
    private fun stepCountFor(vector: ThreatVector): Int = when (vector) {
        ThreatVector.NetworkPivot -> 30
        ThreatVector.ControlFlowDrift -> 25
        ThreatVector.PrivilegeEscalation -> 25
        ThreatVector.StateCorruption -> 20
        ThreatVector.MemoryAnomaly -> 15
        ThreatVector.AgentPatchRejected -> 15
        ThreatVector.BiometricSpoofAttempt -> 12
        ThreatVector.Unknown -> 10
    }

    /**
     * Creates a [SimulationScenario] describing how the threat described by
     * [signal] would propagate through the peer network if unmitigated.
     *
     * @param signal The confirmed anomaly to model. Higher
     *   [AnomalySignal.confidence] values produce more aggressive simulation
     *   parameters.
     * @param stepOverride Optional explicit step count. When `null` the step
     *   count is derived from the threat vector via [stepCountFor].
     */
    fun fromAnomalySignal(signal: AnomalySignal, stepOverride: Int? = null): SimulationScenario {
        val parameters = LinkedHashMap<String, String>(signal.evidence)
        parameters["signal_id"] = signal.id.toString()
        parameters["vector"] = signal.vector.toString()
        parameters["confidence"] = String.format(Locale.ROOT, "%.3f", signal.confidence)
        parameters["affected_module"] = signal.affectedModule
        parameters["detected_at"] = signal.detectedAt.toString()

        val steps = stepOverride ?: stepCountFor(signal.vector)
        val confidencePct = (signal.confidence * 100f).toInt()

        return SimulationScenario(
            id = UUID.randomUUID(),
            kind = ScenarioKind.ThreatPropagation,
            description = "threat-propagation: ${signal.vector} in ${signal.affectedModule} " +
                "(confidence $confidencePct%)",
            parameters = parameters,
            stepCount = steps,
            createdAt = Instant.now(),
        )
    }
}
