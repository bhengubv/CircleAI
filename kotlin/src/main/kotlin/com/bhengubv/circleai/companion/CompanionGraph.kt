// CompanionGraph.kt
//
// The personal knowledge graph on disk, multi-hop recall over it, and the seam
// that puts a Neuron brain behind the voice loop.
//
// Ported from src/CircleAI.Companion/{SqliteKnowledgeGraph, HippoRagStore,
// NeuronVoice}.cs.

package com.bhengubv.circleai.companion

import java.sql.Connection
import java.sql.DriverManager
import java.time.Instant
import java.util.Locale

/**
 * Triples in SQLite, so what the assistant learned about somebody survives a
 * restart.
 *
 * Keyed on (subject, predicate, object) so re-stating a fact UPDATES its
 * confidence rather than adding a duplicate edge. Without that the PageRank walk
 * below is skewed by nothing more than how often a fact happened to be repeated
 * in conversation.
 *
 * The JDBC driver is supplied by the host — this package declares no driver
 * dependency, exactly as the other SQLite stores do.
 */
class SqliteKnowledgeGraph(private val db: Connection) : AutoCloseable {

    constructor(databasePath: String) : this(DriverManager.getConnection("jdbc:sqlite:$databasePath"))

    init {
        db.createStatement().use { st ->
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS kg_nodes (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    name TEXT NOT NULL,
                    properties TEXT
                )
                """.trimIndent()
            )
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS kg_triples (
                    subject TEXT NOT NULL,
                    predicate TEXT NOT NULL,
                    object TEXT NOT NULL,
                    source TEXT,
                    confidence REAL NOT NULL,
                    recorded_at TEXT NOT NULL,
                    PRIMARY KEY (subject, predicate, object)
                )
                """.trimIndent()
            )
            st.executeUpdate("CREATE INDEX IF NOT EXISTS ix_kg_triples_subject ON kg_triples(subject)")
        }
    }

    @Synchronized
    fun upsertNode(node: KnowledgeNode) {
        db.prepareStatement(
            """
            INSERT INTO kg_nodes (id, kind, name, properties) VALUES (?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, name = excluded.name,
                                          properties = excluded.properties
            """.trimIndent()
        ).use { ps ->
            ps.setString(1, node.id)
            ps.setString(2, node.kind)
            ps.setString(3, node.name)
            ps.setString(4, node.properties?.entries?.joinToString(",") { "${it.key}=${it.value}" })
            ps.executeUpdate()
        }
    }

    @Synchronized
    fun getNode(id: String): KnowledgeNode? =
        db.prepareStatement("SELECT id, kind, name, properties FROM kg_nodes WHERE id = ?").use { ps ->
            ps.setString(1, id)
            ps.executeQuery().use { rs ->
                if (!rs.next()) return null
                val props = rs.getString(4)?.split(",")
                    ?.mapNotNull { it.split("=", limit = 2).takeIf { p -> p.size == 2 } }
                    ?.associate { it[0] to it[1] }
                KnowledgeNode(rs.getString(1), rs.getString(2), rs.getString(3), props)
            }
        }

    @Synchronized
    fun addTriple(
        subject: String, predicate: String, `object`: String,
        source: String? = null, confidence: Float = 1.0f, recordedAt: Instant = Instant.now()
    ) {
        db.prepareStatement(
            """
            INSERT INTO kg_triples (subject, predicate, object, source, confidence, recorded_at)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(subject, predicate, object) DO UPDATE SET
                source = excluded.source,
                confidence = excluded.confidence,
                recorded_at = excluded.recorded_at
            """.trimIndent()
        ).use { ps ->
            ps.setString(1, subject)
            ps.setString(2, predicate)
            ps.setString(3, `object`)
            ps.setString(4, source)
            ps.setDouble(5, confidence.toDouble())
            ps.setString(6, recordedAt.toString())
            ps.executeUpdate()
        }
    }

    @Synchronized
    fun allTriples(): List<KnowledgeTriple> = readTriples(
        "SELECT subject, predicate, object, source, confidence, recorded_at " +
            "FROM kg_triples ORDER BY subject, predicate, object", null
    )

    @Synchronized
    fun readTriples(subject: String): List<KnowledgeTriple> = readTriples(
        "SELECT subject, predicate, object, source, confidence, recorded_at " +
            "FROM kg_triples WHERE subject = ? ORDER BY predicate, object", subject
    )

    private fun readTriples(sql: String, bind: String?): List<KnowledgeTriple> =
        db.prepareStatement(sql).use { ps ->
            if (bind != null) ps.setString(1, bind)
            ps.executeQuery().use { rs ->
                val out = ArrayList<KnowledgeTriple>()
                while (rs.next()) {
                    out.add(
                        KnowledgeTriple(
                            rs.getString(1), rs.getString(2), rs.getString(3),
                            rs.getString(4), rs.getDouble(5).toFloat(),
                            runCatching { Instant.parse(rs.getString(6)) }.getOrDefault(Instant.EPOCH)
                        )
                    )
                }
                out
            }
        }

    override fun close() = db.close()
}

