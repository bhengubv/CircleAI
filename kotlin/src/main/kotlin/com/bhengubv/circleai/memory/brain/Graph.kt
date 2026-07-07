// Graph.kt
//
// Personal knowledge graph + HippoRAG multi-hop recall (Personalised PageRank).
//
// Kotlin port of Circle.AI.Domain (MemoryItem / MemoryHit / IHippoRagStore) and
// Circle.AI.Companion (SqliteKnowledgeGraph, SqliteHippoRagStore) — the C#
// reference — mirroring the TypeScript pilot (memory/graph.ts) and the Go port
// (memory_graph.go) 1:1. This is the in-memory port: identical algorithms, no
// SQLite.
//
// HippoRAG (Wang et al. 2024): each memory item is a node in the personal KG;
// at recall time the query's entities seed a Personalised PageRank walk, and the
// nodes with the highest steady-state probability are the multi-hop matches.

package com.bhengubv.circleai.memory.brain

import java.time.Instant

// ---------------------------------------------------------------------------
// Shared recall currency (Circle.AI.Domain Contracts)
// ---------------------------------------------------------------------------

/** One recallable memory with optional string metadata. */
data class MemoryItem(
    val id: String,
    val text: String,
    val metadata: Map<String, String>? = null,
)

/** A recalled memory paired with its relevance score. */
data class MemoryHit(
    val item: MemoryItem,
    val score: Float,
)

// ---------------------------------------------------------------------------
// Knowledge graph node + triple
// ---------------------------------------------------------------------------

/** A node in the personal knowledge graph. */
data class KnowledgeNode(
    val id: String,
    val kind: String,
    val name: String,
    val properties: Map<String, String>? = null,
)

/** One (subject, predicate, object) triple with provenance (source + confidence). */
data class KnowledgeTriple(
    val subject: String,
    val predicate: String,
    val obj: String,
    val source: String?,
    val confidence: Float,
    val recordedAtUtc: Instant,
)

/** HippoRAG-pattern memory + knowledge-graph + Personalised PageRank recall. */
interface IHippoRagStore {
    val backendId: String

    /** Ensures the memory item exists as a node the walker can land on. */
    suspend fun indexAsync(item: MemoryItem)

    /** Seeds a Personalised PageRank walk from the query's terms; returns topK reached nodes. */
    suspend fun multiHopRecallAsync(query: String, topK: Int = 5): List<MemoryHit>
}

// ---------------------------------------------------------------------------
// InMemoryKnowledgeGraph
// ---------------------------------------------------------------------------

/**
 * In-memory personal knowledge graph. Triples are keyed by (subject, predicate,
 * object) — re-adding the same triple replaces its provenance, matching the C#
 * SQLite store's `INSERT OR REPLACE` on the composite primary key.
 *
 * Thread-safe: writes from the encoder's background drain and reads from a graph
 * walk are serialised on a single monitor, matching the C# store's locking and
 * the Go port's mutex.
 */
class InMemoryKnowledgeGraph {
    private val lock = Any()
    private val nodes = LinkedHashMap<String, KnowledgeNode>()
    private val triples = LinkedHashMap<String, KnowledgeTriple>()

    /** Inserts or replaces a node by id. */
    fun upsertNode(node: KnowledgeNode) {
        require(node.id.isNotBlank()) { "node.id required" }
        synchronized(lock) { nodes[node.id] = node }
    }

    /** Returns the node with the given id, or null. */
    fun getNode(id: String): KnowledgeNode? = synchronized(lock) { nodes[id] }

    /** Adds (or replaces) a triple with full provenance. */
    fun addTriple(subject: String, predicate: String, obj: String, source: String?, confidence: Float) {
        require(subject.isNotBlank()) { "subject required" }
        require(predicate.isNotBlank()) { "predicate required" }
        require(obj.isNotBlank()) { "object required" }
        require(confidence in 0f..1f) { "confidence must be in [0,1]" }

        val key = "$subject $predicate $obj"
        synchronized(lock) {
            triples[key] = KnowledgeTriple(subject, predicate, obj, source, confidence, Instant.now())
        }
    }

    /** All triples — used by HippoRAG for the graph walk. */
    fun allTriples(): List<KnowledgeTriple> = synchronized(lock) { triples.values.toList() }

    /** Raw triples for one subject (inspection / debugging). */
    fun readTriples(subject: String): List<KnowledgeTriple> {
        require(subject.isNotBlank()) { "subject required" }
        return synchronized(lock) { triples.values.filter { it.subject == subject } }
    }
}

