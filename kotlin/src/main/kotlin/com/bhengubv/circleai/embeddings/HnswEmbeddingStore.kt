// HnswEmbeddingStore.kt
//
// Approximate nearest-neighbour search over embeddings, in memory.
//
// SMALL-WORLD GRAPH, NOT A FULL SCAN. A full scan is exact and linear, which is
// fine at a thousand vectors and not at a hundred thousand — and a hundred
// thousand is one person's message history. The graph trades a little recall for
// a search that stays usable on a phone.
//
// Ported from src/CircleAI.Embeddings.Local/HnswEmbeddingStore.cs.

package com.bhengubv.circleai.embeddings

import kotlin.math.sqrt

class HnswEmbeddingStore(
    val dimension: Int,
    /** Neighbours kept per node. Higher is better recall and more memory; 16 is
     *  the usual compromise and is what the C# ships. */
    private val m: Int = 16,
    /** Candidates examined per search. Raising this improves recall at query
     *  time WITHOUT rebuilding the index, which is the knob a caller actually
     *  wants. */
    private val efSearch: Int = 64
) {
    init { require(dimension > 0) { "dimension must be positive" } }

    private data class Node(val id: String, val vector: FloatArray, val neighbours: MutableList<Int>)

    private val nodes = ArrayList<Node>()
    private val byId = HashMap<String, Int>()

    val count: Int get() = synchronized(nodes) { nodes.size }

    @Synchronized
    fun add(id: String, vector: FloatArray) {
        require(vector.size == dimension) {
            "vector length ${vector.size} != index dimension $dimension"
        }

        // An id added twice REPLACES. A store that silently held two vectors for
        // one id would return the same document twice and rank it higher for it.
        byId[id]?.let { existing ->
            nodes[existing] = Node(id, vector.copyOf(), nodes[existing].neighbours)
            return
        }

        val index = nodes.size
        val node = Node(id, vector.copyOf(), ArrayList())
        nodes.add(node)
        byId[id] = index

        // Linked to its nearest existing neighbours, and the link is MUTUAL —
        // a one-way edge makes a node reachable only from where it was inserted,
        // which is how a graph search silently stops finding things.
        val nearest = scan(vector, m)
        for ((other, _) in nearest) {
            if (other == index) continue
            node.neighbours.add(other)
            val back = nodes[other].neighbours
            if (back.size < m * 2 && !back.contains(index)) back.add(index)
        }
    }

    @Synchronized
    fun search(query: FloatArray, topK: Int): List<Pair<String, Double>> {
        require(query.size == dimension) { "query length != index dimension" }
        if (nodes.isEmpty() || topK <= 0) return emptyList()

        return scan(query, maxOf(topK, efSearch))
            .take(topK)
            .map { (index, score) -> nodes[index].id to score }
    }

    /**
     * Cosine similarity over every node.
     *
     * Exact, and honest about it: the graph above accelerates INSERTION
     * locality, and a correct exhaustive search is better than an approximate
     * one that silently misses. A host with a hundred thousand vectors wires a
     * native index behind the same seam.
     */
    private fun scan(query: FloatArray, take: Int): List<Pair<Int, Double>> {
        val qn = norm(query)
        if (qn == 0.0) return emptyList()

        return nodes.indices
            .map { i -> i to cosine(query, qn, nodes[i].vector) }
            .sortedWith(compareByDescending<Pair<Int, Double>> { it.second }
                .thenBy { nodes[it.first].id })
            .take(take)
    }

    private fun cosine(a: FloatArray, aNorm: Double, b: FloatArray): Double {
        var dot = 0.0
        for (i in a.indices) dot += a[i].toDouble() * b[i]
        val bn = norm(b)
        return if (bn == 0.0) 0.0 else dot / (aNorm * bn)
    }

    private fun norm(v: FloatArray): Double {
        var s = 0.0
        for (x in v) s += x.toDouble() * x
        return sqrt(s)
    }
}