/**
 * Personalised PageRank over the knowledge graph.
 *
 * The HippoRAG idea (Wang et al. 2024): a query's own terms SEED a random walk,
 * and whatever the walk pools mass on is what the query is associated with —
 * including things no single edge connects it to. That is the "multi-hop" part,
 * and it is what a plain similarity search cannot do.
 */
class SqliteHippoRagStore(
    private val graph: SqliteKnowledgeGraph,
    private val walkIterations: Int = 32,
    private val damping: Double = 0.85
) : IHippoRagStore {

    override val backendId: String get() = "sqlite-hippo-ppr"

    override suspend fun index(item: MemoryItem) {
        // The graph is populated by the extractor; this only ensures the item
        // exists as a NODE so the walker has somewhere to land on it.
        graph.addTriple(item.id, "memory_text", item.text, item.id, 1.0f)
        item.metadata?.toSortedMap()?.forEach { (k, v) ->
            graph.addTriple(item.id, k, v, item.id, 0.9f)
        }
    }

    override suspend fun multiHopRecall(query: String, topK: Int): List<MemoryHit> {
        require(query.isNotBlank()) { "A query is required." }
        require(topK > 0) { "topK must be greater than zero." }

        val triples = graph.allTriples()
        if (triples.isEmpty()) return emptyList()

        val outgoing = HashMap<String, MutableList<Pair<String, Float>>>()
        val allNodes = LinkedHashSet<String>()
        for (t in triples) {
            allNodes.add(t.subject); allNodes.add(t.`object`)
            outgoing.getOrPut(t.subject) { ArrayList() }.add(t.`object` to t.confidence)
        }

        val queryTerms = terms(query).toSet()
        val seeds = allNodes.filter { it.lowercase(Locale.ROOT) in queryTerms }.sorted()

        // NO QUERY TERM TOUCHES THE GRAPH, so there is no genuine association.
        // Return nothing rather than fabricating one from arbitrary nodes — the
        // episodic path already covers recency and similarity, and noise here is
        // worse than silence.
        if (seeds.isEmpty()) return emptyList()

        val seedMass = 1.0 / seeds.size
        var rank = allNodes.associateWith { 0.0 }.toMutableMap()
        seeds.forEach { rank[it] = seedMass }

        repeat(walkIterations) {
            val next = allNodes.associateWith { 0.0 }.toMutableMap()

            // The random-jump component: the walk always falls back to the
            // query's own terms, which is what makes this PERSONALISED rather
            // than global PageRank.
            seeds.forEach { next[it] = next.getValue(it) + (1 - damping) * seedMass }

            for ((node, mass) in rank) {
                if (mass <= 0) continue
                val neighbours = outgoing[node]
                if (neighbours.isNullOrEmpty()) {
                    // A dangling node would otherwise LOSE its mass and the
                    // ranking would stop summing to one.
                    seeds.forEach { next[it] = next.getValue(it) + damping * mass / seeds.size }
                    continue
                }
                // CONFIDENCE-WEIGHTED spread: a high-confidence edge carries more
                // of the walk than a guessed one, so a shaky belief does not
                // steer recall like a stated fact.
                val total = neighbours.sumOf { it.second.toDouble() }
                for ((neighbour, confidence) in neighbours) {
                    val weight = if (total > 0) confidence / total else 1.0 / neighbours.size
                    next[neighbour] = next.getValue(neighbour) + damping * mass * weight
                }
            }
            rank = next
        }

        // THE SEEDS ARE THE QUERY'S OWN TERMS and are not recalled memories.
        // Excluding them is what makes this return what the walk reached rather
        // than the question echoed back.
        val seedSet = seeds.toSet()
        return rank.entries
            .filter { it.value > 0 && it.key !in seedSet }
            // Ties break by node id so repeated recalls of the same graph return
            // the same order — a caller taking the top 1 must reproduce it.
            .sortedWith(compareByDescending<Map.Entry<String, Double>> { it.value }
                .thenBy { it.key })
            .take(topK)
            .map { MemoryHit(MemoryItem(it.key, textFor(it.key)), it.value) }
    }

    /** The stored text for a node, or the node id when it is an entity rather
     *  than a memory. Never empty: a hit with no text is a row nobody can show. */
    private fun textFor(node: String): String =
        graph.readTriples(node).firstOrNull { it.predicate == "memory_text" }?.`object` ?: node

    private fun terms(text: String): List<String> =
        text.lowercase(Locale.ROOT).split(Regex("[^a-z0-9]+")).filter { it.isNotEmpty() }
}

/**
 * Composition seam: build a companion session over a brain, then hand it to the
 * voice listener.
 *
 * The point is that NO NEW VOICE LOGIC exists here. Routing the loop through a
 * session rather than straight at the brain is what makes the Neuron's routing,
 * residency, memory and persona apply to a spoken turn exactly as they do to a
 * typed one — otherwise the assistant knows you when you type and forgets you
 * when you speak.
 */
object NeuronVoice {

    fun createListener(
        pipeline: IVoicePipeline,
        session: ICompanionSession
    ): VoiceCompanionListener = VoiceCompanionListener(pipeline, session)
}