// ---------------------------------------------------------------------------
// InMemoryHippoRagStore — Personalised PageRank multi-hop recall
// ---------------------------------------------------------------------------

/**
 * Real HippoRAG recall over an [InMemoryKnowledgeGraph]. Walks the personal
 * graph via Personalised PageRank (power iteration) seeded from the query's terms.
 *
 * Three precision guarantees carried from the C# reference:
 *   1. No query term touches the graph → returns empty (never fabricates an
 *      association from arbitrary nodes).
 *   2. Seed nodes are excluded from results (recall returns the *associated*
 *      nodes the walk reached, not the query echoed back).
 *   3. Edge spread is confidence-weighted — a high-confidence edge carries more
 *      of the walk's mass than a guessed one, so a shaky belief does not steer
 *      recall like a stated fact.
 */
class InMemoryHippoRagStore(
    private val kg: InMemoryKnowledgeGraph,
    private val walkIterations: Int = 32,
    private val damping: Double = 0.85,
) : IHippoRagStore {

    override val backendId: String get() = "inmemory-hippo-ppr"

    override suspend fun indexAsync(item: MemoryItem) {
        // The graph is populated by the KnowledgeGraphExtractor — here we just ensure
        // the memory item exists as a node so the walker can land on it.
        kg.addTriple(item.id, "memory_text", item.text, item.id, 1.0f)
        item.metadata?.forEach { (k, v) ->
            kg.addTriple(item.id, k, v, item.id, 0.9f)
        }
    }

    override suspend fun multiHopRecallAsync(query: String, topK: Int): List<MemoryHit> {
        require(query.isNotBlank()) { "query required" }
        require(topK > 0) { "topK must be positive" }

        val triples = kg.allTriples()
        if (triples.isEmpty()) return emptyList()

        // Adjacency list: subject -> [(object, confidence)].
        val outgoing = HashMap<String, MutableList<Pair<String, Float>>>()
        val allNodes = LinkedHashSet<String>()
        for (t in triples) {
            allNodes.add(t.subject)
            allNodes.add(t.obj)
            outgoing.getOrPut(t.subject) { mutableListOf() }.add(t.obj to t.confidence)
        }

        // Seed the personalisation vector from query terms that appear as nodes.
        val queryTerms = query.split(Regex("[^A-Za-z0-9]+"))
            .filter { it.isNotEmpty() }
            .map { it.lowercase() }
            .toHashSet()
        val seedNodes = allNodes.filter { queryTerms.contains(it.lowercase()) }
        // Precision guarantee 1: no genuine association → return nothing.
        if (seedNodes.isEmpty()) return emptyList()

        var rank = HashMap<String, Double>()
        for (n in allNodes) rank[n] = 0.0
        val seedMass = 1.0 / seedNodes.size
        for (s in seedNodes) rank[s] = seedMass

        // Power-iteration Personalised PageRank.
        repeat(walkIterations) {
            val next = HashMap<String, Double>()
            for (n in allNodes) next[n] = 0.0

            // Random-jump component (personalisation): mass returns to the seeds.
            for (seed in seedNodes) {
                next[seed] = (next[seed] ?: 0.0) + (1 - damping) * seedMass
            }

            // Walk component.
            for ((node, mass) in rank) {
                if (mass <= 0) continue
                val nbrs = outgoing[node]
                if (nbrs == null || nbrs.isEmpty()) {
                    // Dangling node: redistribute via personalisation.
                    for (seed in seedNodes) {
                        next[seed] = (next[seed] ?: 0.0) + (damping * mass) / seedNodes.size
                    }
                    continue
                }
                // Precision guarantee 3: confidence-weighted spread. With equal
                // confidences this reduces to the plain 1/count split.
                var totalConf = 0f
                for ((_, conf) in nbrs) totalConf += conf
                for ((nbr, conf) in nbrs) {
                    val weight = if (totalConf > 0f) conf.toDouble() / totalConf else 1.0 / nbrs.size
                    next[nbr] = (next[nbr] ?: 0.0) + damping * mass * weight
                }
            }

            rank = next
        }

        // Precision guarantee 2: exclude the seeds — they are the query's own terms.
        val seedSet = seedNodes.toHashSet()
        return rank.entries
            .filter { it.value > 0 && !seedSet.contains(it.key) }
            .sortedByDescending { it.value }
            .take(topK)
            .map { (key, value) ->
                val node = kg.getNode(key)
                val item = MemoryItem(
                    id = key,
                    text = node?.name ?: key,
                    metadata = node?.properties,
                )
                MemoryHit(item, value.toFloat())
            }
    }
}
